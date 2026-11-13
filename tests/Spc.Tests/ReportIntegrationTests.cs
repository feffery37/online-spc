using System.Security.Cryptography;
using System.Text;
using Spc.Application;
using Spc.Domain;
using Spc.Infrastructure;
using SqlSugar;
using Xunit;

namespace Spc.Tests;

/// <summary>M5 集成测试：直方图、受控门禁、周期回放、快照指纹、合规自检、审计窗口。</summary>
public sealed class ReportIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqlSugarScope _db;
    private readonly SpcRepository _repo;
    private readonly CapabilityService _capability;
    private readonly ReportService _report;
    private readonly RecordingPublisher _publisher = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    public ReportIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spc-m5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = SpcDatabase.Create("Sqlite", $"Data Source={Path.Combine(_dir, "spc.db")}");
        SpcDatabase.Initialize(_db, "Sqlite", AppContext.BaseDirectory);
        _repo = new SpcRepository(_db);
        var reader = new FrozenLimitReader(_repo);
        _capability = new CapabilityService(_repo, reader);
        _report = new ReportService(_repo, _capability, _publisher);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<long> NewChartAsync()
        => await _repo.InsertChartAsync(new SpcChart
        {
            Code = "CH-M5", Name = "M5 test", ChartType = (int)ChartType.XbarR,
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
            DataHash = new string('d', 64), SubgroupIds = "[]",
            RuleSetVer = "WECO-8-V1", CalcParams = "test", Iterations = 1,
            ChangeReason = "test", CreatedBy = "qa", ApprovedBy = "qe.li",
            Status = (int)LimitVersionStatus.Active,
            EffectiveFrom = DateTime.UtcNow.AddDays(-7), ExpiredTime = null,
            CreatedAt = DateTime.UtcNow
        }, _ct);

    private async Task SeedMeasureAsync(long chartId, DateTime at, double value)
        => await _repo.InsertMeasureAsync(new SpcMeasure
        {
            ChartId = chartId, EventTime = at, MeasureValue = value,
            LotId = "LOT-M5", Sn = $"SN-{at:yyyyMMddHHmmssfff}", WorkOrder = "WO-M5",
            Shift = "A", Recipe = "RCP", Equipment = "M-01",
            ProcTag = "{\"temperature\":22.5}", Source = (int)MeasureSource.Simulator,
            DedupKey = Guid.NewGuid().ToString("N")[..16], IngestTime = DateTime.UtcNow
        }, _ct);

    private async Task<long> SeedRuleOutAsync(long chartId, long versionId, int ocapStatus, DateTime at)
        => await _repo.InsertRuleOutAsync(new SpcRuleOut
        {
            ChartId = chartId, SubgroupId = 0, LimitVerId = versionId,
            RuleCode = (int)RuleCode.R1, Severity = (int)Severity.Critical,
            StatValue = 10.5, ZValue = 5.0, EventTime = at,
            OcapStatus = ocapStatus, LotId = "LOT-M5", Sn = "SN-OUT",
            CreatedAt = DateTime.UtcNow
        }, _ct);

    [Fact]
    public async Task Histogram_BinsCoverAllSamplesAndAreNonZeroWidth()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        var start = DateTime.UtcNow.AddDays(-2);
        for (int i = 0; i < 50; i++)
            await SeedMeasureAsync(chart, start.AddMinutes(i), 10.0 + (i % 10) * 0.01);

        var report = await _report.BuildCapabilityAsync(chart, start.AddDays(-1), DateTime.UtcNow, 12, _ct);

        Assert.Equal(50, report.SampleCount);
        Assert.NotNull(report.Histogram);
        Assert.Equal(12, report.Histogram.Bins.Count);
        Assert.True(report.Histogram.BinWidth > 0);
        Assert.Equal(50, report.Histogram.Bins.Sum(b => b.Count));
        Assert.Equal(10.1, report.Usl);
        Assert.Equal(9.9, report.Lsl);
        Assert.Contains("规格线", report.SpecLineNote);
    }

    [Fact]
    public async Task Capability_IsBlockedWhileRuleOutIsNotClosed()
    {
        long chart = await NewChartAsync();
        long version = await NewActiveVersionAsync(chart);
        await SeedMeasureAsync(chart, DateTime.UtcNow.AddMinutes(-5), 10.0);
        await SeedRuleOutAsync(chart, version, (int)OcapStatus.ActionSent, DateTime.UtcNow.AddMinutes(-3));

        var result = await _capability.ComputeAsync(chart, DateTime.UtcNow, _ct);

        Assert.False(result.Available);
        Assert.Contains("未闭环 Rule Out", result.Reason);
        Assert.Null(result.Indices);
    }

    [Fact]
    public async Task Capability_IsAvailableAfterRuleOutClosed()
    {
        long chart = await NewChartAsync();
        long version = await NewActiveVersionAsync(chart);
        for (int i = 0; i < 20; i++)
            await SeedMeasureAsync(chart, DateTime.UtcNow.AddMinutes(-20 + i), 10.0 + (i % 5) * 0.01);
        await SeedRuleOutAsync(chart, version, (int)OcapStatus.Closed, DateTime.UtcNow.AddMinutes(-3));

        var result = await _capability.ComputeAsync(chart, DateTime.UtcNow, _ct);

        Assert.True(result.Available, result.Reason);
        Assert.NotNull(result.Indices);
        Assert.Contains("无未闭环", result.ControlBasis);
    }

    [Fact]
    public async Task PeriodReport_GroupsByDayAndReplaysEffectiveVersion()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        for (int d = -3; d <= 0; d++)
            for (int i = 0; i < 10; i++)
                await SeedMeasureAsync(chart, DateTime.UtcNow.Date.AddDays(d).AddHours(9).AddMinutes(i),
                    10.0 + (i % 4) * 0.01);

        var report = await _report.BuildPeriodsAsync(chart, "day", 5, _ct);

        Assert.Equal("day", report.Granularity);
        Assert.Equal(5, report.Periods.Count);
        Assert.Contains(report.Periods, p => p.SampleCount == 10);
        Assert.All(report.Periods.Where(p => p.SampleCount > 0), p =>
        {
            Assert.Equal(1, p.LimitVersionNo);
            Assert.True(p.IsControlled);
            Assert.NotNull(p.Cpk);
        });
    }

    [Fact]
    public async Task Snapshot_HashMatchesPayloadAndIsAudited()
    {
        long chart = await NewChartAsync();
        await NewActiveVersionAsync(chart);
        for (int i = 0; i < 20; i++)
            await SeedMeasureAsync(chart, DateTime.UtcNow.AddMinutes(-30 + i), 10.0 + (i % 3) * 0.01);

        var snapshot = await _report.GenerateSnapshotAsync(chart, "DAILY", "test", _ct);

        string recomputed = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Payload))).ToLowerInvariant();
        Assert.Equal(recomputed, snapshot.DataHash);
        Assert.Equal(64, snapshot.DataHash.Length);
        Assert.Equal("DAILY", snapshot.ReportType);
        Assert.Single(_publisher.Reports);
        Assert.Single(await _repo.GetReportSnapshotsAsync(chart, 50, _ct));
        Assert.Contains(await _repo.GetAuditLogsAsync(chart, null, 100, _ct),
            a => a.EventType == "REPORT_GENERATED");
    }

    [Fact]
    public async Task Compliance_ChecklistDetectsMissingVersion()
    {
        long chart = await NewChartAsync();
        var empty = await _report.BuildComplianceAsync(chart, _ct);

        Assert.False(empty.Ready);
        Assert.Contains(empty.Checklist, c => c.Item.Contains("版本化") && !c.Pass);

        await NewActiveVersionAsync(chart);
        var withVersion = await _report.BuildComplianceAsync(chart, _ct);
        Assert.Contains(withVersion.Checklist, c => c.Item.Contains("版本化") && c.Pass);
        Assert.Contains(withVersion.Checklist, c => c.Item.Contains("人工审批") && c.Pass);
        Assert.Contains(withVersion.Checklist, c => c.Item.Contains("ACTIVE") && c.Pass);
    }

    [Fact]
    public async Task AuditReport_FiltersByWindow()
    {
        long chart = await NewChartAsync();
        await _repo.InsertAuditAsync(new SpcAuditLog
        {
            EventTime = DateTime.UtcNow.AddDays(-10), EventType = "OLD_EVENT",
            EntityType = "test", EntityId = 1, ChartId = chart,
            Payload = "{}", Actor = "test", CreatedAt = DateTime.UtcNow.AddDays(-10)
        }, _ct);
        await _repo.InsertAuditAsync(new SpcAuditLog
        {
            EventTime = DateTime.UtcNow.AddMinutes(-5), EventType = "NEW_EVENT",
            EntityType = "test", EntityId = 2, ChartId = chart,
            Payload = "{}", Actor = "test", CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        }, _ct);

        var report = await _report.BuildAuditAsync(
            chart, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddMinutes(1), _ct);

        Assert.Equal(1, report.Total);
        Assert.Equal("NEW_EVENT", report.Events[0].EventType);
    }

    private sealed class RecordingPublisher : IRealtimePublisher
    {
        public List<object> Reports { get; } = [];
        public Task PublishSubgroupAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishMeasureAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishNoticeAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishRuleOutAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishDispositionAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishReportAsync(object payload, CancellationToken ct)
        {
            Reports.Add(payload);
            return Task.CompletedTask;
        }
    }
}
