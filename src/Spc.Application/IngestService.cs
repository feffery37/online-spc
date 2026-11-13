using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spc.Domain;

namespace Spc.Application;

/// <summary>
/// 采集接入：统一校验、时间戳归一、幂等去重、PROC_TAG 强制标签、原始点落库、有序入队。
/// 对应方案 3.2 步骤 1 与 F-02 / N-02 / 附录 A。
/// </summary>
public sealed class IngestService(
    ISpcRepository repository,
    IOrderedQueue queue,
    IRealtimePublisher publisher)
{
    public async Task<IngestResult> IngestAsync(IngestBatchRequest request, CancellationToken ct)
    {
        var result = new IngestResult();
        if (request.Items.Count == 0)
        {
            result.Errors.Add("批量为空。");
            return result;
        }

        var charts = (await repository.GetChartsAsync(ct)).ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var accepted = new List<SpcMeasure>();
        var byChart = new Dictionary<long, int>();

        // 断网续传 / 乱序到达：先按事件时间排序，禁止按到达时间直接形成顺序（方案 N-02）。
        foreach (var item in request.Items.OrderBy(i => i.EventTime).ThenBy(i => i.Sn))
        {
            var error = Validate(item, charts, out var chart);
            if (error is not null)
            {
                result.Rejected++;
                if (result.Errors.Count < 20) result.Errors.Add(error);
                continue;
            }

            var eventTime = NormalizeToUtc(item.EventTime);
            var procTag = JsonSerializer.Serialize(item.ProcTag.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value));
            var dedupKey = BuildDedupKey(chart!.Id, eventTime, item.Sn, item.Value, item.Source);

            if (await repository.DedupKeyExistsAsync(dedupKey, ct))
            {
                result.Duplicated++;
                continue;
            }

            var measure = new SpcMeasure
            {
                ChartId = chart.Id,
                EventTime = eventTime,
                MeasureValue = item.Value,
                LotId = item.LotId,
                Sn = item.Sn,
                WorkOrder = item.WorkOrder,
                Shift = item.Shift,
                Recipe = item.Recipe,
                Equipment = item.Equipment,
                ProcTag = procTag,
                Source = item.Source,
                DedupKey = dedupKey,
                IngestTime = DateTime.UtcNow
            };

            await repository.InsertMeasureAsync(measure, ct);
            await repository.InsertAuditAsync(new SpcAuditLog
            {
                EventTime = eventTime,
                EventType = "MEASURE_INGESTED",
                EntityType = nameof(SpcMeasure),
                EntityId = measure.Id,
                ChartId = chart.Id,
                Payload = JsonSerializer.Serialize(new { chart.Code, item.Sn, item.Value, item.ProcTag }),
                Actor = "ingest",
                CreatedAt = DateTime.UtcNow
            }, ct);

            accepted.Add(measure);
            byChart[chart.Id] = byChart.GetValueOrDefault(chart.Id) + 1;
            result.Accepted++;
        }

        foreach (var (chartId, count) in byChart)
            queue.Publish(new MeasureSignal(chartId, DateTime.UtcNow, count));

        if (accepted.Count > 0)
        {
            await publisher.PublishMeasureAsync(new
            {
                type = "measure",
                at = DateTime.UtcNow,
                count = accepted.Count,
                byChart = byChart.Select(k => new { chartId = k.Key, count = k.Value })
            }, ct);
        }

        return result;
    }

    private static string? Validate(
        MeasureIngestItem item, IReadOnlyDictionary<string, SpcChart> charts, out SpcChart? chart)
    {
        chart = null;
        if (string.IsNullOrWhiteSpace(item.ChartCode)) return "缺少 ChartCode。";
        if (!charts.TryGetValue(item.ChartCode, out chart)) return $"控制图 {item.ChartCode} 不存在。";
        if (chart.IsEnabled != 1) return $"控制图 {item.ChartCode} 已停用。";
        if (!double.IsFinite(item.Value)) return "量测值必须是有限数值。";
        if (item.EventTime == default) return "缺少事件时间 EVENT_TIME。";

        // F-02：量测值须携带机台、特性、Recipe、Lot/SN、工单、班次、事件时间及工艺参数标签。
        if (string.IsNullOrWhiteSpace(item.LotId)) return "缺少 LOT_ID。";
        if (string.IsNullOrWhiteSpace(item.Sn)) return "缺少 SN。";
        if (string.IsNullOrWhiteSpace(item.WorkOrder)) return "缺少工单号。";
        if (string.IsNullOrWhiteSpace(item.Shift)) return "缺少班次。";
        if (string.IsNullOrWhiteSpace(item.Recipe)) return "缺少 RECIPE。";
        if (string.IsNullOrWhiteSpace(item.Equipment)) return "缺少机台。";
        if (item.ProcTag.Count == 0) return "缺少 PROC_TAG 工艺参数标签：判异后无法做根因分析（附录 A）。";
        return null;
    }

    private static DateTime NormalizeToUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
    };

    private static string BuildDedupKey(long chartId, DateTime eventTime, string sn, double value, int source)
    {
        var raw = string.Join('|', chartId.ToString(CultureInfo.InvariantCulture),
            eventTime.ToString("O", CultureInfo.InvariantCulture), sn,
            value.ToString("F6", CultureInfo.InvariantCulture), source.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
    }
}
