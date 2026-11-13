using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

/// <summary>
/// 能力分析（方案 4.4 / 附录 A）：σ̂ 取冻结版本的过程固有波动，整体标准差另算 Pp/Ppk。
/// M5 强化受控门禁：除 Phase I 收敛外，窗口内存在**未闭环**的 Rule Out 时判定过程不稳定，
/// 拒绝输出 Cp/Cpk —— 对不稳定过程计算 Cpk 没有统计意义。
/// </summary>
public sealed class CapabilityService(ISpcRepository repository, ILimitReader limitReader)
{
    public async Task<CapabilityResultDto> ComputeAsync(long chartId, DateTime? eventTime, CancellationToken ct)
    {
        var chart = await repository.GetChartAsync(chartId, ct);
        if (chart is null)
            return new CapabilityResultDto { Available = false, Reason = "控制图不存在。" };

        var at = eventTime ?? DateTime.UtcNow;
        var version = await repository.GetEffectiveLimitAsync(chartId, at, ct);
        if (version is null)
            return new CapabilityResultDto
            {
                Available = false,
                Reason = "该时刻无生效的冻结限值版本：不得现算控制限，也不得计算 Cpk（方案 4.4 / 5.7）。"
            };

        var limits = await limitReader.GetLimitsForEvaluationAsync(chartId, at, ct);
        if (limits is null)
            return new CapabilityResultDto { Available = false, Reason = "冻结限值版本不可用。" };

        // 受控门禁：近 24 小时内不得有未闭环的 Rule Out（附录 A / 方案 4.4）。
        var recentOuts = await repository.GetRuleOutsInWindowAsync(chartId, at.AddDays(-1), at, ct);
        int unresolved = recentOuts.Count(r => r.OcapStatus != (int)OcapStatus.Closed);
        if (unresolved > 0)
            return new CapabilityResultDto
            {
                Available = false,
                LimitVerId = version.Id,
                VersionNo = version.VersionNo,
                Reason = $"过程未处于统计受控状态：近 24 小时存在 {unresolved} 条未闭环 Rule Out，" +
                         "禁止计算 Cp / Cpk，须先完成 OCAP 处置与验证（方案 4.4 / 附录 A）。",
                ControlBasis = "受控前提：Phase I 收敛 + 版本审批生效 + 近期无未闭环判异。"
            };

        var raw = await repository.GetMeasuresAsync(chartId, 500, ct);
        double sigmaOverall = 0;
        if (raw.Count > 1)
        {
            double mean = raw.Average(m => m.MeasureValue);
            sigmaOverall = Math.Sqrt(raw.Sum(m => (m.MeasureValue - mean) * (m.MeasureValue - mean)) / (raw.Count - 1));
        }

        bool phaseIStable = version.Iterations <= 10 && version.SubgroupCount >= 20;
        var indices = CapabilityCalculator.Compute(
            chart.Usl, chart.Lsl, version.Cl, version.SigmaHat, sigmaOverall, phaseIStable);

        return new CapabilityResultDto
        {
            Available = true,
            LimitVerId = version.Id,
            VersionNo = version.VersionNo,
            Indices = indices,
            ControlBasis = "受控前提：Phase I 稳定性迭代（R1~R4）收敛 + 版本经人工审批生效 + " +
                           "近 24 小时无未闭环 Rule Out；σ̂ 来自冻结版本，Pp/Ppk 用整体标准差。"
        };
    }
}
