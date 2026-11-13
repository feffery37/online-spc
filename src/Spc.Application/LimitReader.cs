using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

/// <summary>
/// 把冻结版本行还原成判异所需的 ControlLimits。
/// 注意：全部字段直接来自版本表 / 由冻结限推导，绝不调用 LimitCalculator（方案 5.7）。
/// </summary>
public static class LimitVersionMapper
{
    public static ControlLimits ToControlLimits(SpcLimitVersion v) => new(
        (ChartType)v.ChartType,
        v.SubgroupSize,
        v.Cl, v.Ucl, v.Lcl, v.WarnUcl, v.WarnLcl,
        v.VarCl, v.VarUcl, v.VarLcl,
        v.SigmaHat,
        (v.Ucl - v.Cl) / 3.0,
        (v.VarUcl - v.VarCl) / 3.0,
        AiagConstants.Version);
}

/// <summary>
/// Phase II 只读取限实现：按事件时间从 SPC_LIMIT_VERSION 定位当时生效版本。
/// 无生效版本返回 null —— 调用方必须拒绝判异，不得现算控制限。
/// </summary>
public sealed class FrozenLimitReader(ISpcRepository repository) : ILimitReader
{
    public async Task<ControlLimits?> GetLimitsForEvaluationAsync(
        long chartId, DateTime eventTime, CancellationToken ct)
        => (await GetLimitContextAsync(chartId, eventTime, ct))?.Limits;

    public async Task<LimitContext?> GetLimitContextAsync(
        long chartId, DateTime eventTime, CancellationToken ct)
    {
        var v = await repository.GetEffectiveLimitAsync(chartId, eventTime, ct);
        if (v is null || !v.EffectiveFrom.HasValue) return null;
        return new LimitContext(
            v.Id, v.VersionNo, v.DataHash,
            v.EffectiveFrom.Value, v.ExpiredTime,
            LimitVersionMapper.ToControlLimits(v));
    }

    public async Task<string> DescribeAsync(long chartId, CancellationToken ct)
    {
        var active = await repository.GetActiveLimitAsync(chartId, ct);
        if (active is null)
            return "无 ACTIVE 冻结限值版本：判异未启用，禁止现算控制限（Phase II 只读契约）。";
        return $"ACTIVE 版本 V{active.VersionNo}（ID={active.Id}）· 哈希 {active.DataHash[..12]}… · " +
               $"审批人 {active.ApprovedBy} · 生效 {active.EffectiveFrom:O} · " +
               $"CL={active.Cl:F4} UCL={active.Ucl:F4} LCL={active.Lcl:F4}";
    }
}
