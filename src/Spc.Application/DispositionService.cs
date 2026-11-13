using System.Text.Json;
using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

/// <summary>
/// M4 处置闭环编排（方案 6.3 / 6.4 / A-07 / A-08）：
/// Rule Out → OCAP 单 → MES LOT/EQP Hold/停机/通知 → 回执回写 → 人工签核 →
/// 验证子组复判 → OCAP CLOSED。
/// 状态机：DETECTED → ALERTED → ACTION_SENT → MES_ACKED → IN_REVIEW → VERIFIED/CLOSED，
/// 异常分支 ESCALATED。任何一环都可从 SPC_AUDIT_LOG 追溯。
/// </summary>
public sealed class DispositionService(
    ISpcRepository repository,
    IMesGateway mesGateway,
    IRealtimePublisher publisher,
    ILimitReader limitReader) : IDispositionTrigger
{
    public Task OnRuleOutAsync(long ruleOutId, CancellationToken ct) => HandleAsync(ruleOutId, ct);

    /// <summary>判异产生后自动开单并下发 Hold；同一 Rule Out 重复触发时幂等返回。</summary>
    public async Task<DispositionResult> HandleAsync(long ruleOutId, CancellationToken ct)
    {
        var ro = await repository.GetRuleOutAsync(ruleOutId, ct);
        if (ro is null) return DispositionResult.Failure("Rule Out 不存在。");

        var existing = await repository.GetOcapByRuleOutAsync(ruleOutId, ct);
        if (existing is not null)
            return new DispositionResult(true, string.Empty, existing.Id, existing.Status, [],
                "该判异已有 OCAP 处置单，跳过重复开单（幂等）。");

        var chart = await repository.GetChartAsync(ro.ChartId, ct);
        if (chart is null) return DispositionResult.Failure("控制图不存在。");

        int recent = await repository.CountRecentRuleOutsAsync(
            ro.ChartId, ro.EventTime.AddMinutes(-OocActionPolicy.RecentWindowMinutes), ct);
        var decision = OocActionPolicy.Decide(
            (RuleCode)ro.RuleCode, (Severity)ro.Severity, recent,
            !string.IsNullOrWhiteSpace(ro.LotId));

        var ocap = new SpcOcap
        {
            RuleOutId = ruleOutId,
            OcapNo = $"OCAP-{DateTime.UtcNow:yyyyMMdd}-{ruleOutId:D6}",
            Status = (int)OcapStatus.Alerted,
            Assignee = "qe.li",
            Cause = string.Empty,
            Countermeasure = string.Empty,
            Decision = string.Empty,
            SignedBy = string.Empty,
            DueTime = DateTime.UtcNow.AddHours(4),
            CreatedAt = DateTime.UtcNow
        };
        await repository.InsertOcapAsync(ocap, ct);
        await repository.UpdateRuleOutOcapStatusAsync(ruleOutId, (int)OcapStatus.Alerted, ct);
        await AuditAsync(ro, ocap.Id, "OCAP_CREATED", new { ocap.OcapNo, ocap.Assignee, ocap.DueTime }, ct);

        var issued = new List<MesActionType>();
        foreach (var type in decision.Actions)
        {
            var action = new SpcMesAction
            {
                RuleOutId = ruleOutId,
                ActionType = (int)type,
                Status = (int)MesActionStatus.Pending,
                Payload = JsonSerializer.Serialize(new
                {
                    ro.ChartId, chart.Code, ro.LotId, ro.Sn, chart.Machine,
                    ro.RuleCode, ro.Severity, decision.Reason
                }),
                MesRef = string.Empty,
                AckPayload = string.Empty,
                RequestTime = DateTime.UtcNow,
                RetryCount = 0,
                CreatedAt = DateTime.UtcNow
            };
            await repository.InsertMesActionAsync(action, ct);

            var request = new MesHoldRequest(
                ruleOutId, type, chart.Code, ro.LotId, ro.Sn, chart.Machine,
                decision.Reason, action.RequestTime);
            MesHoldReceipt receipt;
            try
            {
                receipt = await mesGateway.SendHoldAsync(request, ct);
            }
            catch (Exception ex)
            {
                receipt = new MesHoldReceipt(false, string.Empty, "FAILED", "gateway", null, ex.Message);
            }

            action.MesRef = receipt.MesRef;
            action.AckPayload = JsonSerializer.Serialize(receipt);
            action.Status = receipt.Accepted
                ? (int)MesActionStatus.Sent
                : (int)MesActionStatus.Failed;
            if (!receipt.Accepted) action.RetryCount = 1;
            await repository.UpdateMesActionAsync(action, ct);
            issued.Add(type);

            await AuditAsync(ro, action.Id,
                receipt.Accepted ? "MES_HOLD_SENT" : "MES_HOLD_FAILED",
                new { ActionType = type.ToString(), receipt.MesRef, receipt.Status }, ct);
        }

        ocap.Status = (int)OcapStatus.ActionSent;
        await repository.UpdateOcapAsync(ocap, ct);
        await repository.UpdateRuleOutOcapStatusAsync(ruleOutId, (int)OcapStatus.ActionSent, ct);
        await AuditAsync(ro, ocap.Id, "RULE_OUT_ACTION_SENT",
            new { decision.Reason, decision.RecentCount, Actions = decision.Actions.Select(a => a.ToString()) }, ct);

        await publisher.PublishDispositionAsync(new
        {
            type = "disposition",
            at = DateTime.UtcNow,
            chartId = ro.ChartId,
            ruleOutId,
            ocapId = ocap.Id,
            ocapNo = ocap.OcapNo,
            status = ocap.Status,
            statusLabel = ((OcapStatus)ocap.Status).ToString(),
            actions = decision.Actions.Select(a => a.ToString()),
            reason = decision.Reason,
            message = $"OCAP {ocap.OcapNo} 已开单并下发 MES 动作：{string.Join(" / ", decision.Actions)}"
        }, ct);

        return new DispositionResult(true, string.Empty, ocap.Id, ocap.Status, issued,
            "处置结果必须回写 SPC 与判异记录关联，形成「采集 → 判异 → 处置 → 验证」闭环。");
    }

    /// <summary>MES 回执回写。所有动作都收到回执后才推进到 MES_ACKED（方案 6.3 / A-07）。</summary>
    public async Task<DispositionResult> CallbackAsync(MesCallbackRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MesRef))
            return DispositionResult.Failure("缺少 MES 单据号 MesRef。");

        var action = await repository.GetMesActionByRefAsync(request.MesRef, ct);
        if (action is null) return DispositionResult.Failure($"MES 单据 {request.MesRef} 不存在。");
        if (request.RuleOutId.HasValue && action.RuleOutId != request.RuleOutId.Value)
            return DispositionResult.Failure("回执与判异记录不匹配。");

        var ro = await repository.GetRuleOutAsync(action.RuleOutId, ct);
        var ocap = await repository.GetOcapByRuleOutAsync(action.RuleOutId, ct);
        if (ro is null || ocap is null) return DispositionResult.Failure("判异或 OCAP 不存在。");

        bool rejected = request.Status.Equals("REJECTED", StringComparison.OrdinalIgnoreCase);
        action.Status = rejected ? (int)MesActionStatus.Failed : (int)MesActionStatus.Acked;
        action.AckTime = DateTime.UtcNow;
        action.AckPayload = JsonSerializer.Serialize(request);
        await repository.UpdateMesActionAsync(action, ct);

        if (!string.IsNullOrWhiteSpace(request.Decision))
            ocap.Decision = request.Decision;
        await repository.UpdateOcapAsync(ocap, ct);
        await AuditAsync(ro, action.Id, "MES_CALLBACK",
            new { request.MesRef, request.Status, request.Operator, request.LotStatus,
                  request.EquipmentStatus, request.ReleaseTime, request.Decision }, ct);

        var all = await repository.GetMesActionsByRuleOutAsync(ro.Id, ct);
        bool allAcked = all.Count > 0 && all.All(a => a.Status == (int)MesActionStatus.Acked);
        if (allAcked)
        {
            ocap.Status = (int)OcapStatus.MesAcked;
            await repository.UpdateOcapAsync(ocap, ct);
            await repository.UpdateRuleOutOcapStatusAsync(ro.Id, (int)OcapStatus.MesAcked, ct);
            await AuditAsync(ro, ocap.Id, "MES_ALL_ACKED", new { AckedActions = all.Count }, ct);
        }

        await publisher.PublishDispositionAsync(new
        {
            type = "disposition", at = DateTime.UtcNow,
            chartId = ro.ChartId, ruleOutId = ro.Id, ocapId = ocap.Id,
            status = ocap.Status, statusLabel = ((OcapStatus)ocap.Status).ToString(),
            mesRef = request.MesRef, allAcked,
            message = allAcked ? $"OCAP {ocap.OcapNo} 已收齐 {all.Count} 条 MES 回执" : "部分 MES 回执已回写"
        }, ct);

        return new DispositionResult(true, string.Empty, ocap.Id, ocap.Status, [],
            allAcked ? "全部动作已回执，可进入人工签核。" : "仍有动作等待回执。");
    }

    /// <summary>OCAP 人工签核：原因、对策、不合格品评审结论、签核人。</summary>
    public async Task<DispositionResult> ReviewAsync(OcapReviewRequest request, CancellationToken ct)
    {
        var ocap = await repository.GetOcapAsync(request.OcapId, ct);
        if (ocap is null) return DispositionResult.Failure("OCAP 不存在。");
        if (ocap.Status != (int)OcapStatus.MesAcked)
            return DispositionResult.Failure("必须先收齐全部 MES 回执才允许签核（方案 6.3）。");
        if (string.IsNullOrWhiteSpace(request.SignedBy) || string.IsNullOrWhiteSpace(request.Cause))
            return DispositionResult.Failure("签核必须填写原因与签核人（审计留痕）。");

        var ro = await repository.GetRuleOutAsync(ocap.RuleOutId, ct);
        ocap.Cause = request.Cause;
        ocap.Countermeasure = request.Countermeasure;
        ocap.Decision = request.Decision;
        ocap.SignedBy = request.SignedBy;
        ocap.Status = (int)OcapStatus.InReview;
        await repository.UpdateOcapAsync(ocap, ct);
        await repository.UpdateRuleOutOcapStatusAsync(ocap.RuleOutId, (int)OcapStatus.InReview, ct);
        if (ro is not null)
            await AuditAsync(ro, ocap.Id, "OCAP_REVIEWED",
                new { request.Cause, request.Countermeasure, request.Decision, request.SignedBy }, ct);

        await publisher.PublishDispositionAsync(new
        {
            type = "disposition", at = DateTime.UtcNow, chartId = ro?.ChartId ?? 0,
            ruleOutId = ocap.RuleOutId, ocapId = ocap.Id,
            status = ocap.Status, statusLabel = "IN_REVIEW",
            message = $"OCAP {ocap.OcapNo} 已由 {request.SignedBy} 签核，等待验证子组复判"
        }, ct);

        return new DispositionResult(true, string.Empty, ocap.Id, ocap.Status, [],
            "签核完成，需用 corrective action 之后的验证子组复判受控才能关闭。");
    }

    /// <summary>
    /// 验证子组复判（方案 6.4「采集→判异→处置→验证」）。
    /// 验证子组必须晚于判异事件，且用当时生效的冻结限标准化；单点复判是否回到 3σ 内。
    /// 受控 → CLOSED，仍失控 → ESCALATED（不得假装关闭）。
    /// </summary>
    public async Task<DispositionResult> VerifyAsync(OcapVerifyRequest request, CancellationToken ct)
    {
        var ocap = await repository.GetOcapAsync(request.OcapId, ct);
        if (ocap is null) return DispositionResult.Failure("OCAP 不存在。");
        if (ocap.Status != (int)OcapStatus.InReview)
            return DispositionResult.Failure("OCAP 必须先完成人工签核才能验证关闭。");

        var ro = await repository.GetRuleOutAsync(ocap.RuleOutId, ct);
        if (ro is null) return DispositionResult.Failure("判异记录不存在。");

        var sg = await repository.GetSubgroupAsync(request.VerificationSubgroupId, ct);
        if (sg is null || sg.ChartId != ro.ChartId)
            return DispositionResult.Failure("验证子组不存在或不属于该控制图。");
        if (sg.EventTimeTo <= ro.EventTime)
            return DispositionResult.Failure("验证子组的事件时间必须晚于判异事件（禁止拿旧数据验证）。");

        var context = await limitReader.GetLimitContextAsync(ro.ChartId, sg.EventTimeTo, ct);
        if (context is null)
            return DispositionResult.Failure("验证时刻无生效冻结限值版本，禁止关闭 OCAP（只读契约）。");

        double z = context.Limits.Standardize(sg.Xbar);
        bool controlled = RuleEngine.EvaluatePhaseII([z]).Count == 0;

        ocap.VerificationSubgroupId = sg.Id;
        ocap.Status = controlled ? (int)OcapStatus.Closed : (int)OcapStatus.Escalated;
        if (controlled) ocap.ClosedAt = DateTime.UtcNow;
        await repository.UpdateOcapAsync(ocap, ct);
        await repository.UpdateRuleOutOcapStatusAsync(ro.Id, ocap.Status, ct);
        await AuditAsync(ro, ocap.Id, controlled ? "OCAP_CLOSED" : "OCAP_ESCALATED",
            new { VerificationSubgroupId = sg.Id, sg.Seq, sg.Xbar, z, controlled,
                  LimitVerId = context.VersionId, context.VersionNo }, ct);

        await publisher.PublishDispositionAsync(new
        {
            type = "disposition", at = DateTime.UtcNow, chartId = ro.ChartId,
            ruleOutId = ro.Id, ocapId = ocap.Id, ocapNo = ocap.OcapNo,
            status = ocap.Status, statusLabel = ((OcapStatus)ocap.Status).ToString(),
            controlled, z,
            message = controlled
                ? $"OCAP {ocap.OcapNo} 验证子组复判受控（z={z:F2}），闭环关闭"
                : $"OCAP {ocap.OcapNo} 验证子组仍失控（z={z:F2}），已升级 ESCALATED"
        }, ct);

        return new DispositionResult(true, string.Empty, ocap.Id, ocap.Status, [],
            controlled ? "处置闭环完成：采集 → 判异 → 处置 → 验证 → 关闭。" : "验证未通过，OCAP 升级，禁止关闭。");
    }

    /// <summary>A-08：一次完整追溯要能回答「哪一版限值、谁审批、命中什么规则、触发什么动作、谁处置、结论是什么」。</summary>
    public async Task<DispositionTrace?> GetTraceAsync(long ruleOutId, CancellationToken ct)
    {
        var ro = await repository.GetRuleOutAsync(ruleOutId, ct);
        if (ro is null) return null;

        var ocap = await repository.GetOcapByRuleOutAsync(ruleOutId, ct);
        var actions = await repository.GetMesActionsByRuleOutAsync(ruleOutId, ct);
        var version = await repository.GetLimitVersionAsync(ro.LimitVerId, ct);
        var subgroup = await repository.GetSubgroupAsync(ro.SubgroupId, ct);
        var measures = await repository.GetMeasuresBySubgroupIdsAsync(ro.ChartId, [ro.SubgroupId], ct);
        var audit = await repository.GetAuditLogsAsync(ro.ChartId, ruleOutId, 200, ct);
        return new DispositionTrace(ro, ocap, actions, version, subgroup, measures, audit);
    }

    private async Task AuditAsync(SpcRuleOut ro, long entityId, string eventType, object payload, CancellationToken ct)
        => await repository.InsertAuditAsync(new SpcAuditLog
        {
            EventTime = DateTime.UtcNow,
            EventType = eventType,
            EntityType = eventType.StartsWith("MES_", StringComparison.Ordinal) ? nameof(SpcMesAction) : nameof(SpcOcap),
            EntityId = entityId,
            ChartId = ro.ChartId,
            Payload = JsonSerializer.Serialize(new { RuleOutId = ro.Id, Detail = payload }),
            Actor = "disposition",
            CreatedAt = DateTime.UtcNow
        }, ct);
}
