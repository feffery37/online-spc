using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

/// <summary>量测写入信号。队列按 ChartId 分区串行，聚合时再按事件时间从库重取（N-01 / N-02）。</summary>
public sealed record MeasureSignal(long ChartId, DateTime EventTime, int Count);

/// <summary>审批发布的事务结果（方案 5.6：同一事务内旧版 EXPIRED + 新版 ACTIVE）。</summary>
public sealed record LimitPublishOutcome(
    bool Success, string Error, long VersionId, long? ExpiredVersionId, string DataHash);

/// <summary>判异取限上下文：版本 ID 是审计关键字段，生效区间用于圈定规则窗口。</summary>
public sealed record LimitContext(
    long VersionId,
    int VersionNo,
    string DataHash,
    DateTime EffectiveFrom,
    DateTime? ExpiredTime,
    ControlLimits Limits);

public interface IOrderedQueue
{
    void Publish(MeasureSignal signal);
    IAsyncEnumerable<MeasureSignal> ReadAsync(CancellationToken ct);
}

public interface IRealtimePublisher
{
    Task PublishSubgroupAsync(object payload, CancellationToken ct);
    Task PublishMeasureAsync(object payload, CancellationToken ct);
    Task PublishNoticeAsync(object payload, CancellationToken ct);

    /// <summary>M3 Rule Out 分级告警推送；默认空实现便于测试桩无需改动。</summary>
    Task PublishRuleOutAsync(object payload, CancellationToken ct) => Task.CompletedTask;

    /// <summary>M4 处置状态变更推送（OCAP / MES Hold / 回执 / 关闭）。</summary>
    Task PublishDispositionAsync(object payload, CancellationToken ct) => Task.CompletedTask;

