using Microsoft.AspNetCore.Mvc;
using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi.Controllers;

/// <summary>M4：OCAP + MES Hold 处置闭环接口（方案 6.3 / 6.4 / 8.2 / A-07 / A-08）。</summary>
[ApiController]
[Route("api/spc")]
public sealed class DispositionController(
    DispositionService disposition,
    ISpcRepository repository,
    SimulatedMesGateway mesSimulator) : ControllerBase
{
    /// <summary>方案 8.2：POST /api/spc/ruleout/callback，MES 处置回执回写。</summary>
    [HttpPost("ruleout/callback")]
    public async Task<object> Callback([FromBody] MesCallbackRequest request, CancellationToken ct)
        => await disposition.CallbackAsync(request, ct);

    /// <summary>一键把某条 Rule Out 的所有 Hold 都按 ACKED 回执（演练入口）。</summary>
    [HttpPost("mes/simulate-callback")]
    public async Task<object> SimulateCallback([FromQuery] long ruleOutId, CancellationToken ct)
    {
        var actions = await repository.GetMesActionsByRuleOutAsync(ruleOutId, ct);
        var results = new List<object>();
        foreach (var a in actions.Where(a => a.Status != (int)MesActionStatus.Acked))
        {
            var receipt = await mesSimulator.ReleaseAsync(a.MesRef, "mes-simulator", ct);
            results.Add(await disposition.CallbackAsync(new MesCallbackRequest
            {
                RuleOutId = ruleOutId,
                MesRef = a.MesRef,
                Status = "ACKED",
                Operator = "mes-simulator",
                LotStatus = "HOLD_RELEASED",
                EquipmentStatus = "UNLOCKED",
                ReleaseTime = receipt?.ReleaseTime,
                Decision = "让步接收（模拟 MES 评审结论）"
            }, ct));
        }
        return new
        {
            ruleOutId,
            callbacks = results.Count,
            results,
            note = "模拟 MES 侧解冻 + 回执回写；生产环境由真实 MES 主动调用 /ruleout/callback"
        };
    }

    [HttpPost("ocap/review")]
    public async Task<object> Review([FromBody] OcapReviewRequest request, CancellationToken ct)
        => await disposition.ReviewAsync(request, ct);

    [HttpPost("ocap/verify")]
    public async Task<object> Verify([FromBody] OcapVerifyRequest request, CancellationToken ct)
        => await disposition.VerifyAsync(request, ct);

    [HttpGet("ocap")]
    public async Task<object> Ocaps([FromQuery] long chartId, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var ocaps = await repository.GetOcapsAsync(chartId, Math.Clamp(take, 1, 500), ct);
        return new
        {
            items = ocaps,
            byStatus = ocaps.GroupBy(o => o.Status)
                .Select(g => new { status = g.Key, label = ((OcapStatus)g.Key).ToString(), count = g.Count() }),
            stateMachine = "DETECTED -> ALERTED -> ACTION_SENT -> MES_ACKED -> IN_REVIEW -> VERIFIED/CLOSED（异常 ESCALATED）"
        };
    }

    [HttpGet("ocap/{id:long}")]
    public async Task<object> OcapDetail(long id, CancellationToken ct)
    {
        var ocap = await repository.GetOcapAsync(id, ct);
        if (ocap is null) return NotFound(new { error = "OCAP 不存在" });
        return new
        {
            ocap,
            statusLabel = ((OcapStatus)ocap.Status).ToString(),
            actions = await repository.GetMesActionsByRuleOutAsync(ocap.RuleOutId, ct),
            trace = await disposition.GetTraceAsync(ocap.RuleOutId, ct)
        };
    }

    [HttpGet("mes/actions")]
    public async Task<object> MesActions([FromQuery] long chartId, [FromQuery] int take = 100, CancellationToken ct = default)
        => new { items = await repository.GetMesActionsAsync(chartId, Math.Clamp(take, 1, 500), ct) };

    [HttpGet("mes/state")]
    public object MesState() => new
    {
        mode = "Simulated",
        holds = mesSimulator.Snapshot(),
        note = "LOT/EQP Hold 由处置服务自动下发；生产切到 HttpMesGateway 对接真实 MES"
    };

    /// <summary>A-08：一次完整追溯应能回答六个问题。</summary>
    [HttpGet("audit/trace")]
    public async Task<object> AuditTrace([FromQuery] long ruleOutId, CancellationToken ct)
    {
        var trace = await disposition.GetTraceAsync(ruleOutId, ct);
        if (trace is null) return NotFound(new { error = "判异记录不存在" });

        var ro = trace.RuleOut;
        var version = trace.LimitVersion;
        var ocap = trace.Ocap;
        bool allAcked = trace.Actions.Count > 0 &&
                        trace.Actions.All(a => a.Status == (int)MesActionStatus.Acked);
        bool closed = ocap?.Status == (int)OcapStatus.Closed;

        return new
        {
            ruleOutId,
            ruleOut = ro,
            ocap,
            ocapStatus = ocap is null ? null : ((OcapStatus)ocap.Status).ToString(),
            mesActions = trace.Actions,
            limitVersion = version,
            subgroup = trace.Subgroup,
            measures = trace.Measures,
            auditEvents = trace.AuditEvents,
            answers = new
            {
                whichLimitVersion = version is null ? "缺失" : $"V{version.VersionNo}（ID={version.Id}，哈希 {version.DataHash}）",
                whichDataset = version is null ? "缺失" : $"{version.SubgroupCount} 个子组，{version.DataFrom:O} ~ {version.DataTo:O}",
                whoApproved = version?.ApprovedBy ?? "未审批",
                whichRule = $"R{ro.RuleCode}（z={ro.ZValue:F3}，严重级别 {ro.Severity}）",
                whatAction = trace.Actions.Select(a => $"{((MesActionType)a.ActionType).ToString()}:{((MesActionStatus)a.Status).ToString()}"),
                whoDisposed = ocap?.SignedBy ?? "未签核",
                conclusion = ocap?.Decision ?? "未评审",
                verification = ocap?.VerificationSubgroupId
            },
            complete = allAcked && closed,
            missing = new[]
            {
                version is null ? "限值版本" : null,
                string.IsNullOrEmpty(version?.ApprovedBy) ? "审批人" : null,
                !allAcked ? "MES 回执" : null,
                string.IsNullOrEmpty(ocap?.SignedBy) ? "处置签核" : null,
                !closed ? "验证关闭" : null
            }.Where(x => x is not null).ToArray(),
            note = "任一环缺失，体系审核都会开不符合项（方案 6.4）"
        };
    }
}
