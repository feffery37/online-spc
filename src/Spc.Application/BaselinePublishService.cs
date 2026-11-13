using Spc.Domain;

namespace Spc.Application;

/// <summary>
/// 审批发布（方案 5.6）：DRAFT → PENDING → ACTIVE → EXPIRED。
/// 冻结服务只产出 PENDING；本服务是唯一能把版本置为 ACTIVE 的入口，且必须带审批人。
/// </summary>
public sealed class BaselinePublishService(ISpcRepository repository, IRealtimePublisher publisher)
{
    public async Task<BaselinePublishResult> PublishAsync(BaselinePublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApprovedBy))
            return new BaselinePublishResult { Ok = false, Error = "控制限变更必须人工审批：请填写审批人（N-06 / 附录 A）。" };

        var version = await repository.GetLimitVersionAsync(request.VersionId, ct);
        if (version is null || version.ChartId != request.ChartId)
            return new BaselinePublishResult { Ok = false, Error = "版本不存在或不属于该控制图。" };
        if (version.Status != (int)LimitVersionStatus.Pending)
            return new BaselinePublishResult
            {
                Ok = false,
                Error = $"版本 V{version.VersionNo} 当前状态为 {version.Status}，只有 PENDING 版本允许发布。"
            };

        var effectiveFrom = request.EffectiveFrom ?? DateTime.UtcNow;
        var outcome = await repository.PublishVersionAsync(
            request.ChartId, request.VersionId, request.ApprovedBy,
            effectiveFrom, request.ChangeReason, ct);

        if (!outcome.Success)
            return new BaselinePublishResult { Ok = false, Error = outcome.Error };

        var published = await repository.GetLimitVersionAsync(request.VersionId, ct);
        await publisher.PublishNoticeAsync(new
        {
            type = "notice",
            at = DateTime.UtcNow,
            message = $"V{published?.VersionNo} 已由 {request.ApprovedBy} 审批发布为 ACTIVE，生效 {effectiveFrom:O}" +
                      (outcome.ExpiredVersionId.HasValue ? $"；旧版本 ID={outcome.ExpiredVersionId} 已置 EXPIRED" : "")
        }, ct);

        return new BaselinePublishResult
        {
            Ok = true,
            Version = published,
            ExpiredVersionId = outcome.ExpiredVersionId,
            Note = "发布在同一事务内完成：旧 ACTIVE 置 EXPIRED 并写失效时刻，新版本置 ACTIVE 并写审批人与生效时刻；" +
                   "历史版本永不删除、计算字段永不修改。"
        };
    }
}
