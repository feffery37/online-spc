using Spc.Domain;

namespace Spc.Algorithm;

/// <summary>子组统计量。Values 保留原始单值，Phase I 数据指纹需要它（方案 5.2）。</summary>
public sealed record SubgroupStat(
    long Id,
    long Seq,
    DateTime EventTime,
    IReadOnlyList<double> Values,
    double Xbar,
    double RangeVal,
    double StddevVal,
    double MrVal)
{
    public int SampleCount => Values.Count;

    /// <summary>主图描点统计量：三类计量图都用 Xbar（I-MR 时即单值）。</summary>
    public double MainStatistic => Xbar;

    public double VariabilityStatistic(ChartType type) => type switch
    {
        ChartType.XbarR => RangeVal,
        ChartType.XbarS => StddevVal,
        ChartType.IMR => MrVal,
        _ => RangeVal
    };
}

public static class SubgroupFactory
{
    /// <summary>
    /// 由原始单值构造子组统计量。previousXbar 用于 I-MR 的移动极差，
    /// 必须按事件时间严格有序传入（方案 N-01 / 附录 A）。
    /// </summary>
    public static SubgroupStat From(
        long id, long seq, DateTime eventTime, IReadOnlyList<double> values, double? previousXbar = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("子组不能为空。", nameof(values));

        double sum = 0, min = double.MaxValue, max = double.MinValue;
        foreach (var v in values)
        {
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        double xbar = sum / values.Count;
        double s = 0;
        if (values.Count > 1)
        {
            double ss = 0;
            foreach (var v in values) ss += (v - xbar) * (v - xbar);
            s = Math.Sqrt(ss / (values.Count - 1));
        }

        double mr = previousXbar.HasValue ? Math.Abs(xbar - previousXbar.Value) : 0.0;
        return new SubgroupStat(id, seq, eventTime, values.ToArray(), xbar, max - min, s, mr);
    }

    public static IReadOnlyList<SubgroupStat> FromCandidates(
        IEnumerable<(long Id, long Seq, DateTime EventTime, IReadOnlyList<double> Values)> source)
    {
        var result = new List<SubgroupStat>();
        double? previous = null;
        foreach (var item in source)
        {
            var stat = From(item.Id, item.Seq, item.EventTime, item.Values, previous);
            result.Add(stat);
            previous = stat.Xbar;
        }
        return result;
    }
}

/// <summary>由 Phase I 一次性算出并冻结的控制限快照（CL/UCL/LCL 是数据，不是代码）。</summary>
public sealed record ControlLimits(
    ChartType ChartType,
    int SubgroupSize,
    double Cl,
    double Ucl,
    double Lcl,
    double WarnUcl,
    double WarnLcl,
    double VarCl,
    double VarUcl,
    double VarLcl,
    double SigmaHat,
    double SigmaChart,
    double SigmaVarChart,
    string ConstantsVersion)
{
    /// <summary>判异引擎唯一输入：标准化统计量 z = (stat - CL) / sigma_chart。</summary>
    public double Standardize(double statistic) =>
        SigmaChart > 0 ? (statistic - Cl) / SigmaChart : 0.0;

    public double StandardizeVariability(double statistic) =>
        SigmaVarChart > 0 ? (statistic - VarCl) / SigmaVarChart : 0.0;
}

public static class LimitCalculator
{
    /// <summary>试验/正式控制限的唯一计算入口。Phase II 判异路径禁止调用本方法。</summary>
    public static ControlLimits Compute(ChartType type, int subgroupSize, IReadOnlyList<SubgroupStat> groups)
    {
        if (groups.Count == 0) throw new ArgumentException("至少需要 1 个子组才能计算控制限。", nameof(groups));
        if (!type.IsVariable()) throw new NotSupportedException($"M0/M1 仅实现计量型控制图，{type.Label()} 为扩展能力。");

        int n = type == ChartType.IMR ? 1 : subgroupSize;
        if (n != 1 && (n < 2 || n > 10)) throw new ArgumentOutOfRangeException(nameof(subgroupSize));
        if (n > 1 && groups.Any(g => g.SampleCount != n))
            throw new ArgumentException("每个子组的样本数必须等于子组大小 n。");

        double center = groups.Average(g => g.Xbar);
        double rbar = groups.Average(g => g.RangeVal);
        double sbar = groups.Average(g => g.StddevVal);
        // 第一个子组没有前序值，MR=0 必须排除在 MRbar 之外（AIAG I-MR）。
        var mrValues = groups.Skip(1).Select(g => g.MrVal).ToList();
        double mrbar = mrValues.Count > 0 ? mrValues.Average() : 0.0;

        double ucl, lcl, varCl, varUcl, varLcl, sigmaHat;
        switch (type)
        {
            case ChartType.XbarR:
            {
                var c = AiagConstants.For(n);
                ucl = center + c.A2 * rbar;
                lcl = center - c.A2 * rbar;
                varCl = rbar;
                varUcl = c.D4 * rbar;
                varLcl = c.D3 * rbar;
                sigmaHat = rbar / c.D2;
                break;
            }
            case ChartType.XbarS:
            {
                var c = AiagConstants.For(n);
                ucl = center + c.A3 * sbar;
                lcl = center - c.A3 * sbar;
                varCl = sbar;
                varUcl = c.B4 * sbar;
                varLcl = c.B3 * sbar;
                sigmaHat = sbar / c.C4;
                break;
            }
            case ChartType.IMR:
            {
                var c2 = AiagConstants.For(2);
                // UCL = Xbar + 3*MRbar/d2(n=2) = Xbar + 2.66*MRbar
                double half = 3.0 * mrbar / c2.D2;
                ucl = center + half;
                lcl = center - half;
                varCl = mrbar;
                varUcl = c2.D4 * mrbar; // 3.267 * MRbar
                varLcl = 0.0;
                sigmaHat = mrbar / c2.D2; // MRbar / 1.128
                break;
            }
            default:
                throw new NotSupportedException(type.ToString());
        }

        double sigmaChart = (ucl - center) / 3.0;
        double sigmaVarChart = (varUcl - varCl) / 3.0;
        return new ControlLimits(
            type, n, center, ucl, lcl,
            center + 2 * sigmaChart, center - 2 * sigmaChart,
            varCl, varUcl, varLcl,
            sigmaHat, sigmaChart, sigmaVarChart, AiagConstants.Version);
    }
}
