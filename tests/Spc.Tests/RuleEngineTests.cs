using Spc.Algorithm;
using Spc.Domain;
using Xunit;

namespace Spc.Tests;

/// <summary>A-05：八条判异规则的构造用例全部命中预期规则号。</summary>
public class RuleEngineTests
{
    private static RuleCode[] Codes(IEnumerable<RuleHit> hits) => hits.Select(h => h.Code).Distinct().ToArray();

    [Fact]
    public void A05_R1_SinglePointBeyondThreeSigma()
    {
        double[] z = [0, 0.2, -0.3, 0.1, 3.5];
        var hits = RuleEngine.EvaluatePhaseII(z);
        Assert.Contains(RuleCode.R1, Codes(hits));
        Assert.Equal(4, hits.First(h => h.Code == RuleCode.R1).Index);
    }

    [Fact]
    public void A05_R2_NinePointsSameSide()
    {
        double[] z = Enumerable.Repeat(0.5, 9).ToArray();
        var hits = RuleEngine.EvaluatePhaseII(z);
        Assert.Contains(RuleCode.R2, Codes(hits));
        Assert.Equal(8, hits.First(h => h.Code == RuleCode.R2).Index);
    }

    [Fact]
    public void A05_R3_SixPointsMonotonic()
    {
        double[] z = [0.0, 0.2, 0.4, 0.6, 0.8, 1.0];
        Assert.Contains(RuleCode.R3, Codes(RuleEngine.EvaluatePhaseII(z)));
    }

    [Fact]
    public void A05_R4_FourteenPointsAlternating()
    {
        double[] z = Enumerable.Range(0, 14).Select(i => i % 2 == 0 ? 0.5 : -0.5).ToArray();
        Assert.Contains(RuleCode.R4, Codes(RuleEngine.EvaluatePhaseII(z)));
    }

    [Fact]
    public void A05_R5_TwoOfThreeBeyondTwoSigma()
    {
        double[] z = [0.0, 2.5, 2.5];
        Assert.Contains(RuleCode.R5, Codes(RuleEngine.EvaluatePhaseII(z)));
    }

    [Fact]
    public void A05_R6_FourOfFiveBeyondOneSigma()
    {
        double[] z = [0.0, 1.5, 1.5, 1.5, 1.5];
        Assert.Contains(RuleCode.R6, Codes(RuleEngine.EvaluatePhaseII(z)));
    }

    [Fact]
    public void A05_R7_FifteenPointsAllWithinOneSigma()
    {
        double[] z = Enumerable.Repeat(0.2, 15).ToArray();
        Assert.Contains(RuleCode.R7, Codes(RuleEngine.EvaluatePhaseII(z)));
    }

    [Fact]
    public void A05_R8_EightPointsAllOutsideOneSigma()
    {
        double[] z = Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? 1.5 : -1.5).ToArray();
        Assert.Contains(RuleCode.R8, Codes(RuleEngine.EvaluatePhaseII(z)));
    }

    [Fact]
    public void PhaseI_OnlyEnablesR1ToR4()
    {
        double[] z = Enumerable.Repeat(0.2, 15).ToArray(); // 会命中 R7，但不应在 Phase I 命中
        var phaseI = RuleEngine.EvaluatePhaseI(z);
        var phaseII = RuleEngine.EvaluatePhaseII(z);

        Assert.DoesNotContain(RuleCode.R7, Codes(phaseI));
        Assert.DoesNotContain(phaseI, h => (int)h.Code > 4);
        Assert.Contains(RuleCode.R7, Codes(phaseII));
    }

    [Fact]
    public void RuleEngine_IsIndependentOfLimitsByUsingZ()
    {
        // 规则引擎只接受 z，不接触控制限，便于「不重算窗口的前提下更换规则集并重放历史事件」。
        double[] z = [0, 0, 0, 3.2];
        Assert.Single(RuleEngine.Evaluate(z, [RuleCode.R1]));
        Assert.DoesNotContain(RuleCode.R2, RuleEngine.Evaluate(z, [RuleCode.R2]).Select(h => h.Code));
    }
}
