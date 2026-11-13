using Spc.Application;
using Spc.Domain;
using Spc.Infrastructure;
using SqlSugar;
using Xunit;

namespace Spc.Tests;

/// <summary>
/// M2 集成测试：真实 SQLite 库 + DDL + 仓储事务，验证 A-04、历史回放、
/// 只读取限契约与「冻结只生成 PENDING」门禁。
/// </summary>
public sealed class BaselinePublishIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqlSugarScope _db;
    private readonly SpcRepository _repo;
    private readonly CancellationToken _ct = CancellationToken.None;

    public BaselinePublishIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spc-m2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = SpcDatabase.Create("Sqlite", $"Data Source={Path.Combine(_dir, "spc.db")}");
        SpcDatabase.Initialize(_db, "Sqlite", AppContext.BaseDirectory);
        _repo = new SpcRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<long> NewChartAsync()
        => await _repo.InsertChartAsync(new SpcChart
        {
            Code = "CH-TEST", Name = "integration", ChartType = (int)ChartType.XbarR,
            SubgroupSize = 5, Usl = 10.5, Lsl = 9.5, Target = 10.0, Unit = "mm",
            Machine = "M-01", Product = "P", Process = "PR", Recipe = "RCP",
            RuleSetVer = "WECO-8-V1", IsEnabled = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        }, _ct);

    private static SpcLimitVersion NewVersion(long chartId, int no, int status, string hash = "")
        => new()
        {
            ChartId = chartId, VersionNo = no, ChartType = (int)ChartType.XbarR,
            Cl = 10.0, Ucl = 10.3, Lcl = 9.7, WarnUcl = 10.2, WarnLcl = 9.8,
            VarCl = 0.2, VarUcl = 0.42, VarLcl = 0.0, SigmaHat = 0.086,
            SubgroupSize = 5, SubgroupCount = 25,
            DataFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DataTo = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            DataHash = string.IsNullOrEmpty(hash) ? new string('a', 64) : hash,
            SubgroupIds = "[]", RuleSetVer = "WECO-8-V1", CalcParams = "test",
            Iterations = 1, ChangeReason = "test", CreatedBy = "qa", ApprovedBy = "",
            Status = status, CreatedAt = DateTime.UtcNow
        };

    private async Task<long> NewPendingAsync(long chartId, int no, string? hash = null)
        => await _repo.InsertLimitVersionAsync(
            NewVersion(chartId, no, (int)LimitVersionStatus.Pending,
                hash ?? new string((char)('a' + no % 20), 64)), _ct);

    [Fact]
    public async Task A04_PublishExpiresPreviousActiveAndKeepsExactlyOneActive()
    {
        long chart = await NewChartAsync();
        long v1 = await NewPendingAsync(chart, 1);
        long v2 = await NewPendingAsync(chart, 2);
        var t1 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddDays(1);

        var r1 = await _repo.PublishVersionAsync(chart, v1, "qe.li", t1, "首次建限", _ct);
        var r2 = await _repo.PublishVersionAsync(chart, v2, "qe.li", t2, "季度复审换版", _ct);

        Assert.True(r1.Success, r1.Error);
        Assert.True(r2.Success, r2.Error);
        Assert.Equal(v1, r2.ExpiredVersionId);

        var actives = await _db.Queryable<SpcLimitVersion>()
            .Where(v => v.ChartId == chart && v.Status == (int)LimitVersionStatus.Active).ToListAsync();
        Assert.Single(actives);
        Assert.Equal(v2, actives[0].Id);

        var old = await _repo.GetLimitVersionAsync(v1, _ct);
        Assert.Equal((int)LimitVersionStatus.Expired, old!.Status);
        Assert.Equal(t2, old.ExpiredTime!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(10.0, old.Cl); // 计算字段永不修改

        // 第二次发布同版本必须被拒绝（状态门禁）。
        var again = await _repo.PublishVersionAsync(chart, v2, "qe.li", t2.AddHours(1), "重复发布", _ct);
        Assert.False(again.Success);
    }

    [Fact]
    public async Task A04_ConcurrentPublishLeavesExactlyOneActive()
    {
        long chart = await NewChartAsync();
        long version = await NewPendingAsync(chart, 1);
        var at = DateTime.UtcNow.AddMinutes(-1);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
        {
            await Task.Delay(i * 12);
            try
            {
                return await _repo.PublishVersionAsync(chart, version, $"qe.{i}", at, "并发审批", _ct);
            }
            catch (Exception ex)
            {
                return new LimitPublishOutcome(false, ex.Message, version, null, string.Empty);
            }
        })));

        Assert.Contains(results, r => r.Success);
        var actives = await _db.Queryable<SpcLimitVersion>()
            .Where(v => v.ChartId == chart && v.Status == (int)LimitVersionStatus.Active).ToListAsync();
        Assert.Single(actives);
        Assert.Equal(version, actives[0].Id);
    }

    [Fact]
    public async Task A04_UniqueIndexRejectsSecondActiveRow()
    {
        long chart = await NewChartAsync();
        await _repo.InsertLimitVersionAsync(NewVersion(chart, 1, (int)LimitVersionStatus.Active), _ct);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _repo.InsertLimitVersionAsync(NewVersion(chart, 2, (int)LimitVersionStatus.Active), _ct));
    }

    [Fact]
    public async Task Replay_UsesVersionEffectiveAtEventTime()
    {
        long chart = await NewChartAsync();
        long v1 = await NewPendingAsync(chart, 1);
        long v2 = await NewPendingAsync(chart, 2);
        var t1 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddDays(1);

        Assert.True((await _repo.PublishVersionAsync(chart, v1, "qe", t1, "v1", _ct)).Success);
        Assert.True((await _repo.PublishVersionAsync(chart, v2, "qe", t2, "v2", _ct)).Success);

        Assert.Null(await _repo.GetEffectiveLimitAsync(chart, t1.AddHours(-1), _ct));
        Assert.Equal(v1, (await _repo.GetEffectiveLimitAsync(chart, t1.AddHours(1), _ct))!.Id);
        Assert.Equal(v2, (await _repo.GetEffectiveLimitAsync(chart, t2.AddHours(1), _ct))!.Id);

        // 历史回放必须拿到 EXPIRED 版本，而不是当前 ACTIVE。
        var expired = await _repo.GetEffectiveLimitAsync(chart, t1.AddHours(1), _ct);
        Assert.Equal((int)LimitVersionStatus.Expired, expired!.Status);
    }

    [Fact]
    public async Task FrozenLimitReader_ReturnsStoredLimitsWithoutRecomputing()
    {
        long chart = await NewChartAsync();
        long v1 = await NewPendingAsync(chart, 1);
        var t1 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.True((await _repo.PublishVersionAsync(chart, v1, "qe", t1, "v1", _ct)).Success);

        var reader = new FrozenLimitReader(_repo);
        var limits = await reader.GetLimitsForEvaluationAsync(chart, t1.AddHours(1), _ct);

        Assert.NotNull(limits);
        Assert.Equal(10.0, limits.Cl, 9);
        Assert.Equal(10.3, limits.Ucl, 9);
        Assert.Equal(9.7, limits.Lcl, 9);
        Assert.Equal(3.0, limits.Standardize(limits.Ucl), 9);
        Assert.Null(await reader.GetLimitsForEvaluationAsync(chart, t1.AddHours(-1), _ct));
        Assert.Contains("ACTIVE", await reader.DescribeAsync(chart, _ct));
    }

    [Fact]
    public async Task Freeze_ProducesPendingVersionAndIsReproducible()
    {
        long chart = await NewChartAsync();
        await SeedStableSubgroupsAsync(chart, 25, 5);

        var service = new BaselineFreezeService(_repo, new NullPublisher());
        var request = new BaselineFreezeRequest
        {
            ChartId = chart, SubgroupCount = 25,
            CreatedBy = "qa.zhang", ChangeReason = "首次建立基线"
        };

        var first = await service.FreezeAsync(request, _ct);
        Assert.True(first.Ok, first.Error);
        Assert.NotNull(first.Version);
        Assert.Equal((int)LimitVersionStatus.Pending, first.Version.Status);
        Assert.Null(first.Version.EffectiveFrom);
        Assert.Empty(first.Version.ApprovedBy);
        Assert.Equal(64, first.Version.DataHash.Length);
        Assert.True(first.Limits!.Ucl > first.Limits.Cl);
        Assert.Contains("PENDING", first.Note);

        // 系统绝不自动激活：发布前 GetEffectiveLimit 仍为空。
        Assert.Null(await _repo.GetEffectiveLimitAsync(chart, DateTime.UtcNow, _ct));

        // A-02：同一数据集重算得到完全相同的指纹。
        var second = await service.FreezeAsync(request, _ct);
        Assert.True(second.Ok, second.Error);
        Assert.Equal(first.Version.DataHash, second.Version!.DataHash);
        Assert.Equal(first.Limits.Cl, second.Limits!.Cl, 12);
        Assert.Equal(first.Limits.Ucl, second.Limits.Ucl, 12);
    }

    private async Task SeedStableSubgroupsAsync(long chartId, int count, int n)
    {
        var anchor = DateTime.UtcNow.AddMinutes(-count - 5);
        long seq = 0;
        for (int g = 0; g < count; g++)
        {
            var measureIds = new List<long>();
            var values = new List<double>();
            for (int j = 0; j < n; j++)
            {
                double raw = Math.Sin((g * 127.1 + j * 311.7 + 13.7) * 0.0174532925199433) * 43758.5453;
                double noise = (raw - Math.Floor(raw)) - 0.5;
                double value = Math.Round(10.0 + 0.09 * noise, 6);
                values.Add(value);
                seq++;
                measureIds.Add(await _repo.InsertMeasureAsync(new SpcMeasure
                {
                    ChartId = chartId,
                    EventTime = anchor.AddMinutes(g).AddSeconds(j * 6),
                    MeasureValue = value,
                    LotId = "LOT-TEST", Sn = $"SN-{seq:D8}", WorkOrder = "WO-TEST",
                    Shift = "A", Recipe = "RCP", Equipment = "M-01",
                    ProcTag = "{\"temperature\":22.5}", Source = (int)MeasureSource.Simulator,
                    DedupKey = $"dk-{seq}", IngestTime = DateTime.UtcNow
                }, _ct));
            }

            var sg = new SpcSubgroup
            {
                ChartId = chartId, Seq = g + 1,
                EventTimeFrom = anchor.AddMinutes(g),
                EventTimeTo = anchor.AddMinutes(g).AddSeconds((n - 1) * 6),
                SampleCount = n, Xbar = values.Average(),
                RangeVal = values.Max() - values.Min(), StddevVal = 0, MrVal = 0,
                Status = (int)SubgroupStatus.Closed,
                ClosedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
            };
            await _repo.InsertSubgroupAsync(sg, _ct);
            await _repo.AssignSubgroupAsync(measureIds, sg.Id, _ct);
        }
    }

    private sealed class NullPublisher : IRealtimePublisher
    {
        public Task PublishSubgroupAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishMeasureAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishNoticeAsync(object payload, CancellationToken ct) => Task.CompletedTask;
    }
}
