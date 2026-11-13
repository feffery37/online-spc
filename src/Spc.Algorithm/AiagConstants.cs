using System.Collections.ObjectModel;

namespace Spc.Algorithm;

/// <summary>AIAG / ISO 8258 控制图常数（方案 4.2 表）。业务代码禁止硬编码这些数值。</summary>
public sealed record ChartConstants(
    int N, double A2, double A3, double D3, double D4, double D2, double B3, double B4, double C4);

public static class AiagConstants
{
    public const string Version = "AIAG-SPC-2ND/ISO8258";

    private static readonly ReadOnlyDictionary<int, ChartConstants> Table =
        new(new Dictionary<int, ChartConstants>
        {
            [2] = new(2, 1.880, 2.659, 0.000, 3.267, 1.128, 0.000, 3.267, 0.7979),
            [3] = new(3, 1.023, 1.954, 0.000, 2.574, 1.693, 0.000, 2.568, 0.8862),
            [4] = new(4, 0.729, 1.628, 0.000, 2.282, 2.059, 0.000, 2.266, 0.9213),
            [5] = new(5, 0.577, 1.427, 0.000, 2.114, 2.326, 0.000, 2.089, 0.9400),
            [6] = new(6, 0.483, 1.287, 0.000, 2.004, 2.534, 0.030, 1.970, 0.9515),
            [7] = new(7, 0.419, 1.182, 0.076, 1.924, 2.704, 0.118, 1.882, 0.9594),
            [8] = new(8, 0.373, 1.099, 0.136, 1.864, 2.847, 0.185, 1.815, 0.9650),
            [9] = new(9, 0.337, 1.032, 0.184, 1.816, 2.970, 0.239, 1.761, 0.9693),
            [10] = new(10, 0.308, 0.975, 0.223, 1.777, 3.078, 0.284, 1.716, 0.9727)
        });

    public static IReadOnlyDictionary<int, ChartConstants> All => Table;

    public static ChartConstants For(int n) =>
        Table.TryGetValue(n, out var c)
            ? c
            : throw new ArgumentOutOfRangeException(nameof(n), n, "控制图常数仅支持子组大小 2~10（I-MR 使用 n=2 的移动极差常数）。");

    /// <summary>I-MR 单值图的 E2 = 3 / d2(n=2)，避免把 2.66 硬编码进业务代码。</summary>
    public static double E2ForIMR => 3.0 / For(2).D2;

    /// <summary>常数表自校验：A2 ≈ 3 / (d2 * sqrt(n))，用于 A-01 验收。</summary>
    public static bool IsConsistent(int n, double tolerance = 0.002)
        => Math.Abs(For(n).A2 - 3.0 / (For(n).D2 * Math.Sqrt(n))) <= tolerance;
}
