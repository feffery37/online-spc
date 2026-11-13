using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

/// <summary>
/// M5 报表中心（方案 2.2 F-06 / 8.3 / 9 M5）：能力直方图与 Cp/Cpk、日周月周期报表、
/// 合规取证包、带哈希的报表快照。σ̂ 一律取冻结版本且按事件时间回放，过程未受控拒绝输出 Cpk。
/// </summary>
public sealed class ReportService(
    ISpcRepository repository,
    CapabilityService capabilityService,
    IRealtimePublisher publisher)
{
    public async Task<CapabilityReportDto> BuildCapabilityAsync(
        long chartId, DateTime? from, DateTime? to, int bins, CancellationToken ct)
    {
        var chart = await repository.GetChartAsync(chartId, ct);
        if (chart is null) return new CapabilityReportDto { ChartId = chartId };

        var end = to ?? DateTime.UtcNow;
        var start = from ?? end.AddDays(-7);
        var measures = await repository.GetMeasuresInWindowAsync(chartId, start, end, ct);
        var values = measures.Select(m => m.MeasureValue).ToArray();

        return new CapabilityReportDto
        {
            ChartId = chartId,
            ChartCode = chart.Code,
            From = start,
            To = end,
            SampleCount = values.Length,
            Histogram = Histogram.Build(values, bins),
            Usl = chart.Usl,
            Lsl = chart.Lsl,
            Target = chart.Target,
            Unit = chart.Unit,
            Capability = await capabilityService.ComputeAsync(chartId, end, ct)
        };
    }

    public async Task<PeriodReportDto> BuildPeriodsAsync(
        long chartId, string granularity, int count, CancellationToken ct)
    {
        granularity = granularity.ToLowerInvariant() switch
        {
            "week" => "week", "month" => "month", _ => "day"
        };
        count = Math.Clamp(count, 1, 24);
        var chart = await repository.GetChartAsync(chartId, ct);
        var end = DateTime.UtcNow;
        var start = granularity switch
        {
            "week" => StartOfWeek(end).AddDays(-7 * (count - 1)),
            "month" => new DateTime(end.Year, end.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(count - 1)),
            _ => end.Date.AddDays(-(count - 1))
        };

        var measures = await repository.GetMeasuresInWindowAsync(chartId, start, end.AddDays(1), ct);
        var ruleOuts = await repository.GetRuleOutsInWindowAsync(chartId, start, end.AddDays(1), ct);
        var buckets = BucketBoundaries(granularity, start, end, count);
        var periods = new List<PeriodCapability>();

        foreach (var (label, from, to) in buckets)
        {
            var values = measures.Where(m => m.EventTime >= from && m.EventTime < to)
                .Select(m => m.MeasureValue).ToArray();
            double mean = values.Length > 0 ? values.Average() : 0;
            double sigma = values.Length > 1
                ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1))
                : 0;

            // 按周期结束时刻回放当时生效的冻结版本（方案 5.7）。
            var version = await repository.GetEffectiveLimitAsync(chartId, to.AddSeconds(-1), ct);
            int openOuts = ruleOuts.Count(r =>
                r.EventTime >= from && r.EventTime < to &&
                r.OcapStatus != (int)OcapStatus.Closed);
            bool controlled = version is not null && openOuts == 0 && values.Length > 0;
            string gate = version is null
                ? "该周期无生效冻结版本，禁止计算 Cpk"
                : openOuts > 0
                    ? $"存在 {openOuts} 条未闭环 Rule Out，过程未受控，禁止计算 Cpk"
                    : "Phase I 收敛 + 本周期无未闭环判异";

            var indices = version is null || chart is null || sigma <= 0
                ? null
                : CapabilityCalculator.Compute(
                    chart.Usl, chart.Lsl, version.Cl, version.SigmaHat, sigma, controlled);

            periods.Add(new PeriodCapability(
                label, from, to, values.Length, mean, sigma,
                indices?.Cp, indices?.Cpk, indices?.Pp, indices?.Ppk,
                controlled, gate, version?.VersionNo));
        }

        return new PeriodReportDto
        {
            Granularity = granularity,
            From = start,
            To = end,
            Periods = periods,
            Note = "Cp/Cpk 使用按周期结束时刻回放的冻结 σ̂；Pp/Ppk 使用当期整体标准差；" +
                   "过程未受控时能力指数一律为空（方案 4.4 / 附录 A）。"
        };
    }

    /// <summary>体系审核取证包：把审核员要问的证据一次性打包并给出自检清单。</summary>
    public async Task<CompliancePackageDto> BuildComplianceAsync(long chartId, CancellationToken ct)
    {
        var chart = await repository.GetChartAsync(chartId, ct);
        var versions = await repository.GetLimitVersionsAsync(chartId, ct);
        var excludes = await repository.GetBaselineExcludesAsync(chartId, ct);
        var ruleOuts = await repository.GetRuleOutsAsync(chartId, 500, ct);
        var ocaps = await repository.GetOcapsAsync(chartId, 500, ct);
        var mesActions = await repository.GetMesActionsAsync(chartId, 500, ct);
        var audit = await repository.GetAuditLogsAsync(chartId, null, 1000, ct);
        var snapshots = await repository.GetReportSnapshotsAsync(chartId, 500, ct);

        bool anyVersion = versions.Count > 0;
        bool hashed = anyVersion && versions.All(v => v.DataHash.Length == 64);
        bool approved = anyVersion && versions.All(v => !string.IsNullOrWhiteSpace(v.ApprovedBy) ||
                                                        v.Status == (int)LimitVersionStatus.Pending);
        bool activeUnique = versions.Count(v => v.Status == (int)LimitVersionStatus.Active) <= 1;
        bool ruleOutHasVersion = ruleOuts.All(r => r.LimitVerId > 0);
        bool everyOutHasOcap = ruleOuts.All(r => ocaps.Any(o => o.RuleOutId == r.Id));
        bool everyOcapClosed = ocaps.Count > 0 && ocaps.All(o => o.Status == (int)OcapStatus.Closed);
        bool mesAcked = mesActions.All(a => a.Status == (int)MesActionStatus.Acked);
        bool excludeLedgerComplete = excludes.All(e =>
            !string.IsNullOrWhiteSpace(e.Reason) && !string.IsNullOrWhiteSpace(e.ConfirmedBy));

        var checklist = new[]
        {
            new ComplianceCheckItem("控制限版本化且带数据集指纹", hashed,
                $"{versions.Count} 个版本，{versions.Count(v => v.DataHash.Length == 64)} 个带 SHA256"),
            new ComplianceCheckItem("版本经人工审批发布", approved && anyVersion,
                $"ACTIVE {versions.Count(v => v.Status == (int)LimitVersionStatus.Active)} 个，审批人 {string.Join(",", versions.Where(v => !string.IsNullOrEmpty(v.ApprovedBy)).Select(v => v.ApprovedBy).Distinct())}"),
            new ComplianceCheckItem("一图最多一个 ACTIVE 版本", activeUnique,
                $"ACTIVE 数量 {versions.Count(v => v.Status == (int)LimitVersionStatus.Active)}"),
            new ComplianceCheckItem("规格限与控制限物理分离", chart is not null,
                $"SPC_CHART.USL/LSL={chart?.Usl}/{chart?.Lsl}；控制限仅在 SPC_LIMIT_VERSION"),
            new ComplianceCheckItem("无理由删点禁止，剔除台账完整", excludeLedgerComplete,
                $"剔除台账 {excludes.Count} 条，全部含特殊原因与确认人"),
            new ComplianceCheckItem("判异事件携带 LIMIT_VER_ID", ruleOutHasVersion,
                $"Rule Out {ruleOuts.Count} 条"),
            new ComplianceCheckItem("每条 Rule Out 均有 OCAP 处置单", everyOutHasOcap,
                $"OCAP {ocaps.Count} 张"),
            new ComplianceCheckItem("MES 回执齐全且处置闭环", mesAcked && (everyOcapClosed || ocaps.Count == 0),
                $"MES 动作 {mesActions.Count} 条，已回执 {mesActions.Count(a => a.Status == (int)MesActionStatus.Acked)} 条；已关闭 OCAP {ocaps.Count(o => o.Status == (int)OcapStatus.Closed)} 张"),
            new ComplianceCheckItem("审计事件全链路留痕", audit.Count > 0,
                $"审计事件 {audit.Count} 条，报表快照 {snapshots.Count} 份")
        };

        return new CompliancePackageDto
        {
            ChartId = chartId,
            ChartCode = chart?.Code ?? string.Empty,
            GeneratedAt = DateTime.UtcNow,
            Chart = chart,
            Versions = versions,
            Exclusions = excludes,
            RuleOutCount = ruleOuts.Count,
            OcapCount = ocaps.Count,
            MesActionCount = mesActions.Count,
            AuditEventCount = audit.Count,
            SnapshotCount = snapshots.Count,
            Checklist = checklist,
            Ready = checklist.All(c => c.Pass),
            Note = "任一环缺失，体系审核都会开不符合项（方案 6.4 / 12 A-08）。"
        };
    }

    public async Task<AuditReportDto> BuildAuditAsync(
        long chartId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var end = to ?? DateTime.UtcNow;
        var start = from ?? end.AddDays(-7);
        var events = await repository.GetAuditLogsInWindowAsync(chartId, start, end, 1000, ct);
        return new AuditReportDto
        {
            ChartId = chartId,
            From = start,
            To = end,
            Total = events.Count,
            Events = events,
            ByType = events.GroupBy(e => e.EventType)
                .Select(g => new { type = g.Key, count = g.Count() }).ToArray()
        };
    }

    /// <summary>生成带 SHA256 指纹的报表快照（只增不改），供定时推送与取证复核。</summary>
    public async Task<SpcReportSnapshot> GenerateSnapshotAsync(
        long chartId, string reportType, string generatedBy, CancellationToken ct)
    {
        reportType = reportType.ToUpperInvariant() switch
        {
            "WEEKLY" => "WEEKLY", "MONTHLY" => "MONTHLY", "COMPLIANCE" => "COMPLIANCE", _ => "DAILY"
        };
        int lookbackDays = reportType switch { "WEEKLY" => 7, "MONTHLY" => 30, _ => 1 };
        var end = DateTime.UtcNow;
        var start = end.AddDays(-lookbackDays);

        var payload = new
        {
            schema = "SPC-REPORT-V1",
            chartId,
            reportType,
            from = start,
            to = end,
            capability = await BuildCapabilityAsync(chartId, start, end, 20, ct),
            periods = await BuildPeriodsAsync(chartId, reportType == "MONTHLY" ? "month" : "day", 7, ct),
            compliance = await BuildComplianceAsync(chartId, ct)
        };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        var snapshot = new SpcReportSnapshot
        {
            ChartId = chartId,
            ReportType = reportType,
            PeriodFrom = start,
            PeriodTo = end,
            Payload = json,
            DataHash = hash,
            GeneratedBy = generatedBy,
            GeneratedAt = end
        };
        await repository.InsertReportSnapshotAsync(snapshot, ct);
        await repository.InsertAuditAsync(new SpcAuditLog
        {
            EventTime = end,
            EventType = "REPORT_GENERATED",
            EntityType = nameof(SpcReportSnapshot),
            EntityId = snapshot.Id,
            ChartId = chartId,
            Payload = JsonSerializer.Serialize(new { reportType, start, end, hash }),
            Actor = generatedBy,
            CreatedAt = end
        }, ct);

        await publisher.PublishReportAsync(new
        {
            type = "report",
            at = end,
            chartId,
            snapshotId = snapshot.Id,
            reportType,
            dataHash = hash,
            period = $"{start:yyyy-MM-dd HH:mm} ~ {end:yyyy-MM-dd HH:mm}",
            message = $"已生成 {reportType} 报表快照，指纹 {hash[..12]}…"
        }, ct);

        return snapshot;
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // 周一为一周起点
        return d.Date.AddDays(-diff);
    }

    private static IReadOnlyList<(string Label, DateTime From, DateTime To)> BucketBoundaries(
        string granularity, DateTime start, DateTime end, int count)
    {
        var list = new List<(string, DateTime, DateTime)>();
        for (int i = 0; i < count; i++)
        {
            DateTime from, to;
            string label;
            if (granularity == "month")
            {
                from = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(i);
                to = from.AddMonths(1);
                label = from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            }
            else if (granularity == "week")
            {
                from = StartOfWeek(end).AddDays(-7 * (count - 1 - i));
                to = from.AddDays(7);
                label = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 周";
            }
            else
            {
                from = start.Date.AddDays(i);
                to = from.AddDays(1);
                label = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            list.Add((label, DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.SpecifyKind(to, DateTimeKind.Utc)));
        }
        return list;
    }
}
