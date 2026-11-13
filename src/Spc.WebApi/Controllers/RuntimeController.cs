using Microsoft.AspNetCore.Mvc;
using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi.Controllers;

[ApiController]
[Route("api/spc")]
public sealed class RuntimeController(
    IngestService ingest,
    ISpcRepository repository,
    ILimitReader limitReader,
    SimulatorService simulator,
    ChartService chartService,
    CapabilityService capability) : ControllerBase
{
    /// <summary>方案 8.2：POST /api/spc/measure/ingest，单条或批量量测接入。</summary>
    [HttpPost("measure/ingest")]
    public async Task<object> Ingest([FromBody] IngestBatchRequest request, CancellationToken ct)
        => await ingest.IngestAsync(request, ct);

    [HttpGet("measure")]
    public async Task<object> Measures([FromQuery] long chartId, [FromQuery] int take = 50, CancellationToken ct = default)
        => new { items = await repository.GetMeasuresAsync(chartId, Math.Clamp(take, 1, 500), ct) };

    [HttpGet("subgroups")]
    public async Task<object> Subgroups([FromQuery] long chartId, [FromQuery] int take = 60, CancellationToken ct = default)
        => new { items = await repository.GetSubgroupsAsync(chartId, Math.Clamp(take, 1, 500), ct) };

    /// <summary>
    /// 方案 8.2：GET /api/spc/chart/query，控制图数据与版本回放。
    /// M2 起按事件时间返回冻结限；回放必须用当时生效版本，不能用当前 ACTIVE。
    /// </summary>
    [HttpGet("chart/query")]
    public async Task<object> ChartQuery(
        [FromQuery] long chartId, [FromQuery] int take = 60,
        [FromQuery] DateTime? eventTime = null, CancellationToken ct = default)
    {
        var chart = await repository.GetChartAsync(chartId, ct);
        if (chart is null) return NotFound(new { error = "控制图不存在" });

        var at = eventTime ?? DateTime.UtcNow;
        var versions = await repository.GetLimitVersionsAsync(chartId, ct);
        var subgroups = await repository.GetSubgroupsAsync(chartId, Math.Clamp(take, 1, 500), ct);
        var version = await repository.GetEffectiveLimitAsync(chartId, at, ct);
        var limits = version is null ? null : LimitVersionMapper.ToControlLimits(version);

        // 严格的历史回放：每个子组按自身 EVENT_TIME 标注当时生效的限值版本，
        // 而不是把当前 ACTIVE 版本套到全部历史点上（方案 5.7 / 附录 A）。
        var effective = versions.Where(v => v.EffectiveFrom.HasValue)
            .Select(v => (Id: v.Id, No: v.VersionNo, From: v.EffectiveFrom!.Value,
                          To: v.ExpiredTime ?? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)))
            .ToArray();
        var subgroupLimits = subgroups.Select(s =>
        {
            var hit = effective.FirstOrDefault(v => v.From <= s.EventTimeTo && v.To > s.EventTimeTo);
            return new
            {
                s.Seq,
                limitVerId = hit.Id == 0 ? null : (long?)hit.Id,
                versionNo = hit.Id == 0 ? null : (int?)hit.No
            };
        }).ToArray();

        return new
        {
            chart,
            type = ((ChartType)chart.ChartType).Label(),
            eventTime = at,
            subgroups,
            subgroupLimits,
            versions,
            limits,
            limitsVerId = version?.Id,
            limitsVersionNo = version?.VersionNo,
            limitsActive = version is not null,
            // M3 接入规则引擎前，判异仍不启用；但冻结限已可绘制。
            evaluationEnabled = false,
            evaluationNote = "M2：冻结限值版本已生效，控制图可绘制 CL/UCL/LCL 与 Zone 分区；Rule Out 判异在 M3 接入。",
            contract = await limitReader.DescribeAsync(chartId, ct),
            capability = await capability.ComputeAsync(chartId, at, ct),
            specLimits = new { chart.Usl, chart.Lsl, note = "规格限只用于能力直方图，禁止画在 Xbar 控制图上（方案 4.5）" }
        };
    }

    [HttpGet("limits/versions")]
    public async Task<object> LimitVersions([FromQuery] long chartId, CancellationToken ct)
        => new { items = await repository.GetLimitVersionsAsync(chartId, ct) };

    [HttpPost("simulator/start")]
    public object SimulatorStart([FromBody] SimulatorRequest request) => new
    {
        started = simulator.Start(request),
        state = simulator.GetState(),
        scenarios = new[] { "stable", "drift", "trend", "oscillation", "spike" }
    };

    [HttpPost("simulator/stop")]
    public object SimulatorStop() => new { stopped = simulator.Stop(), state = simulator.GetState() };

    [HttpGet("simulator/state")]
    public object SimulatorState() => simulator.GetState();

    /// <summary>断网续传演练：乱序到达的一批点，验证按 EVENT_TIME 重放。</summary>
    [HttpPost("simulator/replay")]
    public async Task<object> Replay(
        [FromQuery] string chartCode = "CH-DEMO-01",
        [FromQuery] int count = 60,
        CancellationToken ct = default)
        => new
        {
            purpose = "N-02 断网续传：乱序到达，按事件时间重放",
            result = await simulator.ReplayOutOfOrderAsync(chartCode, count, ct)
        };

    /// <summary>一键生成 Phase I 候选数据：稳定过程、足量合理子组、完整上下文标签。</summary>
    [HttpPost("simulator/baseline-data")]
    public async Task<object> BaselineData(
        [FromQuery] string chartCode = "CH-DEMO-01",
        [FromQuery] int subgroups = 25,
        CancellationToken ct = default)
        => await simulator.GenerateBaselineDataAsync(chartCode, subgroups, ct);

    [HttpPost("seed")]
    public async Task<object> Seed(CancellationToken ct)
    {
        await DbSeeder.EnsureDemoChartAsync(chartService, ct);
        return new { ok = true, charts = await repository.GetChartsAsync(ct) };
    }
}
