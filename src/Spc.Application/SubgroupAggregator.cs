using Spc.Algorithm;
using Spc.Domain;
using System.Text.Json;

namespace Spc.Application;

/// <summary>
/// 子组聚合：同一张图的信号由队列串行投递；聚合时按 EVENT_TIME 从库重取未分组点，
/// 配合迟到水位线，保证连续类规则依赖的先后顺序，且断网补传不会乱序（N-01 / N-02 / 6.1）。
/// M3 起每个子组闭合后立即触发 Phase II 判异，判异结果随子组一起推送（A-06）。
/// </summary>
public sealed class SubgroupAggregator(
    ISpcRepository repository,
    IRealtimePublisher publisher,
    ILimitReader limitReader,
    EvaluationService evaluation,
    TimeSpan lateArrivalTolerance)
{
    public async Task<int> ProcessAsync(long chartId, CancellationToken ct)
    {
        var chart = await repository.GetChartAsync(chartId, ct);
        if (chart is null || chart.IsEnabled != 1) return 0;

        var type = (ChartType)chart.ChartType;
        int n = type == ChartType.IMR ? 1 : Math.Clamp(chart.SubgroupSize, 2, 10);
        var watermark = DateTime.UtcNow.Subtract(lateArrivalTolerance);
        var pending = await repository.GetUnassignedMeasuresAsync(chartId, watermark, ct);
        if (pending.Count < n) return 0;

        var ordered = pending.OrderBy(m => m.EventTime).ThenBy(m => m.Id).ToArray();
        var last = await repository.GetLastSubgroupAsync(chartId, ct);
        long seq = last?.Seq ?? 0;
        double? previousXbar = last?.Xbar;
        int closed = 0;

        for (int offset = 0; offset + n <= ordered.Length; offset += n)
        {
            var slice = ordered.Skip(offset).Take(n).ToArray();
            var stat = SubgroupFactory.From(0, 0, slice[^1].EventTime,
                slice.Select(m => m.MeasureValue).ToArray(), previousXbar);

            var subgroup = new SpcSubgroup
            {
                ChartId = chartId,
                Seq = ++seq,
                EventTimeFrom = slice[0].EventTime,
                EventTimeTo = slice[^1].EventTime,
                SampleCount = n,
                Xbar = stat.Xbar,
                RangeVal = stat.RangeVal,
                StddevVal = stat.StddevVal,
                MrVal = stat.MrVal,
                Status = (int)SubgroupStatus.Closed,
                ClosedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            await repository.InsertSubgroupAsync(subgroup, ct);
            await repository.AssignSubgroupAsync(slice.Select(m => m.Id).ToArray(), subgroup.Id, ct);
            await repository.InsertAuditAsync(new SpcAuditLog
            {
                EventTime = subgroup.EventTimeTo,
                EventType = "SUBGROUP_CLOSED",
                EntityType = nameof(SpcSubgroup),
                EntityId = subgroup.Id,
                ChartId = chartId,
                Payload = JsonSerializer.Serialize(new { subgroup.Seq, stat.Xbar, stat.RangeVal }),
                Actor = "aggregator",
                CreatedAt = DateTime.UtcNow
            }, ct);

            // M3：子组闭合即判异，内部只读取当时生效的冻结版本。
            var outcome = await evaluation.EvaluateAsync(chartId, subgroup.Id, ct);
            var limits = await limitReader.GetLimitsForEvaluationAsync(chartId, subgroup.EventTimeTo, ct);

            await publisher.PublishSubgroupAsync(new
            {
                type = "subgroup",
                chartId,
                code = chart.Code,
                id = subgroup.Id,
                seq = subgroup.Seq,
                eventTime = subgroup.EventTimeTo,
                xbar = stat.Xbar,
                range = stat.RangeVal,
                stddev = stat.StddevVal,
                mr = stat.MrVal,
                sampleCount = n,
                chartType = type.Label(),
                limitsActive = limits is not null,
                cl = limits?.Cl,
                ucl = limits?.Ucl,
                lcl = limits?.Lcl,
                warnUcl = limits?.WarnUcl,
                warnLcl = limits?.WarnLcl,
                varCl = limits?.VarCl,
                varUcl = limits?.VarUcl,
                varLcl = limits?.VarLcl,
                evaluated = outcome.Evaluated,
                limitVerId = outcome.LimitVerId,
                versionNo = outcome.VersionNo,
                ruleOuts = outcome.RuleOuts.Select(r => r.RuleCode).ToArray(),
                maxSeverity = outcome.RuleOuts.Count == 0 ? 0 : outcome.RuleOuts.Max(r => r.Severity)
            }, ct);

            previousXbar = stat.Xbar;
            closed++;
        }

        return closed;
    }
}
