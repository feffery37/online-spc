using Microsoft.AspNetCore.Mvc;
using Spc.Algorithm;
using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi.Controllers;

/// <summary>M3：实时判异、Rule Out 告警与规则集配置（方案 8.2 / 6.2 / 6.4）。</summary>
[ApiController]
[Route("api/spc")]
public sealed class EvaluationController(
    EvaluationService evaluation,
    ISpcRepository repository,
    ILimitReader limitReader) : ControllerBase
{
    /// <summary>方案 8.2：POST /api/spc/evaluate，触发判异；支持指定子组或重放未评估子组。</summary>
    [HttpPost("evaluate")]
    public async Task<object> Evaluate([FromBody] EvaluationRequest request, CancellationToken ct)
        => new
        {
            purpose = "判异只读取当刻生效的冻结限值版本，禁止现算控制限",
            results = await evaluation.ReplayAsync(request, ct)
        };

    [HttpGet("ruleout")]
    public async Task<object> RuleOuts(
        [FromQuery] long chartId, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var items = await repository.GetRuleOutsAsync(chartId, Math.Clamp(take, 1, 500), ct);
        return new
        {
            items,
            total = items.Count,
            bySeverity = items.GroupBy(r => r.Severity)
                .Select(g => new { severity = g.Key, label = RuleSetCatalog.SeverityLabel((Severity)g.Key), count = g.Count() }),
            ocapNote = "OCAP 状态当前为 DETECTED；M4 接入 LOT/EQP Hold 与处置回执后推进到 CLOSED"
        };
    }

    /// <summary>判异点下钻：量测值、命中规则、限值版本、审批人、处置状态（方案 8.3）。</summary>
    [HttpGet("ruleout/{id:long}")]
    public async Task<object> RuleOutDetail(long id, CancellationToken ct)
    {
        var r = await repository.GetRuleOutAsync(id, ct);
        if (r is null) return NotFound(new { error = "Rule Out 不存在" });

        var subgroup = await repository.GetSubgroupAsync(r.SubgroupId, ct);
        var version = await repository.GetLimitVersionAsync(r.LimitVerId, ct);
        var measures = await repository.GetMeasuresBySubgroupIdsAsync(r.ChartId, [r.SubgroupId], ct);
        var chart = await repository.GetChartAsync(r.ChartId, ct);

        return new
        {
            ruleOut = r,
            ruleCode = ((RuleCode)r.RuleCode).Code(),
            severityLabel = RuleSetCatalog.SeverityLabel((Severity)r.Severity),
            description = RuleEngine.Descriptions[(RuleCode)r.RuleCode],
            ocapStatus = ((OcapStatus)r.OcapStatus).ToString(),
            subgroup,
            measures = measures.OrderBy(m => m.EventTime).Select(m => new
            {
                m.Id, m.EventTime, m.MeasureValue, m.LotId, m.Sn, m.ProcTag
            }),
            limitVersion = version is null ? null : new
            {
                version.Id, version.VersionNo, version.DataHash, version.Cl, version.Ucl, version.Lcl,
                version.SigmaHat, version.ApprovedBy, version.EffectiveFrom, version.ExpiredTime,
                version.Status, version.SubgroupCount
            },
            ruleSet = chart is null ? null : new
            {
                chart.RuleSetVer,
                rules = RuleSetCatalog.Resolve(chart.RuleSetVer).Rules.Select(x => x.Code()),
                description = RuleSetCatalog.Resolve(chart.RuleSetVer).Description
            },
            auditChain = "SPC_MEASURE -> SPC_SUBGROUP -> SPC_LIMIT_VERSION(审批人/哈希) -> SPC_RULE_OUT -> SPC_AUDIT_LOG"
        };
    }

    [HttpGet("alerts")]
    public async Task<object> Alerts([FromQuery] long chartId, CancellationToken ct)
    {
        var items = await repository.GetRuleOutsAsync(chartId, 500, ct);
        var active = await repository.GetActiveLimitAsync(chartId, ct);
        return new
        {
            chartId,
            activeVersion = active is null ? null : new { active.Id, active.VersionNo, active.DataHash },
            total = items.Count,
            critical = items.Count(r => r.Severity >= (int)Severity.Critical),
            major = items.Count(r => r.Severity == (int)Severity.Major),
            warning = items.Count(r => r.Severity <= (int)Severity.Warning),
            latest = items.FirstOrDefault(),
            contract = await limitReader.DescribeAsync(chartId, ct)
        };
    }

    [HttpGet("rulesets")]
    public object RuleSets() => new
    {
        items = RuleSetCatalog.List().Select(s => new
        {
            s.Version, s.Description,
            rules = s.Rules.Select(r => r.Code()),
            severity = s.Severity.Select(k => new { rule = k.Key.Code(), level = RuleSetCatalog.SeverityLabel(k.Value) })
        }),
        note = "Phase I 恒用 R1~R4；Phase II 按 SPC_CHART.RULE_SET_VER 选择子集与严重级别（方案 4.3 / 风险清单）"
    };
}
