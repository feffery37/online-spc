using Spc.Domain;

namespace Spc.Application;

/// <summary>方案 8.2：POST /api/spc/ruleout/callback，MES 处置回执回写。</summary>
public sealed class MesCallbackRequest
{
    public long? RuleOutId { get; set; }
    public string MesRef { get; set; } = string.Empty;
    /// <summary>ACKED / REJECTED</summary>
    public string Status { get; set; } = "ACKED";
    public string Operator { get; set; } = string.Empty;
    public string LotStatus { get; set; } = string.Empty;
    public string EquipmentStatus { get; set; } = string.Empty;
    public DateTime? ReleaseTime { get; set; }
    /// <summary>不合格品评审结论：返工 / 返修 / 让步接收 / 报废</summary>
    public string Decision { get; set; } = string.Empty;
}

public sealed class OcapReviewRequest
{
    public long OcapId { get; set; }
    public string Cause { get; set; } = string.Empty;
    public string Countermeasure { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string SignedBy { get; set; } = string.Empty;
}

public sealed class OcapVerifyRequest
{
    public long OcapId { get; set; }
    /// <summary> corrective action 之后采集的验证子组 ID。</summary>
    public long VerificationSubgroupId { get; set; }
}

public sealed record DispositionResult(
    bool Ok,
    string Error,
    long? OcapId,
    int Status,
    IReadOnlyList<MesActionType> Actions,
    string Note)
{
    public static DispositionResult Failure(string error) =>
        new(false, error, null, 0, [], string.Empty);
}

/// <summary>A-08 全链路追溯快照。</summary>
public sealed record DispositionTrace(
    SpcRuleOut RuleOut,
    SpcOcap? Ocap,
    IReadOnlyList<SpcMesAction> Actions,
    SpcLimitVersion? LimitVersion,
    SpcSubgroup? Subgroup,
    IReadOnlyList<SpcMeasure> Measures,
    IReadOnlyList<SpcAuditLog> AuditEvents);
