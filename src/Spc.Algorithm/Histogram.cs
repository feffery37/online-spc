namespace Spc.Algorithm;

public sealed record HistogramBin(int Index, double From, double To, int Count);

public sealed record HistogramResult(
    IReadOnlyList<HistogramBin> Bins,
    double Min,
    double Max,
    double Mean,
    double SigmaOverall,
    int Count,
    double BinWidth);

/// <summary>
/// 能力直方图分箱（方案 8.3：规格线只画在这里，不画在 Xbar 控制图上）。
/// 纯函数，无 IO，便于单测与报表快照复现。
/// </summary>
public static class Histogram
{
    public static HistogramResult Build(IReadOnlyList<double> values, int binCount = 20)
    {
        binCount = Math.Clamp(binCount, 5, 60);
        if (values.Count == 0)
            return new HistogramResult([], 0, 0, 0, 0, 0, 0);

        double min = values.Min(), max = values.Max();
        double mean = values.Average();
        double sigma = values.Count > 1
            ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1))
            : 0.0;

        // 所有值相同时人为展宽，避免零宽分箱。
        if (max - min < 1e-12)
        {
            double pad = Math.Max(Math.Abs(min) * 1e-6, 1e-6);
            min -= pad;
            max += pad;
        }

        double width = (max - min) / binCount;
        var counts = new int[binCount];
        foreach (var v in values)
        {
            int index = (int)Math.Floor((v - min) / width);
            if (index < 0) index = 0;
            if (index >= binCount) index = binCount - 1;
            counts[index]++;
        }

        var bins = counts.Select((c, i) => new HistogramBin(
            i, min + i * width, min + (i + 1) * width, c)).ToArray();
        return new HistogramResult(bins, min, max, mean, sigma, values.Count, width);
    }
}
