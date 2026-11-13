namespace Spc.Algorithm;

/// <summary>
/// 过程能力指数。σ̂ 必须来自冻结限值的 R̄/d₂、s̄/c₄ 或 MR̄/1.128（过程固有波动），
/// 不可用整体样本标准差冒充 Cpk（参考项目 CpkClass.cs 的缺陷）。
/// 前提：过程已处于统计受控状态，否则拒绝输出能力指数（方案 4.4 / 附录 A）。
/// </summary>
public sealed record CapabilityResult(
    bool IsProcessControlled,
    double? Cp,
    double? Cpk,
    double? Cpu,
    double? Cpl,
    double? Pp,
    double? Ppk,
    double SigmaHat,
    double SigmaOverall,
    string Message)
{
    public string Grade => Cpk switch
    {
        null => "不可评定",
        > 1.67 => "特级（能力很充分）",
        > 1.33 => "一级（能力充分）",
        > 1.00 => "二级（能力尚可）",
        > 0.67 => "三级（能力不足）",
        _ => "四级（能力严重不足）"
    };
}

public static class CapabilityCalculator
{
    public static CapabilityResult Compute(
        double? usl, double? lsl, double center,
        double sigmaHat, double sigmaOverall, bool isProcessControlled)
    {
        if (!isProcessControlled)
            return new CapabilityResult(false, null, null, null, null, null, null,
                sigmaHat, sigmaOverall,
                "过程未处于统计受控状态：必须先剔除特殊原因，禁止计算 Cp / Cpk（方案 4.4、附录 A）。");

        if (sigmaHat <= 0 || sigmaOverall <= 0)
            return new CapabilityResult(true, null, null, null, null, null, null,
                sigmaHat, sigmaOverall, "标准差估计无效，无法计算过程能力。");

        double? cpu = usl.HasValue ? (usl.Value - center) / (3 * sigmaHat) : null;
        double? cpl = lsl.HasValue ? (center - lsl.Value) / (3 * sigmaHat) : null;
        double? cpk = cpu.HasValue && cpl.HasValue
            ? Math.Min(cpu.Value, cpl.Value)
            : cpu ?? cpl;
        double? cp = usl.HasValue && lsl.HasValue ? (usl.Value - lsl.Value) / (6 * sigmaHat) : null;

        double? ppu = usl.HasValue ? (usl.Value - center) / (3 * sigmaOverall) : null;
        double? ppl = lsl.HasValue ? (center - lsl.Value) / (3 * sigmaOverall) : null;
        double? ppk = ppu.HasValue && ppl.HasValue
            ? Math.Min(ppu.Value, ppl.Value)
            : ppu ?? ppl;
        double? pp = usl.HasValue && lsl.HasValue ? (usl.Value - lsl.Value) / (6 * sigmaOverall) : null;

        return new CapabilityResult(true, cp, cpk, cpu, cpl, pp, ppk,
            sigmaHat, sigmaOverall, "过程受控，能力指数有效（Cp/Cpk 用 σ̂，Pp/Ppk 用整体标准差）。");
    }
}
