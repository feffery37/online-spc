using Spc.Domain;

namespace Spc.Application;

public sealed record MesHoldRequest(
    long RuleOutId,
    MesActionType ActionType,
    string ChartCode,
    string LotId,
    string Sn,
    string Equipment,
    string Reason,
    DateTime RequestTime);

public sealed record MesHoldReceipt(
    bool Accepted,
    string MesRef,
    string Status,
    string Operator,
    DateTime? ReleaseTime,
    string RawPayload);

/// <summary>MES 模拟器中一条 Hold 的可视化状态，供 /api/spc/mes/state 查询。</summary>
public sealed record SimulatedHoldState(
    string MesRef,
    long RuleOutId,
    MesActionType ActionType,
    string ChartCode,
    string LotId,
    string Sn,
    string Equipment,
    string Status,
    DateTime RequestTime,
    DateTime? ReleaseTime,
    string Operator);
