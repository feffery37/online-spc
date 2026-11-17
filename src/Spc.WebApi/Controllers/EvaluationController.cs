using Microsoft.AspNetCore.Mvc;
using Spc.Algorithm;
using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi.Controllers;

/// <summary>M3：实时判异、Rule Out 告警；规则集配置（方案 4.3 / 6.3 / 8.2）。</summary>
[ApiController]
[Route("api/spc")]
public sealed class EvaluationController(
    EvaluationService evaluation,
    ISpcRepository repository,
    ILimitReader limitReader,
    RuleSetService ruleSets) : ControllerBase
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
        var resolved = chart is null ? null : await ruleSets.ResolveAsync(chart.RuleSetVer, ct);

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
            ruleSet = resolved is null ? null : new
            {
                version = resolved.Version,
                description = resolved.Description,
                rules = resolved.Rules.Select(x => x.Code()),
                severity = resolved.Severity.Select(k => new
                {
                    rule = k.Key.Code(), level = RuleSetCatalog.SeverityLabel(k.Value)
                })
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

    /// <summary>八条 WECO/Nelson 规则目录与标准阈值（阈值是常量，不开放修改）。</summary>
    [HttpGet("rules")]
    public object Rules() => new
    {
        items = RuleEngine.Descriptions.Select(k => new
        {
            code = k.Key.Code(),
            description = k.Value,
            threshold = Thresholds(k.Key),
            phaseI = EnumText.PhaseIRules.Contains(k.Key)
        }),
        note = "阈值是 WECO/Nelson 标准常量；可配置的只有「启用哪些规则」与「命中后严重级别」"
    };

    [HttpGet("rulesets")]
    public async Task<object> RuleSets(CancellationToken ct) => new
    {
        items = await ruleSets.ListAsync(ct),
        builtInReadOnly = true,
        note = "内置 WECO 预设只读；自定义规则集通过 POST /api/spc/rulesets 保存，" +
               "再用 POST /api/spc/rulesets/bind 绑定到控制图。Phase I 恒用 R1~R4，不受此处影响。"
    };

    [HttpPost("rulesets")]
    public async Task<object> SaveRuleSet([FromBody] RuleSetSaveRequest request, CancellationToken ct)
    {
        var (ok, error, view) = await ruleSets.SaveAsync(request, ct);
        return new { ok, error, item = view };
    }

    [HttpPost("rulesets/bind")]
    public async Task<object> BindRuleSet(
        [FromQuery] long chartId, [FromQuery] string code, CancellationToken ct)
    {
        var (ok, error) = await ruleSets.BindAsync(chartId, code, ct);
        return new { ok, error, chartId, code };
    }

    private static string Thresholds(RuleCode code) => code switch
    {
        RuleCode.R1 => "1 点超出 ±3σ",
        RuleCode.R2 => "连续 9 点位于 CL 同侧",
        RuleCode.R3 => "连续 6 点递增或递减",
        RuleCode.R4 => "连续 14 点上下交替",
        RuleCode.R5 => "3 点中 2 点超出 ±2σ",
        RuleCode.R6 => "5 点中 4 点超出 ±1σ",
        RuleCode.R7 => "连续 15 点全部落在 ±1σ 内",
        RuleCode.R8 => "连续 8 点全部落在 ±1σ 外",
        _ => string.Empty
    };
}
