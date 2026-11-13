using Spc.Domain;

namespace Spc.Algorithm;

public sealed record RuleHit(RuleCode Code, int Index, string Description);

/// <summary>
/// WECO / Nelson 八条判异规则。输入必须是由冻结限标准化后的 z 序列，
/// 且严格按事件时间有序（方案 4.3 / N-01）。
/// Phase I 稳定性检验只启用 R1~R4，Phase II 才启用全套八条。
/// </summary>
public static class RuleEngine
{
    public static readonly IReadOnlyDictionary<RuleCode, string> Descriptions =
        new Dictionary<RuleCode, string>
        {
            [RuleCode.R1] = "单点超出 3σ 控制限",
            [RuleCode.R2] = "连续 9 点落在中心线同侧",
            [RuleCode.R3] = "连续 6 点递增或递减",
            [RuleCode.R4] = "连续 14 点上下交替",
            [RuleCode.R5] = "连续 3 点中有 2 点超 2σ 且同侧",
            [RuleCode.R6] = "连续 5 点中有 4 点超 1σ 且同侧",
            [RuleCode.R7] = "连续 15 点全部落在 ±1σ 内",
            [RuleCode.R8] = "连续 8 点无一落在 ±1σ 内"
        };

    public static IReadOnlyList<RuleHit> EvaluatePhaseI(IReadOnlyList<double> z) =>
        Evaluate(z, EnumText.PhaseIRules);

    public static IReadOnlyList<RuleHit> EvaluatePhaseII(IReadOnlyList<double> z) =>
        Evaluate(z, EnumText.PhaseIIRules);

    public static IReadOnlyList<RuleHit> Evaluate(IReadOnlyList<double> z, IEnumerable<RuleCode> enabled)
    {
        ArgumentNullException.ThrowIfNull(z);
        var rules = enabled?.Distinct().ToHashSet() ?? [];
        var hits = new List<RuleHit>();

        for (int i = 0; i < z.Count; i++)
        {
            if (rules.Contains(RuleCode.R1) && Math.Abs(z[i]) > 3.0)
                Add(hits, RuleCode.R1, i);

            if (rules.Contains(RuleCode.R2) && i >= 8 && AllSameSide(z, i - 8, 9))
                Add(hits, RuleCode.R2, i);

            if (rules.Contains(RuleCode.R3) && i >= 5 && Monotonic(z, i - 5, 6))
                Add(hits, RuleCode.R3, i);

            if (rules.Contains(RuleCode.R4) && i >= 13 && Alternating(z, i - 13, 14))
                Add(hits, RuleCode.R4, i);

            if (rules.Contains(RuleCode.R5) && i >= 2 && CountBeyond(z, i - 2, 3, 2.0, out _) >= 2)
                Add(hits, RuleCode.R5, i);

            if (rules.Contains(RuleCode.R6) && i >= 4 && CountBeyond(z, i - 4, 5, 1.0, out _) >= 4)
                Add(hits, RuleCode.R6, i);

            if (rules.Contains(RuleCode.R7) && i >= 14 && AllWithin(z, i - 14, 15, 1.0))
                Add(hits, RuleCode.R7, i);

            if (rules.Contains(RuleCode.R8) && i >= 7 && AllOutside(z, i - 7, 8, 1.0))
                Add(hits, RuleCode.R8, i);
        }

        return hits.OrderBy(h => h.Index).ThenBy(h => h.Code).ToArray();
    }

    private static void Add(List<RuleHit> hits, RuleCode code, int index)
    {
        // 同一规则在连续窗口内会对多个点命中，这里保留全部触发点，
        // 去重只针对「同一规则 + 同一索引」。
        if (!hits.Any(h => h.Code == code && h.Index == index))
            hits.Add(new RuleHit(code, index, Descriptions[code]));
    }

    private static bool AllSameSide(IReadOnlyList<double> z, int start, int length)
    {
        bool positive = true, negative = true;
        for (int k = start; k < start + length; k++)
        {
            if (z[k] <= 0) positive = false;
            if (z[k] >= 0) negative = false;
        }
        return positive || negative;
    }

    private static bool Monotonic(IReadOnlyList<double> z, int start, int length)
    {
        bool up = true, down = true;
        for (int k = start + 1; k < start + length; k++)
        {
            if (z[k] <= z[k - 1]) up = false;
            if (z[k] >= z[k - 1]) down = false;
        }
        return up || down;
    }

    private static bool Alternating(IReadOnlyList<double> z, int start, int length)
    {
        int previous = 0;
        for (int k = start + 1; k < start + length; k++)
        {
            int direction = Math.Sign(z[k] - z[k - 1]);
            if (direction == 0 || direction == previous) return false;
            previous = direction;
        }
        return true;
    }

    /// <summary>窗口内超出 threshold 且同侧的点计数（取正侧/负侧的较大值）。</summary>
    private static int CountBeyond(IReadOnlyList<double> z, int start, int length, double threshold, out int side)
    {
        int above = 0, below = 0;
        for (int k = start; k < start + length; k++)
        {
            if (z[k] > threshold) above++;
            else if (z[k] < -threshold) below++;
        }
        side = above >= below ? 1 : -1;
        return Math.Max(above, below);
    }

    /// <summary>「落在 ±1σ 内」采用 |z| &lt; 1；边界点归入「外」，与 R8 互补且不重不漏。</summary>
    private static bool AllWithin(IReadOnlyList<double> z, int start, int length, double threshold)
    {
        for (int k = start; k < start + length; k++)
            if (Math.Abs(z[k]) >= threshold) return false;
        return true;
    }

    private static bool AllOutside(IReadOnlyList<double> z, int start, int length, double threshold)
    {
        for (int k = start; k < start + length; k++)
            if (Math.Abs(z[k]) < threshold) return false;
        return true;
    }
}
