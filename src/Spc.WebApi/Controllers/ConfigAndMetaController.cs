using Microsoft.AspNetCore.Mvc;
using Spc.Algorithm;
using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi.Controllers;

[ApiController]
public sealed class MetaController(IConfiguration configuration) : ControllerBase
{
    [HttpGet("/")]
    [HttpGet("/api/meta")]
    public object Meta() => new
    {
        product = "Online-SPC · 制造质量数字化闭环系统",
        phase = configuration["Spc:Phase"] ?? "M0/M1/M2/M3/M4/M5",
        provider = configuration["Spc:Provider"] ?? "Sqlite",
        mesGateway = configuration["Spc:MesGateway:Mode"] ?? "Simulated",
        stage = new
        {
            completed = "M0 算法内核 / M1 建模与采集 / M2 Phase I 冻结与审批 / " +
                        "M3 Phase II 实时判异与告警 / M4 OCAP + MES Hold 处置闭环 / " +
                        "M5 报表中心与合规取证",
            pending = "M6 试运行、换版演练、Oracle 分区压测与计数型图扩展"
        },
        architecture = new[]
        {
            "Spc.Domain 零第三方依赖 POCO + 整数枚举",
            "计算(Spc.Algorithm) - 规则(EvaluationService) - 处置(DispositionService) - 报表(ReportService) 分层解耦",
            "IMesGateway 端口：SimulatedMesGateway（本机演练）/ HttpMesGateway（真实 MES）"
        },
        algorithm = new
        {
            constantsVersion = AiagConstants.Version,
            charts = new[] { "Xbar-R", "Xbar-S", "I-MR" },
            phaseIRules = EnumText.PhaseIRules.Select(r => r.Code()),
            phaseIIRules = EnumText.PhaseIIRules.Select(r => r.Code()),
            constants = AiagConstants.All.Select(k => new
            {
                n = k.Key, a2 = k.Value.A2, a3 = k.Value.A3, d3 = k.Value.D3,
                d4 = k.Value.D4, d2 = k.Value.D2, b3 = k.Value.B3, b4 = k.Value.B4, c4 = k.Value.C4
            })
        },
        rules = RuleEngine.Descriptions.Select(k => new { code = k.Key.Code(), description = k.Value }),
        stateMachines = new
        {
            limitVersion = "DRAFT(1) -> PENDING(2) -> ACTIVE(3) -> EXPIRED(4)",
            subgroup = "COLLECTING(1) -> CLOSED(2) -> EVALUATED(3)",
            ocap = "DETECTED -> ALERTED -> ACTION_SENT -> MES_ACKED -> IN_REVIEW -> VERIFIED/CLOSED，异常 ESCALATED"
        },
        limitsContract = "GetLimitsForEvaluation(chartId, eventTime) 只查 SPC_LIMIT_VERSION；无生效版本时判异被拒绝",
        closedLoop = "采集 -> 子组聚合 -> 冻结限判异 -> Rule Out -> OCAP + LOT/EQP Hold -> MES 回执 -> 人工签核 -> 验证复判 -> CLOSED",
        reports = "能力直方图（规格线只画在这里）/ Cp·Cpk·Pp·Ppk / 日周月报表 / 带指纹快照 / 定时推送 / CSV 导出 / 合规自检",
        acceptance = new[]
        {
            "A-01 ~ A-08 八项验收标准全部通过（50 个自动化用例 + 端到端闭环实测）",
            "M5 增强：过程未受控（近 24h 存在未闭环 Rule Out）时拒绝输出 Cpk（方案 4.4 / 附录 A）"
        },
        endpoints = new[]
        {
            "GET  /api/meta",
            "POST /api/spc/measure/ingest | GET /api/spc/chart/query",
            "POST /api/spc/baseline/precheck | /freeze | /publish | /verify-hash",
            "POST /api/spc/evaluate | GET /api/spc/ruleout | /alerts | /rulesets",
            "POST /api/spc/ruleout/callback | /mes/simulate-callback | GET /mes/state",
            "POST /api/spc/ocap/review | /ocap/verify | GET /ocap | /audit/trace",
            "GET  /api/spc/reports/capability | /periods | /compliance | /audit | /snapshots | /schedule | /export",
            "POST /api/spc/reports/generate | /schedule/run",
            "WS   /hubs/spc (SignalR: subgroup / measure / notice / ruleout / disposition / report)"
        }
    };
}

[ApiController]
[Route("api/spc/charts")]
public sealed class ChartsController(ChartService charts, ISpcRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<object> List(CancellationToken ct) => new { items = await repository.GetChartsAsync(ct) };

    [HttpPost]
    public async Task<object> Upsert([FromBody] ChartUpsertRequest request, CancellationToken ct)
    {
        var (ok, error, chart) = await charts.UpsertAsync(request, ct);
        return ok ? (object)new { ok, chart } : new { ok, error };
    }

    [HttpGet("{id:long}/overview")]
    public async Task<object> Overview(long id, CancellationToken ct)
    {
        var chart = await repository.GetChartAsync(id, ct);
        if (chart is null) return NotFound(new { error = "控制图不存在" });
        return new
        {
            chart,
            measures = await repository.CountMeasuresAsync(id, ct),
            subgroups = await repository.CountSubgroupsAsync(id, ct),
            versions = await repository.GetLimitVersionsAsync(id, ct),
            ruleOuts = await repository.GetRuleOutsAsync(id, 100, ct),
            ocaps = await repository.GetOcapsAsync(id, 100, ct),
            reports = await repository.GetReportSnapshotsAsync(id, 20, ct)
        };
    }
}
