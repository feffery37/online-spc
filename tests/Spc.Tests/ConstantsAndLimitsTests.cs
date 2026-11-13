using Spc.Algorithm;
using Spc.Domain;
using Xunit;

namespace Spc.Tests;

/// <summary>A-01：控制限计算与 AIAG 标准常数手算结果一致，误差小于 0.0001。</summary>
public class ConstantsAndLimitsTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);

    private static List<(long Id, long Seq, DateTime EventTime, IReadOnlyList<double> Values)> MakeGroups(
        int count, int n, Func<int, int, double> value)
        => Enumerable.Range(0, count)
            .Select(i => ((long)(i + 1), (long)(i + 1), T0.AddMinutes(i),
                (IReadOnlyList<double>)Enumerable.Range(0, n).Select(j => value(i, j)).ToArray()))
            .ToList();

    [Theory]
    [InlineData(2, 1.880, 3.267, 1.128, 0.7979)]
    [InlineData(3, 1.023, 2.574, 1.693, 0.8862)]
    [InlineData(4, 0.729, 2.282, 2.059, 0.9213)]
    [InlineData(5, 0.577, 2.114, 2.326, 0.9400)]
    [InlineData(6, 0.483, 2.004, 2.534, 0.9515)]
    [InlineData(7, 0.419, 1.924, 2.704, 0.9594)]
    [InlineData(8, 0.373, 1.864, 2.847, 0.9650)]
    [InlineData(9, 0.337, 1.816, 2.970, 0.9693)]
    [InlineData(10, 0.308, 1.777, 3.078, 0.9727)]
    public void A01_ConstantsMatchDocument(int n, double a2, double d4, double d2, double c4)
    {
        var c = AiagConstants.For(n);
        Assert.Equal(a2, c.A2, 3);
        Assert.Equal(d4, c.D4, 3);
        Assert.Equal(d2, c.D2, 3);
        Assert.Equal(c4, c.C4, 4);
        Assert.True(AiagConstants.IsConsistent(n), "A2 应等于 3/(d2*sqrt(n))");
    }

    [Fact]
    public void A01_XbarRLimitsMatchManualComputation()
    {
        var raw = MakeGroups(25, 5, (i, j) => 10.0 + 0.08 * Math.Sin((i + 1) * 0.7 + (j + 1) * 1.3));
        var stats = SubgroupFactory.FromCandidates(raw);

        double xbarbar = raw.Average(g => g.Values.Average());
        double rbar = raw.Average(g => g.Values.Max() - g.Values.Min());
        const double a2 = 0.577, d4 = 2.114, d3 = 0.0, d2 = 2.326;

        var limits = LimitCalculator.Compute(ChartType.XbarR, 5, stats);

        Assert.Equal(xbarbar, limits.Cl, 9);
        Assert.Equal(xbarbar + a2 * rbar, limits.Ucl, 9);
        Assert.Equal(xbarbar - a2 * rbar, limits.Lcl, 9);
        Assert.Equal(rbar, limits.VarCl, 9);
        Assert.Equal(d4 * rbar, limits.VarUcl, 9);
        Assert.Equal(d3 * rbar, limits.VarLcl, 9);
        Assert.Equal(rbar / d2, limits.SigmaHat, 9);
        Assert.Equal(2 * (limits.Ucl - limits.Cl) / 3 + limits.Cl, limits.WarnUcl, 9);
        Assert.Equal(3.0, limits.Standardize(limits.Ucl), 9); // UCL 恰好是 3σ
    }

    /// <summary>复现方案 13.2 的自测数值：CL=9.9981、R̄=0.2279、σ̂=0.0980。</summary>
    [Fact]
    public void A01_DocumentSelfTestNumbersAreReproducible()
    {
        double[] offsets = [-0.11395, -0.05, 0.0, 0.05, 0.11395];
        var raw = MakeGroups(25, 5, (_, j) => 9.9981 + offsets[j]);
        var limits = LimitCalculator.Compute(ChartType.XbarR, 5, SubgroupFactory.FromCandidates(raw));

        Assert.Equal(9.9981, limits.Cl, 4);
        Assert.Equal(0.2279, limits.VarCl, 4);
        Assert.Equal(0.0980, limits.SigmaHat, 4);
        Assert.InRange(limits.Ucl, 10.1293, 10.1298); // 文档值 10.1295（文档 R̄ 为四舍五入值）
        Assert.InRange(limits.Lcl, 9.8664, 9.8669);   // 文档值 9.8666
    }

    [Fact]
    public void A01_XbarSLimitsMatchManualComputation()
    {
        var raw = MakeGroups(25, 5, (i, j) => 10.0 + 0.08 * Math.Sin((i + 1) * 0.7 + (j + 1) * 1.3));
        var stats = SubgroupFactory.FromCandidates(raw);
        double sbar = stats.Average(s => s.StddevVal);
        double xbarbar = stats.Average(s => s.Xbar);

        var limits = LimitCalculator.Compute(ChartType.XbarS, 5, stats);

        Assert.Equal(xbarbar + 1.427 * sbar, limits.Ucl, 9);
        Assert.Equal(2.089 * sbar, limits.VarUcl, 9);
        Assert.Equal(0.0, limits.VarLcl, 9); // B3(5)=0
        Assert.Equal(sbar / 0.9400, limits.SigmaHat, 9);
    }

    [Fact]
    public void A01_IMRLimitsMatchManualComputation()
    {
        var raw = MakeGroups(30, 1, (i, _) => 10.0 + 0.06 * Math.Sin(i * 0.8));
        var stats = SubgroupFactory.FromCandidates(raw);
        double xbar = stats.Average(s => s.Xbar);
        double mrbar = stats.Skip(1).Average(s => s.MrVal);

        var limits = LimitCalculator.Compute(ChartType.IMR, 1, stats);

        Assert.Equal(xbar + 3.0 * mrbar / 1.128, limits.Ucl, 9);
        Assert.Equal(3.267 * mrbar, limits.VarUcl, 9);
        Assert.Equal(0.0, limits.VarLcl, 9);
        Assert.Equal(mrbar / 1.128, limits.SigmaHat, 9);
        Assert.Equal(2.66, AiagConstants.E2ForIMR, 2);
    }
}