    /// <summary>M5 定时报表生成与推送。</summary>
    Task PublishReportAsync(object payload, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// M4：Rule Out 产生后的处置触发器。判异服务只发通知，具体动作由处置服务编排，
/// 保持「计算 - 规则 - 处置」三层解耦（方案 3.1）。
/// </summary>
public interface IDispositionTrigger
{
    Task OnRuleOutAsync(long ruleOutId, CancellationToken ct);
}

/// <summary>
/// MES 网关端口。开发态用内置模拟器，生产态用 HTTP 适配器对接真实 MES，
/// 业务编排不感知具体实现（方案 6.3 / 8.1）。
/// </summary>
public interface IMesGateway
{
    Task<MesHoldReceipt> SendHoldAsync(MesHoldRequest request, CancellationToken ct);
    Task<MesHoldReceipt?> QueryAsync(string mesRef, CancellationToken ct);
    Task<MesHoldReceipt?> ReleaseAsync(string mesRef, string operatorName, CancellationToken ct);
}

/// <summary>
/// Phase II 判异的唯一取限入口。实现只查询限值版本表，禁止调用 LimitCalculator（方案 5.7）。
/// 无生效版本时返回 null，由调用方拒绝判异，绝不临时用近期数据现算。
/// </summary>
public interface ILimitReader
{
    Task<ControlLimits?> GetLimitsForEvaluationAsync(long chartId, DateTime eventTime, CancellationToken ct);
    Task<LimitContext?> GetLimitContextAsync(long chartId, DateTime eventTime, CancellationToken ct);
    Task<string> DescribeAsync(long chartId, CancellationToken ct);
}

public interface ISpcRepository
{
    Task<IReadOnlyList<SpcChart>> GetChartsAsync(CancellationToken ct);
    Task<SpcChart?> GetChartAsync(long chartId, CancellationToken ct);
    Task<SpcChart?> GetChartByCodeAsync(string code, CancellationToken ct);
    Task<long> InsertChartAsync(SpcChart chart, CancellationToken ct);
    Task UpdateChartAsync(SpcChart chart, CancellationToken ct);

    Task<bool> DedupKeyExistsAsync(string dedupKey, CancellationToken ct);
    Task<long> InsertMeasureAsync(SpcMeasure measure, CancellationToken ct);
    Task<IReadOnlyList<SpcMeasure>> GetUnassignedMeasuresAsync(long chartId, DateTime watermark, CancellationToken ct);
    Task AssignSubgroupAsync(IReadOnlyList<long> measureIds, long subgroupId, CancellationToken ct);
    Task<SpcSubgroup?> GetLastSubgroupAsync(long chartId, CancellationToken ct);
    Task<long> InsertSubgroupAsync(SpcSubgroup subgroup, CancellationToken ct);
    Task<IReadOnlyList<SpcSubgroup>> GetSubgroupsAsync(long chartId, int take, CancellationToken ct);
    Task<IReadOnlyList<SpcMeasure>> GetMeasuresAsync(long chartId, int take, CancellationToken ct);
    Task<long> CountMeasuresAsync(long chartId, CancellationToken ct);
    Task<long> CountSubgroupsAsync(long chartId, CancellationToken ct);
    Task<long> InsertAuditAsync(SpcAuditLog log, CancellationToken ct);

    // ---- M2 Phase I 冻结基线 ----
    Task<IReadOnlyList<SpcSubgroup>> GetClosedSubgroupsInWindowAsync(
        long chartId, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<SpcMeasure>> GetMeasuresBySubgroupIdsAsync(
        long chartId, IReadOnlyList<long> subgroupIds, CancellationToken ct);
    Task<long> GetMaxVersionNoAsync(long chartId, CancellationToken ct);
    Task<long> InsertLimitVersionAsync(SpcLimitVersion version, CancellationToken ct);
    Task InsertBaselineExcludesAsync(IReadOnlyList<SpcBaselineExclude> rows, CancellationToken ct);
    Task<SpcLimitVersion?> GetLimitVersionAsync(long versionId, CancellationToken ct);
    Task<SpcLimitVersion?> GetActiveLimitAsync(long chartId, CancellationToken ct);
    Task<SpcLimitVersion?> GetEffectiveLimitAsync(long chartId, DateTime eventTime, CancellationToken ct);
    Task<IReadOnlyList<SpcLimitVersion>> GetLimitVersionsAsync(long chartId, CancellationToken ct);
    Task<LimitPublishOutcome> PublishVersionAsync(
        long chartId, long versionId, string approvedBy,
        DateTime effectiveFrom, string changeReason, CancellationToken ct);

    // ---- M3 Phase II 实时判异 ----
    Task<SpcSubgroup?> GetSubgroupAsync(long subgroupId, CancellationToken ct);
    Task<IReadOnlyList<SpcSubgroup>> GetSubgroupsInVersionWindowAsync(
        long chartId, DateTime from, DateTime? to, CancellationToken ct);
    Task<IReadOnlyList<SpcSubgroup>> GetUnevaluatedSubgroupsAsync(long chartId, int take, CancellationToken ct);
    Task MarkSubgroupEvaluatedAsync(long subgroupId, long limitVerId, CancellationToken ct);
    Task<long> InsertRuleOutAsync(SpcRuleOut ruleOut, CancellationToken ct);
    Task<SpcRuleOut?> GetRuleOutAsync(long ruleOutId, CancellationToken ct);
    Task<IReadOnlyList<SpcRuleOut>> GetRuleOutsAsync(long chartId, int take, CancellationToken ct);
    Task<IReadOnlyList<SpcRuleOut>> GetRuleOutsBySubgroupAsync(long subgroupId, CancellationToken ct);

    // ---- M4 OCAP + MES Hold 处置闭环 ----
    Task<long> InsertOcapAsync(SpcOcap ocap, CancellationToken ct);
    Task UpdateOcapAsync(SpcOcap ocap, CancellationToken ct);
    Task<SpcOcap?> GetOcapAsync(long ocapId, CancellationToken ct);
    Task<SpcOcap?> GetOcapByRuleOutAsync(long ruleOutId, CancellationToken ct);
    Task<IReadOnlyList<SpcOcap>> GetOcapsAsync(long chartId, int take, CancellationToken ct);
    Task<long> InsertMesActionAsync(SpcMesAction action, CancellationToken ct);
    Task UpdateMesActionAsync(SpcMesAction action, CancellationToken ct);
    Task<SpcMesAction?> GetMesActionByRefAsync(string mesRef, CancellationToken ct);
    Task<IReadOnlyList<SpcMesAction>> GetMesActionsByRuleOutAsync(long ruleOutId, CancellationToken ct);
    Task<IReadOnlyList<SpcMesAction>> GetMesActionsAsync(long chartId, int take, CancellationToken ct);
    Task UpdateRuleOutOcapStatusAsync(long ruleOutId, int status, CancellationToken ct);
    Task<int> CountRecentRuleOutsAsync(long chartId, DateTime since, CancellationToken ct);
    Task<IReadOnlyList<SpcAuditLog>> GetAuditLogsAsync(long chartId, long? entityId, int take, CancellationToken ct);

    // ---- M5 报表中心与合规取证 ----
    Task<IReadOnlyList<SpcMeasure>> GetMeasuresInWindowAsync(
        long chartId, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<SpcRuleOut>> GetRuleOutsInWindowAsync(
        long chartId, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<SpcBaselineExclude>> GetBaselineExcludesAsync(long chartId, CancellationToken ct);
    Task<IReadOnlyList<SpcAuditLog>> GetAuditLogsInWindowAsync(
        long chartId, DateTime from, DateTime to, int take, CancellationToken ct);
    Task<long> InsertReportSnapshotAsync(SpcReportSnapshot snapshot, CancellationToken ct);
    Task<IReadOnlyList<SpcReportSnapshot>> GetReportSnapshotsAsync(long chartId, int take, CancellationToken ct);
}
