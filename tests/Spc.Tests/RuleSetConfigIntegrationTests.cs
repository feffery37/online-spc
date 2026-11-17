using Spc.Application;
using Spc.Domain;
using Spc.Infrastructure;
using SqlSugar;
using Xunit;

namespace Spc.Tests;

/// <summary>规则集配置：自定义子集/严重级别的保存、绑定、审计与判异生效。</summary>
public sealed class RuleSetConfigIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqlSugarScope _db;
    private readonly SpcRepository _repo;
    private readonly RuleSetService _service;
    private readonly CancellationToken _ct = CancellationToken.None;

    public RuleSetConfigIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spc-ruleset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = SpcDatabase.Create("Sqlite", $"Data Source={Path.Combine(_dir, "spc.db")}");
        SpcDatabase.Initialize(_db, "Sqlite", AppContext.BaseDirectory);
        _repo = new SpcRepository(_db);
        _service = new RuleSetService(_repo);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<long> NewChartAsync(string ruleSet = "WECO-8-V1")
        => await _repo.InsertChartAsync(new SpcChart
        {
            Code = "CH-RS", Name = "ruleset test", ChartType = (int)ChartType.XbarR,
            SubgroupSize = 5, Usl = 10.1, Lsl = 9.9, Target = 10.0, Unit = "mm",
            Machine = "M-01", Product = "P", Process = "PR", Recipe = "RCP",
            RuleSetVer = ruleSet, IsEnabled = 1,
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
            DataHash = new string('e', 64), SubgroupIds = "[]",
            RuleSetVer = "WECO-8-V1", CalcParams = "test", Iterations = 1,
            ChangeReason = "test", CreatedBy = "qa", ApprovedBy = "qe.li",
            Status = (int)LimitVersionStatus.Active,
            EffectiveFrom = DateTime.UtcNow.AddHours(-2), ExpiredTime = null,
            CreatedAt = DateTime.UtcNow
        }, _ct);

    private async Task<long> SeedSubgroupAsync(long chartId, long seq, double xbar)
    {
        var at = DateTime.UtcNow.AddMinutes(-30).AddMinutes(seq);
        var sg = new SpcSubgroup
        {
            ChartId = chartId, Seq = seq, EventTimeFrom = at, EventTimeTo = at,
            SampleCount = 5, Xbar = xbar, RangeVal = 0.2, StddevVal = 0, MrVal = 0,
            Status = (int)SubgroupStatus.Closed,
            ClosedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        await _repo.InsertSubgroupAsync(sg, _ct);
        return sg.Id;
    }

    private EvaluationService NewEvaluator()
        => new(_repo, new FrozenLimitReader(_repo), new NullPublisher(), null, _service);

    private static RuleSetSaveRequest Request(
        string code, string[] rules, Dictionary<string, string>? severity = null) => new()
    {
        Code = code,
        Name = "custom " + code,
        Description = "integration test",
        Rules = [.. rules],
        Severity = severity ?? rules.ToDictionary(r => r, _ => "WARNING"),
        CreatedBy = "qe.li"
    };

    [Fact]
    public async Task List_ContainsBuiltInPresetsAndCustomSets()
    {
        var builtIn = await _service.ListAsync(_ct);
        Assert.Equal(3, builtIn.Count);
        Assert.All(builtIn, v => Assert.True(v.BuiltIn));

        var saved = await _service.SaveAsync(Request("CUSTOM-A", ["R1", "R3"]), _ct);
        Assert.True(saved.Ok, saved.Error);

        var all = await _service.ListAsync(_ct);
        Assert.Equal(4, all.Count);
        var custom = Assert.Single(all, v => !v.BuiltIn);
        Assert.Equal("CUSTOM-A", custom.Version);
        Assert.Equal(new[] { "R1", "R3" }, custom.Rules);
    }

    [Fact]
    public async Task Save_ValidatesCodeRulesAndSeverity()
    {
        Assert.False((await _service.SaveAsync(Request("WECO-MINE", ["R1"]), _ct)).Ok);
        Assert.False((await _service.SaveAsync(Request("X", ["R1"]), _ct)).Ok);
        Assert.False((await _service.SaveAsync(Request("CUSTOM-EMPTY", []), _ct)).Ok);
        Assert.False((await _service.SaveAsync(Request("CUSTOM-BAD", ["R9"]), _ct)).Ok);
        Assert.False((await _service.SaveAsync(
            Request("CUSTOM-SEV", ["R1"], new Dictionary<string, string> { ["R1"] = "UNKNOWN" }), _ct)).Ok);

        Assert.True((await _service.SaveAsync(Request("CUSTOM-OK", ["R1"]), _ct)).Ok);
    }

    [Fact]
    public async Task Bind_UpdatesChartAndWritesAuditTrail()
    {
        long chart = await NewChartAsync("WECO-8-V1");
        Assert.True((await _service.SaveAsync(Request("CUSTOM-BIND", ["R1", "R2"]), _ct)).Ok);

        var (ok, error) = await _service.BindAsync(chart, "CUSTOM-BIND", _ct);
        Assert.True(ok, error);
        Assert.Equal("CUSTOM-BIND", (await _repo.GetChartAsync(chart, _ct))!.RuleSetVer);

        var audits = await _repo.GetAuditLogsAsync(chart, null, 50, _ct);
        Assert.Contains(audits, a => a.EventType == "RULE_SET_BOUND");

        var fail = await _service.BindAsync(chart, "NOT-EXIST", _ct);
        Assert.False(fail.Ok);
        Assert.Equal("CUSTOM-BIND", (await _repo.GetChartAsync(chart, _ct))!.RuleSetVer);
    }

    [Fact]
    public async Task CustomRuleSet_OnlyR1_SuppressesR7()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        Assert.True((await _service.SaveAsync(Request("CUSTOM-R1", ["R1"]), _ct)).Ok);
        Assert.True((await _service.BindAsync(chart, "CUSTOM-R1", _ct)).Ok);

        // 15 个 |z|<1 且符号不连续、不交替、不单调的序列：全套规则只会命中 R7。
        double[] signs = [1, 1, -1, 1, 1, -1, -1, 1, 1, -1, -1, 1, -1, 1, -1];
        long last = 0;
        for (int i = 0; i < signs.Length; i++)
            last = await SeedSubgroupAsync(chart, i + 1,
                10.0 + signs[i] * (i % 2 == 0 ? 0.02 : 0.03));

        var outcome = await NewEvaluator().EvaluateAsync(chart, last, _ct);

        Assert.True(outcome.Evaluated, outcome.Reason);
        Assert.Empty(outcome.RuleOuts);
        Assert.Equal("CUSTOM-R1", outcome.RuleSetVer);
    }

    [Fact]
    public async Task CustomRuleSet_SeverityIsApplied()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        Assert.True((await _service.SaveAsync(
            Request("CUSTOM-SEV2", ["R1"], new Dictionary<string, string> { ["R1"] = "WARNING" }), _ct)).Ok);
        Assert.True((await _service.BindAsync(chart, "CUSTOM-SEV2", _ct)).Ok);

        long sg = await SeedSubgroupAsync(chart, 1, 10.5); // z = 5
        var outcome = await NewEvaluator().EvaluateAsync(chart, sg, _ct);

        var hit = Assert.Single(outcome.RuleOuts);
        Assert.Equal((int)RuleCode.R1, hit.RuleCode);
        Assert.Equal((int)Severity.Warning, hit.Severity); // 内置 R1 是 CRITICAL，此处被自定义覆盖
        Assert.Equal("CUSTOM-SEV2", outcome.RuleSetVer);
    }

    private sealed class NullPublisher : IRealtimePublisher
    {
        public Task PublishSubgroupAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishMeasureAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishNoticeAsync(object payload, CancellationToken ct) => Task.CompletedTask;
    }
}
