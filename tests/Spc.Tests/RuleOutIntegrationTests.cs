using Spc.Application;
using Spc.Domain;
using Spc.Infrastructure;
using SqlSugar;
using Xunit;

namespace Spc.Tests;

/// <summary>
/// M3 集成测试：真实 SQLite 库 + 冻结版本 + 判异服务，验证只读契约门禁、
/// Rule Out 审计字段、规则集可插拔、去重与 A-06 实时性。
/// </summary>
public sealed class RuleOutIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqlSugarScope _db;
    private readonly SpcRepository _repo;
    private readonly RecordingPublisher _publisher = new();
    private readonly EvaluationService _service;
    private readonly CancellationToken _ct = CancellationToken.None;
    private readonly DateTime _base = DateTime.UtcNow.AddMinutes(-30);

    public RuleOutIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spc-m3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = SpcDatabase.Create("Sqlite", $"Data Source={Path.Combine(_dir, "spc.db")}");
        SpcDatabase.Initialize(_db, "Sqlite", AppContext.BaseDirectory);
        _repo = new SpcRepository(_db);
        var reader = new FrozenLimitReader(_repo);
        _service = new EvaluationService(_repo, reader, _publisher);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<long> NewChartAsync(string ruleSet = "WECO-8-V1")
        => await _repo.InsertChartAsync(new SpcChart
        {
            Code = "CH-M3", Name = "M3 test", ChartType = (int)ChartType.XbarR,
            SubgroupSize = 5, Usl = 10.1, Lsl = 9.9, Target = 10.0, Unit = "mm",
            Machine = "M-01", Product = "P", Process = "PR", Recipe = "RCP",
            RuleSetVer = ruleSet, IsEnabled = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }, _ct);

    /// <summary>冻结限：CL=10，UCL=10.3 → sigma_chart=0.1；变异图 VarCl=0.2。</summary>
    private async Task<long> NewActiveVersionAsync(long chartId)
    {
        var v = new SpcLimitVersion
        {
            ChartId = chartId, VersionNo = 1, ChartType = (int)ChartType.XbarR,
            Cl = 10.0, Ucl = 10.3, Lcl = 9.7, WarnUcl = 10.2, WarnLcl = 9.8,
            VarCl = 0.2, VarUcl = 0.42, VarLcl = 0.0, SigmaHat = 0.086,
            SubgroupSize = 5, SubgroupCount = 25,
            DataFrom = DateTime.UtcNow.AddDays(-2), DataTo = DateTime.UtcNow.AddDays(-1),
            DataHash = new string('b', 64), SubgroupIds = "[]",
            RuleSetVer = "WECO-8-V1", CalcParams = "test", Iterations = 1,
            ChangeReason = "test", CreatedBy = "qa", ApprovedBy = "qe.li",
            Status = (int)LimitVersionStatus.Active,
            EffectiveFrom = DateTime.UtcNow.AddHours(-1), ExpiredTime = null,
            CreatedAt = DateTime.UtcNow
        };
        return await _repo.InsertLimitVersionAsync(v, _ct);
    }

    private async Task<long> SeedSubgroupAsync(long chartId, long seq, double xbar, double? range = null)
    {
        var eventTime = _base.AddMinutes(seq);
        var sg = new SpcSubgroup
        {
            ChartId = chartId, Seq = seq,
            EventTimeFrom = eventTime, EventTimeTo = eventTime,
            SampleCount = 5, Xbar = xbar, RangeVal = range ?? 0.2,
            StddevVal = 0, MrVal = 0,
            Status = (int)SubgroupStatus.Closed,
            ClosedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        await _repo.InsertSubgroupAsync(sg, _ct);
        var measureId = await _repo.InsertMeasureAsync(new SpcMeasure
        {
            ChartId = chartId, EventTime = eventTime, MeasureValue = xbar,
            LotId = "LOT-M3", Sn = $"SN-M3-{seq}", WorkOrder = "WO-M3",
            Shift = "A", Recipe = "RCP", Equipment = "M-01",
            ProcTag = "{\"temperature\":22.5}", Source = (int)MeasureSource.Simulator,
            DedupKey = $"dk-m3-{seq}", IngestTime = DateTime.UtcNow
        }, _ct);
        await _repo.AssignSubgroupAsync([measureId], sg.Id, _ct);
        return sg.Id;
    }

    [Fact]
    public async Task NoActiveLimit_RejectsEvaluationAndEmitsNoRuleOut()
    {
        long chart = await NewChartAsync();
        long sg = await SeedSubgroupAsync(chart, 1, 10.5);

        var outcome = await _service.EvaluateAsync(chart, sg, _ct);

        Assert.False(outcome.Evaluated);
        Assert.Contains("拒绝判异", outcome.Reason);
        Assert.Empty(outcome.RuleOuts);
        Assert.Empty(_publisher.RuleOuts);
        Assert.Empty(await _repo.GetRuleOutsAsync(chart, 50, _ct));
        var unchanged = await _repo.GetSubgroupAsync(sg, _ct);
        Assert.Equal((int)SubgroupStatus.Closed, unchanged!.Status);
    }

    [Fact]
    public async Task R1_ProducesRuleOutWithLimitVerIdAndCriticalSeverity()
    {
        long chart = await NewChartAsync();
        long version = await NewActiveVersionAsync(chart);
        long sg = await SeedSubgroupAsync(chart, 1, 10.5); // z = (10.5-10)/0.1 = 5

        var outcome = await _service.EvaluateAsync(chart, sg, _ct);

        Assert.True(outcome.Evaluated, outcome.Reason);
        Assert.Equal(version, outcome.LimitVerId);
        Assert.Equal(1, outcome.VersionNo);
        var hit = Assert.Single(outcome.RuleOuts);
        Assert.Equal((int)RuleCode.R1, hit.RuleCode);
        Assert.Equal((int)Severity.Critical, hit.Severity);
        Assert.Equal(version, hit.LimitVerId);
        Assert.Equal(5.0, hit.ZValue, 6);
        Assert.Equal((int)OcapStatus.Detected, hit.OcapStatus);
        Assert.Equal("LOT-M3", hit.LotId);
        Assert.Equal("SN-M3-1", hit.Sn);
        Assert.Single(_publisher.RuleOuts);

        var evaluated = await _repo.GetSubgroupAsync(sg, _ct);
        Assert.Equal((int)SubgroupStatus.Evaluated, evaluated!.Status);
        Assert.Equal(version, evaluated.LimitVerId);
    }

    [Fact]
    public async Task FrozenLimits_AreNotRecomputed_DriftIsStillDetected()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long lastSubgroup = 0;
        for (long seq = 1; seq <= 20; seq++)
            lastSubgroup = await SeedSubgroupAsync(chart, seq, 10.0 + seq * 0.03);

        var outcome = await _service.EvaluateAsync(chart, lastSubgroup, _ct);

        Assert.True(outcome.Evaluated, outcome.Reason);
        Assert.Contains(outcome.RuleOuts, r => r.RuleCode == (int)RuleCode.R1);
        // 若滚动重算控制限，漂移会被「自适应」吸收；这里 z 仍严格按冻结限 10.0/0.1 计算。
        var r1 = outcome.RuleOuts.First(r => r.RuleCode == (int)RuleCode.R1);
        Assert.Equal(6.0, r1.ZValue, 6); // (10.6-10)/0.1
        Assert.Equal(20, outcome.WindowSize);
    }

    [Fact]
    public async Task RuleSetSubset_SuppressesR5ToR8()
    {
        long chart = await NewChartAsync("WECO-4-V1");
        await NewActiveVersionAsync(chart);

        // 15 个 |z|<1 的点，符号非全同侧、非严格交替、非单调：只触发 R7。
        double[] signs = [1, 1, -1, 1, 1, -1, -1, 1, 1, -1, -1, 1, -1, 1, -1];
        long last = 0;
        for (int i = 0; i < signs.Length; i++)
            last = await SeedSubgroupAsync(chart, i + 1, 10.0 + signs[i] * (i % 2 == 0 ? 0.02 : 0.03));

        var conservative = await _service.EvaluateAsync(chart, last, _ct);
        Assert.True(conservative.Evaluated, conservative.Reason);
        Assert.Empty(conservative.RuleOuts);
        Assert.Equal("WECO-4-V1", conservative.RuleSetVer);

        var chartEntity = await _repo.GetChartAsync(chart, _ct);
        chartEntity!.RuleSetVer = "WECO-8-V1";
        await _repo.UpdateChartAsync(chartEntity, _ct);

        var full = await _service.EvaluateAsync(chart, last, _ct);
        Assert.Contains(full.RuleOuts, r => r.RuleCode == (int)RuleCode.R7);
    }

    [Fact]
    public async Task DuplicateEvaluation_DoesNotDuplicateRuleOuts()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long sg = await SeedSubgroupAsync(chart, 1, 10.5);

        await _service.EvaluateAsync(chart, sg, _ct);
        var again = await _service.EvaluateAsync(chart, sg, _ct);

        Assert.Empty(again.RuleOuts);
        Assert.Single(await _repo.GetRuleOutsBySubgroupAsync(sg, _ct));
        Assert.Single(_publisher.RuleOuts);
    }

    [Fact]
    public async Task A06_AlertIsPublishedWellWithinOneMinute()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        long sg = await SeedSubgroupAsync(chart, 1, 10.5);

        var outcome = await _service.EvaluateAsync(chart, sg, _ct);

        Assert.True(outcome.Elapsed < TimeSpan.FromMinutes(1),
            $"子组闭合到告警推送耗时 {outcome.Elapsed}");
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(5),
            $"实际判异耗时 {outcome.Elapsed}，应远低于 1 分钟");
        Assert.Single(_publisher.RuleOuts);
    }

    [Fact]
    public async Task Replay_EvaluatesAllUnevaluatedSubgroups()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        for (long seq = 1; seq <= 5; seq++)
            await SeedSubgroupAsync(chart, seq, 10.0 + seq * 0.01);

        var results = await _service.ReplayAsync(
            new EvaluationRequest { ChartId = chart, ReplayCount = 10 }, _ct);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.Evaluated, r.Reason));
        Assert.Empty(await _repo.GetUnevaluatedSubgroupsAsync(chart, 50, _ct));
    }

    private sealed class RecordingPublisher : IRealtimePublisher
    {
        public List<object> RuleOuts { get; } = [];
        public Task PublishSubgroupAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishMeasureAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishNoticeAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishRuleOutAsync(object payload, CancellationToken ct)
        {
            RuleOuts.Add(payload);
            return Task.CompletedTask;
        }
    }
}
