using System.Diagnostics;
using System.Text.Json;
using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

public sealed record EvaluationOutcome(
    bool Evaluated,
    string Reason,
    long ChartId,
    long SubgroupId,
    long Seq,
    long? LimitVerId,
    int VersionNo,
    int WindowSize,
    string RuleSetVer,
    IReadOnlyList<SpcRuleOut> RuleOuts,
    TimeSpan Elapsed)
{
    public static EvaluationOutcome Rejected(long chartId, long subgroupId, string reason) =>
        new(false, reason, chartId, subgroupId, 0, null, 0, 0, string.Empty, [], TimeSpan.Zero);
}

public sealed class EvaluationRequest
{
    public long ChartId { get; set; }
    public long? SubgroupId { get; set; }
    /// <summary>重放：对最近 N 个未评估子组逐个判异（不触发聚合）。</summary>
    public int ReplayCount { get; set; }
}

/// <summary>
/// Phase II 实时判异（方案 6.2 / 6.4 / A-06）。
/// 计算与规则评估分离：规则引擎只接受冻结限标准化后的 z 序列与规则集，
/// 可在不重算窗口的前提下更换规则集并重放历史事件（方案 3.1）。
/// 无生效限值版本时**直接拒绝判异**，绝不现算控制限（方案 5.7）。
/// </summary>
public sealed class EvaluationService(
    ISpcRepository repository,
    ILimitReader limitReader,
    IRealtimePublisher publisher,
    IDispositionTrigger? disposition = null)
{
    public async Task<EvaluationOutcome> EvaluateAsync(long chartId, long subgroupId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chart = await repository.GetChartAsync(chartId, ct);
        var subgroup = await repository.GetSubgroupAsync(subgroupId, ct);
        if (chart is null || subgroup is null || subgroup.ChartId != chartId)
            return EvaluationOutcome.Rejected(chartId, subgroupId, "控制图或子组不存在。");

        // ---- 只读契约门禁 ----
        var context = await limitReader.GetLimitContextAsync(chartId, subgroup.EventTimeTo, ct);
        if (context is null)
        {
            await repository.InsertAuditAsync(new SpcAuditLog
            {
                EventTime = subgroup.EventTimeTo,
                EventType = "EVALUATION_REJECTED_NO_LIMIT",
                EntityType = nameof(SpcSubgroup),
                EntityId = subgroupId,
                ChartId = chartId,
                Payload = JsonSerializer.Serialize(new { subgroup.Seq, reason = "事件时刻无生效冻结限值版本" }),
                Actor = "evaluator",
                CreatedAt = DateTime.UtcNow
            }, ct);
            return EvaluationOutcome.Rejected(chartId, subgroupId,
                "该事件时刻无生效的冻结限值版本：拒绝判异，禁止用近期数据现算控制限（方案 5.7）。");
        }

        // 连续类规则的窗口只取「同一限值版本生效区间内」的子组，换版后窗口重置。
        var window = (await repository.GetSubgroupsInVersionWindowAsync(
                chartId, context.EffectiveFrom, context.ExpiredTime, ct))
            .OrderBy(s => s.Seq).ThenBy(s => s.EventTimeTo).ToArray();
        int currentIndex = Array.FindIndex(window, s => s.Id == subgroupId);
        if (currentIndex < 0)
            return EvaluationOutcome.Rejected(chartId, subgroupId, "子组不在该限值版本的生效区间内。");

        var limits = context.Limits;
        var type = (ChartType)chart.ChartType;
        var zMain = window.Select(s => limits.Standardize(s.Xbar)).ToArray();
        var zVar = window.Select(s => limits.StandardizeVariability(s.VariabilityStatisticFor(type))).ToArray();
        if (type == ChartType.IMR && zVar.Length > 0) zVar[0] = 0.0;

        var ruleSet = RuleSetCatalog.Resolve(chart.RuleSetVer);
        var hits = RuleEngine.Evaluate(zMain, ruleSet.Rules)
            .Where(h => h.Index == currentIndex)
            .Select(h => (h.Code, h.Description, Chart: "MAIN", Z: zMain[currentIndex], Stat: window[currentIndex].Xbar))
            .Concat(RuleEngine.Evaluate(zVar, ruleSet.Rules)
                .Where(h => h.Index == currentIndex)
                .Select(h => (h.Code, h.Description, Chart: "VAR", Z: zVar[currentIndex],
                              Stat: window[currentIndex].VariabilityStatisticFor(type))))
            .ToArray();

        // 同一规则在主图/变异图同时命中时保留严重级别更高的一条，避免重复告警。
        var existing = (await repository.GetRuleOutsBySubgroupAsync(subgroupId, ct))
            .Select(r => r.RuleCode).ToHashSet();
        var created = new List<SpcRuleOut>();
        foreach (var group in hits.GroupBy(h => h.Code))
        {
            if (existing.Contains((int)group.Key)) continue;
            var hit = group.OrderByDescending(h => h.Chart == "VAR").First();
            int severity = (int)ruleSet.Severity[group.Key] + (hit.Chart == "VAR" ? 1 : 0);
            severity = Math.Min(severity, (int)Severity.Critical);

            var ruleOut = new SpcRuleOut
            {
                ChartId = chartId,
                SubgroupId = subgroupId,
                LimitVerId = context.VersionId,
                RuleCode = (int)group.Key,
                Severity = severity,
                StatValue = hit.Stat,
                ZValue = hit.Z,
                EventTime = subgroup.EventTimeTo,
                OcapStatus = (int)OcapStatus.Detected,
                LotId = string.Empty,
                Sn = string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            await FillTraceabilityAsync(chartId, subgroupId, ruleOut, ct);
            await repository.InsertRuleOutAsync(ruleOut, ct);
            created.Add(ruleOut);

            await repository.InsertAuditAsync(new SpcAuditLog
            {
                EventTime = ruleOut.EventTime,
                EventType = "RULE_OUT_DETECTED",
                EntityType = nameof(SpcRuleOut),
                EntityId = ruleOut.Id,
                ChartId = chartId,
                Payload = JsonSerializer.Serialize(new
                {
                    ruleOut.RuleCode, ruleOut.Severity, ruleOut.ZValue,
                    LimitVerId = context.VersionId, context.VersionNo,
                    context.DataHash, subgroup.Seq, hit.Chart, ruleSet.Version
                }),
                Actor = "evaluator",
                CreatedAt = DateTime.UtcNow
            }, ct);

            await publisher.PublishRuleOutAsync(new
            {
                type = "ruleout",
                at = DateTime.UtcNow,
                chartId,
                code = chart.Code,
                ruleOutId = ruleOut.Id,
                subgroupId,
                seq = subgroup.Seq,
                eventTime = ruleOut.EventTime,
                ruleCode = group.Key.Code(),
                description = hit.Description,
                severity,
                severityLabel = RuleSetCatalog.SeverityLabel((Severity)severity),
                chartPart = hit.Chart,
                z = hit.Z,
                statValue = hit.Stat,
                limitVerId = context.VersionId,
                versionNo = context.VersionNo,
                dataHash = context.DataHash,
                ruleSetVer = ruleSet.Version,
                message = $"[{RuleSetCatalog.SeverityLabel((Severity)severity)}] {chart.Code} 子组 #{subgroup.Seq} 命中 {group.Key.Code()}：{hit.Description}（V{context.VersionNo}）"
            }, ct);

            // M4：判异产生即触发 OCAP + MES Hold 处置编排（计算 / 规则 / 处置三层解耦）。
            if (disposition is not null)
                await disposition.OnRuleOutAsync(ruleOut.Id, ct);
        }

        await repository.MarkSubgroupEvaluatedAsync(subgroupId, context.VersionId, ct);
        sw.Stop();

        return new EvaluationOutcome(
            true, created.Count > 0 ? $"命中 {created.Count} 条规则" : "受控",
            chartId, subgroupId, subgroup.Seq, context.VersionId, context.VersionNo,
            window.Length, ruleSet.Version, created, sw.Elapsed);
    }

    /// <summary>手动触发 / 历史重放（方案 8.2 的 POST /api/spc/evaluate）。</summary>
    public async Task<IReadOnlyList<EvaluationOutcome>> ReplayAsync(EvaluationRequest request, CancellationToken ct)
    {
        var targets = new List<long>();
        if (request.SubgroupId.HasValue)
        {
            targets.Add(request.SubgroupId.Value);
        }
        else
        {
            int take = Math.Clamp(request.ReplayCount, 1, 200);
            targets.AddRange((await repository.GetUnevaluatedSubgroupsAsync(request.ChartId, take, ct))
                .OrderBy(s => s.Seq).Select(s => s.Id));
        }

        var results = new List<EvaluationOutcome>();
        foreach (var id in targets)
            results.Add(await EvaluateAsync(request.ChartId, id, ct));
        return results;
    }

    private async Task FillTraceabilityAsync(
        long chartId, long subgroupId, SpcRuleOut ruleOut, CancellationToken ct)
    {
        var measures = await repository.GetMeasuresBySubgroupIdsAsync(chartId, [subgroupId], ct);
        var first = measures.OrderBy(m => m.EventTime).FirstOrDefault();
        if (first is null) return;
        ruleOut.LotId = first.LotId;
        ruleOut.Sn = first.Sn;
    }
}

/// <summary>子组统计量的变异图描点值（M3 判异从已落库的子组读取，不重新聚合）。</summary>
public static class SubgroupVariabilityExtensions
{
    public static double VariabilityStatisticFor(this SpcSubgroup s, ChartType type) => type switch
    {
        ChartType.XbarS => s.StddevVal,
        ChartType.IMR => s.MrVal,
        _ => s.RangeVal
    };
}
