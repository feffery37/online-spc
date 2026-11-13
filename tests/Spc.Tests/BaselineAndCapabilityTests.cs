using Spc.Algorithm;
using Spc.Domain;
using Xunit;

namespace Spc.Tests;

public class BaselineAndCapabilityTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>确定性无趋势拟随机噪声：稳定过程数据集，避免人造数据自带 R2/R3/R4 模式。</summary>
    private static double Noise(int i, int j)
    {
        double x = Math.Sin((i * 127.1 + j * 311.7 + 13.7) * 0.0174532925199433) * 43758.5453;
        return (x - Math.Floor(x)) - 0.5;
    }

    private static BaselineRequest StableRequest(int count = 25)
    {
        var candidates = Enumerable.Range(0, count)
            .Select(i => new BaselineCandidate(
                i + 1, i + 1, T0.AddMinutes(i),
                Enumerable.Range(0, 5)
                    .Select(j => 10.0 + 0.09 * Noise(i, j))
                    .ToArray(),
                null, null))
            .ToList();
        return new BaselineRequest(ChartType.XbarR, 5, candidates,
            "WECO-8-V1", "A2=AIAG;pass=10;rules=R1-R4", "qa.zhang",
            T0, T0.AddMinutes(count - 1));
    }

    [Fact]
    public void A02_SameDatasetProducesIdenticalLimitsAndHash()
    {
        var request = StableRequest();
        var a = BaselineService.Freeze(request);
        var b = BaselineService.Freeze(request);

        Assert.True(a.Success, a.Error);
        Assert.True(b.Success, b.Error);
        Assert.Equal(a.DataHash, b.DataHash);
        Assert.Equal(64, a.DataHash.Length);
        Assert.Equal(a.Limits!.Cl, b.Limits!.Cl, 12);
        Assert.Equal(a.Limits.Ucl, b.Limits.Ucl, 12);
        Assert.Equal(a.Limits.SigmaHat, b.Limits.SigmaHat, 12);
        Assert.Equal(25, a.IncludedSubgroupIds.Distinct().Count());
        Assert.Equal(25, a.IncludedSubgroupIds.Count);
    }

    [Fact]
    public void A02_HashChangesWhenAnyMeasurementChanges()
    {
        var request = StableRequest();
        var original = BaselineService.Freeze(request);
        var altered = StableRequest();
        var values = altered.Candidates[7].Values.ToList();
        values[2] += 0.001;
        var mutated = altered.Candidates.ToList();
        mutated[7] = altered.Candidates[7] with { Values = values };
        var changed = BaselineService.Freeze(altered with { Candidates = mutated });

        Assert.NotEqual(original.DataHash, changed.DataHash);
    }

    [Fact]
    public void A03_UnexplainedOutOfControlPointMustRejectBaseline()
    {
        var request = StableRequest();
        var list = request.Candidates.ToList();
        list[12] = list[12] with { Values = Enumerable.Repeat(11.5, 5).ToArray() }; // 3σ 外且无特殊原因
        var result = BaselineService.Freeze(request with { Candidates = list });

        Assert.False(result.Success);
        Assert.Contains("过程未受控", result.Error);
        Assert.Null(result.Limits);
        Assert.Empty(result.DataHash);
    }

    [Fact]
    public void ExplainedSpecialCauseIsExcludedAndLedgerIsComplete()
    {
        var request = StableRequest();
        var list = request.Candidates.ToList();
        list[12] = list[12] with
        {
            Values = Enumerable.Repeat(11.5, 5).ToArray(),
            ConfirmedCause = "量具校准偏移，校准记录 CAL-2026-1113-07",
            ConfirmedBy = "qa.zhang"
        };
        var result = BaselineService.Freeze(request with { Candidates = list });

        Assert.True(result.Success, result.Error);
        Assert.Equal(24, result.IncludedSubgroupIds.Count);
        Assert.NotEmpty(result.Exclusions);
        Assert.All(result.Exclusions, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Reason));
            Assert.False(string.IsNullOrWhiteSpace(e.ConfirmedBy));
            Assert.True((int)e.Rule <= 4); // Phase I 仅 R1~R4
        });
        Assert.Contains(result.Exclusions, e => e.Seq == 13);
    }

    [Fact]
    public void LessThanTwentySubgroupsIsRejected()
    {
        var result = BaselineService.Freeze(StableRequest(19));
        Assert.False(result.Success);
        Assert.Contains("禁止建立基线", result.Error);
    }

    [Fact]
    public void Capability_IsBlockedWhenProcessIsNotInControl()
    {
        var unstable = CapabilityCalculator.Compute(10.5, 9.5, 10.0, 0.1, 0.12, false);
        Assert.False(unstable.IsProcessControlled);
        Assert.Null(unstable.Cpk);
        Assert.Null(unstable.Cp);
        Assert.Equal("不可评定", unstable.Grade);

        var stable = CapabilityCalculator.Compute(10.5, 9.5, 10.0, 0.1, 0.12, true);
        Assert.Equal(1.0 / 0.6, stable.Cp!.Value, 9);
        Assert.Equal(0.5 / 0.3, stable.Cpk!.Value, 9);
        Assert.Equal(1.0 / 0.72, stable.Pp!.Value, 9);
        Assert.True(stable.Cpk > stable.Ppk);
    }
}
