using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Spc.Application;

namespace Spc.WebApi.Controllers;

/// <summary>M5 报表中心：能力直方图、日周月报表、审计视图、合规取证包、CSV 导出与定时推送。</summary>
[ApiController]
[Route("api/spc/reports")]
public sealed class ReportController(
    ReportService report,
    ISpcRepository repository,
    ReportSchedulerWorker scheduler) : ControllerBase
{
    /// <summary>能力分析：直方图分箱 + 规格线 + Cp/Cpk/Pp/Ppk。规格线只出现在这里。</summary>
    [HttpGet("capability")]
    public async Task<object> Capability(
        [FromQuery] long chartId, [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null, [FromQuery] int bins = 20, CancellationToken ct = default)
        => await report.BuildCapabilityAsync(chartId, from, to, bins, ct);

    [HttpGet("periods")]
    public async Task<object> Periods(
        [FromQuery] long chartId, [FromQuery] string granularity = "day",
        [FromQuery] int count = 7, CancellationToken ct = default)
        => await report.BuildPeriodsAsync(chartId, granularity, count, ct);

    [HttpGet("compliance")]
    public async Task<object> Compliance([FromQuery] long chartId, CancellationToken ct)
        => await report.BuildComplianceAsync(chartId, ct);

    [HttpGet("audit")]
    public async Task<object> Audit(
        [FromQuery] long chartId, [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null, CancellationToken ct = default)
        => await report.BuildAuditAsync(chartId, from, to, ct);

    [HttpGet("snapshots")]
    public async Task<object> Snapshots(
        [FromQuery] long chartId, [FromQuery] int take = 50, CancellationToken ct = default)
        => new { items = await repository.GetReportSnapshotsAsync(chartId, Math.Clamp(take, 1, 500), ct) };

    /// <summary>手工生成带 SHA256 指纹的报表快照（DAILY / WEEKLY / MONTHLY / COMPLIANCE）。</summary>
    [HttpPost("generate")]
    public async Task<object> Generate(
        [FromQuery] long chartId, [FromQuery] string type = "DAILY",
        [FromQuery] string generatedBy = "manual", CancellationToken ct = default)
    {
        var snapshot = await report.GenerateSnapshotAsync(chartId, type, generatedBy, ct);
        return new
        {
            ok = true,
            snapshot = new
            {
                snapshot.Id, snapshot.ChartId, snapshot.ReportType,
                snapshot.PeriodFrom, snapshot.PeriodTo, snapshot.DataHash,
                snapshot.GeneratedBy, snapshot.GeneratedAt,
                payloadBytes = snapshot.Payload.Length
            },
            note = "快照只增不改，SHA256 指纹可用于取证复核"
        };
    }

    [HttpGet("schedule")]
    public object Schedule() => scheduler.GetState();

    /// <summary>手动触发一次定时报表任务（演练用）。</summary>
    [HttpPost("schedule/run")]
    public async Task<object> RunNow(CancellationToken ct)
    {
        await scheduler.GenerateOnce(ct);
        return scheduler.GetState();
    }

    /// <summary>CSV 导出：capability / periods / audit / compliance。</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] long chartId, [FromQuery] string kind = "capability",
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] string granularity = "day", [FromQuery] int count = 7,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        string inv(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

        switch (kind.ToLowerInvariant())
        {
            case "periods":
            {
                var r = await report.BuildPeriodsAsync(chartId, granularity, count, ct);
                sb.AppendLine("PERIOD,FROM,TO,SAMPLES,MEAN,SIGMA_OVERALL,CP,CPK,PP,PPK,CONTROLLED,LIMIT_VERSION,GATE");
                foreach (var p in r.Periods)
                    sb.AppendLine(string.Join(',',
                        p.Period, p.From.ToString("O", CultureInfo.InvariantCulture),
                        p.To.ToString("O", CultureInfo.InvariantCulture), p.SampleCount,
                        inv(p.Mean), inv(p.SigmaOverall),
                        p.Cp.HasValue ? inv(p.Cp.Value) : string.Empty,
                        p.Cpk.HasValue ? inv(p.Cpk.Value) : string.Empty,
                        p.Pp.HasValue ? inv(p.Pp.Value) : string.Empty,
                        p.Ppk.HasValue ? inv(p.Ppk.Value) : string.Empty,
                        p.IsControlled, p.LimitVersionNo?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        Csv(p.ControlGateReason)));
                break;
            }
            case "audit":
            {
                var r = await report.BuildAuditAsync(chartId, from, to, ct);
                sb.AppendLine("EVENT_TIME,EVENT_TYPE,ENTITY_TYPE,ENTITY_ID,ACTOR,PAYLOAD");
                foreach (var e in r.Events)
                    sb.AppendLine(string.Join(',',
                        e.EventTime.ToString("O", CultureInfo.InvariantCulture),
                        e.EventType, e.EntityType, e.EntityId, e.Actor, Csv(e.Payload)));
                break;
            }
            case "compliance":
            {
                var r = await report.BuildComplianceAsync(chartId, ct);
                sb.AppendLine("CHECK_ITEM,PASS,EVIDENCE");
                foreach (var c in r.Checklist)
                    sb.AppendLine(string.Join(',', Csv(c.Item), c.Pass, Csv(c.Evidence)));
                sb.AppendLine($"READY,{r.Ready},");
                break;
            }
            default:
            {
                var r = await report.BuildCapabilityAsync(chartId, from, to, 20, ct);
                sb.AppendLine("BIN_FROM,BIN_TO,COUNT");
                foreach (var b in r.Histogram?.Bins ?? [])
                    sb.AppendLine(string.Join(',', inv(b.From), inv(b.To), b.Count));
                sb.AppendLine($"#USL,{r.Usl?.ToString(CultureInfo.InvariantCulture) ?? ""},");
                sb.AppendLine($"#LSL,{r.Lsl?.ToString(CultureInfo.InvariantCulture) ?? ""},");
                sb.AppendLine($"#SPEC_LINE_NOTE,{Csv(r.SpecLineNote)},");
                break;
            }
        }

        byte[] bytes = Encoding.UTF8.GetBytes("\uFEFF" + sb.ToString());
        return File(bytes, "text/csv; charset=utf-8",
            $"spc-{kind}-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    private static string Csv(string value) =>
        "\"" + (value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
