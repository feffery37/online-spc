using Spc.Application;
using Spc.Domain;
using Spc.Infrastructure;
using SqlSugar;
using Xunit;

namespace Spc.Tests;

/// <summary>M4 集成测试：真实 SQLite 库 + 冻结版本 + 判异 + MES 模拟器，验证 A-07 / A-08 全闭环。</summary>
public sealed class DispositionIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqlSugarScope _db;
    private readonly SpcRepository _repo;
    private readonly FakeMesGateway _mes = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly DispositionService _disposition;
    private readonly EvaluationService _evaluation;
    private readonly CancellationToken _ct = CancellationToken.None;

    public DispositionIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spc-m4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = SpcDatabase.Create("Sqlite", $"Data Source={Path.Combine(_dir, "spc.db")}");
        SpcDatabase.Initialize(_db, "Sqlite", AppContext.BaseDirectory);
        _repo = new SpcRepository(_db);
        var reader = new FrozenLimitReader(_repo);
        _disposition = new DispositionService(_repo, _mes, _publisher, reader);
        _evaluation = new EvaluationService(_repo, reader, _publisher, _disposition);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<long> NewChartAsync()
        => await _repo.InsertChartAsync(new SpcChart
        {
            Code = "CH-M4", Name = "M4 test", ChartType = (int)ChartType.XbarR,
            SubgroupSize = 5, Usl = 10.1, Lsl = 9.9, Target = 10.0, Unit = "mm",
            Machine = "M-01", Product = "P", Process = "PR", Recipe = "RCP",
            RuleSetVer = "WECO-8-V1", IsEnabled = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }, _ct);

    private async Task<long> NewActiveVersionAsync(long chartId)
        => await _repo.InsertLimitVersionAsync(new SpcLimitVersion
        {
            ChartId = chartId, VersionNo = 1, ChartType = (int)ChartType.XbarR,
            Cl = 10.0, Ucl = 10.3, Lcl = 9.7, WarnUcl = 10.2, WarnLcl = 9.8,
            VarCl = 0.2, VarUcl = 0.42, VarLcl = 0.0, SigmaHat = 0.086,
            SubgroupSize = 5, SubgroupCount = 25,
            DataFrom = DateTime.UtcNow.AddDays(-2), DataTo = DateTime.UtcNow.AddDays(-1),
            DataHash = new string('c', 64), SubgroupIds = "[]",
            RuleSetVer = "WECO-8-V1", CalcParams = "test", Iterations = 1,
            ChangeReason = "test", CreatedBy = "qa", ApprovedBy = "qe.li",
            Status = (int)LimitVersionStatus.Active,
            EffectiveFrom = DateTime.UtcNow.AddHours(-2), ExpiredTime = null,
            CreatedAt = DateTime.UtcNow
        }, _ct);

    private async Task<long> SeedSubgroupAsync(long chartId, long seq, double xbar, DateTime? at = null)
    {
        var eventTime = at ?? DateTime.UtcNow.AddMinutes(-30).AddMinutes(seq);
        var sg = new SpcSubgroup
        {
            ChartId = chartId, Seq = seq,
            EventTimeFrom = eventTime, EventTimeTo = eventTime,
            SampleCount = 5, Xbar = xbar, RangeVal = 0.2, StddevVal = 0, MrVal = 0,
            Status = (int)SubgroupStatus.Closed,
            ClosedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        await _repo.InsertSubgroupAsync(sg, _ct);
        var measureId = await _repo.InsertMeasureAsync(new SpcMeasure
        {
            ChartId = chartId, EventTime = eventTime, MeasureValue = xbar,
            LotId = "LOT-M4", Sn = $"SN-M4-{seq}", WorkOrder = "WO-M4",
            Shift = "A", Recipe = "RCP", Equipment = "M-01",
            ProcTag = "{\"temperature\":22.5}", Source = (int)MeasureSource.Simulator,
            DedupKey = $"dk-m4-{seq}", IngestTime = DateTime.UtcNow
        }, _ct);
        await _repo.AssignSubgroupAsync([measureId], sg.Id, _ct);
        return sg.Id;
    }

    /// <summary>完整链路：判异 → OCAP → Hold，返回 ruleOutId。</summary>
    private async Task<long> DetectAsync(long chartId, long seq = 1, double xbar = 10.5)
    {
        long sg = await SeedSubgroupAsync(chartId, seq, xbar);
        var outcome = await _evaluation.EvaluateAsync(chartId, sg, _ct);
        Assert.True(outcome.Evaluated, outcome.Reason);
        var hit = Assert.Single(outcome.RuleOuts,
            r => r.RuleCode == (int)RuleCode.R1);
        return hit.Id;
    }

    private async Task AckAllAsync(long ruleOutId)
    {
        var actions = await _repo.GetMesActionsByRuleOutAsync(ruleOutId, _ct);
        foreach (var a in actions)
        {
            var result = await _disposition.CallbackAsync(new MesCallbackRequest
            {
                RuleOutId = ruleOutId, MesRef = a.MesRef, Status = "ACKED",
                Operator = "mes.test", LotStatus = "HELD", EquipmentStatus = "LOCKED"
            }, _ct);
            Assert.True(result.Ok, result.Error);
        }
    }

    [Fact]
    public async Task A07_EveryRuleOutGetsOcapAndMesActions()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long roId = await DetectAsync(chart);

        var ocap = await _repo.GetOcapByRuleOutAsync(roId, _ct);
        Assert.NotNull(ocap);
        Assert.StartsWith("OCAP-", ocap.OcapNo);
        Assert.Equal((int)OcapStatus.ActionSent, ocap.Status);

        var ro = await _repo.GetRuleOutAsync(roId, _ct);
        Assert.Equal((int)OcapStatus.ActionSent, ro!.OcapStatus);

        var actions = await _repo.GetMesActionsByRuleOutAsync(roId, _ct);
        Assert.Contains(actions, a => a.ActionType == (int)MesActionType.LotHold);
        Assert.Contains(actions, a => a.ActionType == (int)MesActionType.StopLine);
        Assert.Contains(actions, a => a.ActionType == (int)MesActionType.Notify);
        Assert.All(actions, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.MesRef));
            Assert.Equal((int)MesActionStatus.Sent, a.Status);
        });
        Assert.NotEmpty(_mes.Sent);
        Assert.NotEmpty(_publisher.Dispositions);
    }

    [Fact]
    public async Task Callback_AllAcked_AdvancesToMesAcked()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long roId = await DetectAsync(chart);
        await AckAllAsync(roId);

        var ocap = await _repo.GetOcapByRuleOutAsync(roId, _ct);
        var ro = await _repo.GetRuleOutAsync(roId, _ct);
        Assert.Equal((int)OcapStatus.MesAcked, ocap!.Status);
        Assert.Equal((int)OcapStatus.MesAcked, ro!.OcapStatus);
        Assert.All(await _repo.GetMesActionsByRuleOutAsync(roId, _ct),
            a => Assert.Equal((int)MesActionStatus.Acked, a.Status));
    }

    [Fact]
    public async Task Review_ThenVerify_ClosesOcap()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long roId = await DetectAsync(chart);
        await AckAllAsync(roId);

        var ocap = await _repo.GetOcapByRuleOutAsync(roId, _ct);
        var review = await _disposition.ReviewAsync(new OcapReviewRequest
        {
            OcapId = ocap!.Id,
            Cause = "刀具磨损导致均值漂移",
            Countermeasure = "换刀并重新对刀，首件检验合格",
            Decision = "返工",
            SignedBy = "qe.li"
        }, _ct);
        Assert.True(review.Ok, review.Error);
        Assert.Equal((int)OcapStatus.InReview, review.Status);

        long verification = await SeedSubgroupAsync(chart, 2, 10.0, DateTime.UtcNow.AddMinutes(-1));
        var verify = await _disposition.VerifyAsync(new OcapVerifyRequest
        {
            OcapId = ocap.Id, VerificationSubgroupId = verification
        }, _ct);
        Assert.True(verify.Ok, verify.Error);
        Assert.Equal((int)OcapStatus.Closed, verify.Status);

        var closed = await _repo.GetOcapAsync(ocap.Id, _ct);
        Assert.Equal(verification, closed!.VerificationSubgroupId);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal("返工", closed.Decision);
        Assert.Equal((int)OcapStatus.Closed, (await _repo.GetRuleOutAsync(roId, _ct))!.OcapStatus);
    }

    [Fact]
    public async Task VerifyFails_EscalatesAndDoesNotClose()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long roId = await DetectAsync(chart);
        await AckAllAsync(roId);
        var ocap = await _repo.GetOcapByRuleOutAsync(roId, _ct);
        Assert.True((await _disposition.ReviewAsync(new OcapReviewRequest
        {
            OcapId = ocap!.Id, Cause = "待查", SignedBy = "qe.li"
        }, _ct)).Ok);

        long stillBad = await SeedSubgroupAsync(chart, 2, 10.6, DateTime.UtcNow.AddMinutes(-1));
        var verify = await _disposition.VerifyAsync(new OcapVerifyRequest
        {
            OcapId = ocap.Id, VerificationSubgroupId = stillBad
        }, _ct);

        Assert.True(verify.Ok, verify.Error);
        Assert.Equal((int)OcapStatus.Escalated, verify.Status);
        var escalated = await _repo.GetOcapAsync(ocap.Id, _ct);
        Assert.Null(escalated!.ClosedAt);
    }

    [Fact]
    public async Task Review_WithoutAllAcks_IsRejected()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long roId = await DetectAsync(chart);
        var actions = await _repo.GetMesActionsByRuleOutAsync(roId, _ct);
        Assert.True((await _disposition.CallbackAsync(new MesCallbackRequest
        {
            RuleOutId = roId, MesRef = actions[0].MesRef, Status = "ACKED", Operator = "mes.test"
        }, _ct)).Ok);

        var ocap = await _repo.GetOcapByRuleOutAsync(roId, _ct);
        var review = await _disposition.ReviewAsync(new OcapReviewRequest
        {
            OcapId = ocap!.Id, Cause = "原因", SignedBy = "qe.li"
        }, _ct);
        Assert.False(review.Ok);
        Assert.Contains("全部 MES 回执", review.Error);
    }

    [Fact]
    public async Task EqpHold_TriggeredByRepeatedRuleOuts()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        for (int i = 0; i < OocActionPolicy.EqpHoldThreshold; i++)
            await _repo.InsertRuleOutAsync(new SpcRuleOut
            {
                ChartId = chart, SubgroupId = 0, LimitVerId = 1,
                RuleCode = (int)RuleCode.R2, Severity = (int)Severity.Major,
                StatValue = 10.1, ZValue = 1.2, EventTime = DateTime.UtcNow.AddMinutes(-5),
                OcapStatus = (int)OcapStatus.Detected, LotId = "LOT-HIST", Sn = "SN-HIST",
                CreatedAt = DateTime.UtcNow
            }, _ct);

        long roId = await DetectAsync(chart);
        var actions = await _repo.GetMesActionsByRuleOutAsync(roId, _ct);
        Assert.Contains(actions, a => a.ActionType == (int)MesActionType.EqpHold);
    }

    [Fact]
    public async Task A08_TraceAnswersAllQuestionsAndReportsComplete()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long roId = await DetectAsync(chart);
        await AckAllAsync(roId);
        var ocap = await _repo.GetOcapByRuleOutAsync(roId, _ct);
        Assert.True((await _disposition.ReviewAsync(new OcapReviewRequest
        {
            OcapId = ocap!.Id, Cause = "量具漂移", Countermeasure = "重新校准",
            Decision = "让步接收", SignedBy = "qe.li"
        }, _ct)).Ok);
        long verification = await SeedSubgroupAsync(chart, 2, 10.0, DateTime.UtcNow.AddMinutes(-1));
        Assert.True((await _disposition.VerifyAsync(new OcapVerifyRequest
        {
            OcapId = ocap.Id, VerificationSubgroupId = verification
        }, _ct)).Ok);

        var trace = await _disposition.GetTraceAsync(roId, _ct);
        Assert.NotNull(trace);
        Assert.NotNull(trace.Ocap);
        Assert.NotNull(trace.LimitVersion);
        Assert.Equal("qe.li", trace.LimitVersion!.ApprovedBy);
        Assert.Equal(64, trace.LimitVersion.DataHash.Length);
        Assert.Equal("qe.li", trace.Ocap!.SignedBy);
        Assert.Equal("让步接收", trace.Ocap.Decision);
        Assert.All(trace.Actions, a => Assert.Equal((int)MesActionStatus.Acked, a.Status));
        Assert.Contains(trace.AuditEvents, e => e.EventType == "OCAP_CLOSED");
        Assert.Contains(trace.AuditEvents, e => e.EventType == "MES_CALLBACK");
        Assert.Contains(trace.AuditEvents, e => e.EventType == "RULE_OUT_DETECTED");
    }

    [Fact]
    public async Task DuplicateHandle_IsIdempotent()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long roId = await DetectAsync(chart);
        int actionsBefore = (await _repo.GetMesActionsByRuleOutAsync(roId, _ct)).Count;

        var again = await _disposition.HandleAsync(roId, _ct);

        Assert.True(again.Ok, again.Error);
        Assert.Contains("幂等", again.Note);
        Assert.Single(await _repo.GetOcapsAsync(chart, 50, _ct), o => o.RuleOutId == roId);
        Assert.Equal(actionsBefore, (await _repo.GetMesActionsByRuleOutAsync(roId, _ct)).Count);
    }

    private sealed class FakeMesGateway : IMesGateway
    {
        private long _seq;
        public List<MesHoldRequest> Sent { get; } = [];

        public Task<MesHoldReceipt> SendHoldAsync(MesHoldRequest request, CancellationToken ct)
        {
            Sent.Add(request);
            string mesRef = $"MES-TEST-{Interlocked.Increment(ref _seq):D6}";
            return Task.FromResult(new MesHoldReceipt(true, mesRef, "HELD", "mes.test", null, "{}"));
        }

        public Task<MesHoldReceipt?> QueryAsync(string mesRef, CancellationToken ct) =>
            Task.FromResult<MesHoldReceipt?>(null);

        public Task<MesHoldReceipt?> ReleaseAsync(string mesRef, string operatorName, CancellationToken ct) =>
            Task.FromResult<MesHoldReceipt?>(null);
    }

    private sealed class RecordingPublisher : IRealtimePublisher
    {
        public List<object> Dispositions { get; } = [];
        public Task PublishSubgroupAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishMeasureAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishNoticeAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishRuleOutAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishDispositionAsync(object payload, CancellationToken ct)
        {
            Dispositions.Add(payload);
            return Task.CompletedTask;
        }
    }
}
