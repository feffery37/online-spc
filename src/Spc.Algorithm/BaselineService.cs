using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Spc.Domain;

namespace Spc.Algorithm;

public sealed record BaselineCandidate(
    long Id, long Seq, DateTime EventTime, IReadOnlyList<double> Values,
    string? ConfirmedCause, string? ConfirmedBy);

public sealed record BaselineExclusion(
    int Iteration, long SubgroupId, long Seq, RuleCode Rule, string Reason, string ConfirmedBy);

public sealed record BaselineRequest(
    ChartType ChartType,
    int SubgroupSize,
    IReadOnlyList<BaselineCandidate> Candidates,
    string RuleSetVer,
    string CalcParams,
    string CreatedBy,
    DateTime DataFrom,
    DateTime DataTo)
{
    public const int MinimumSubgroups = 20;
    public const int MaximumPasses = 10;
}

public sealed record BaselineResult(
    bool Success,
    string Error,
    ControlLimits? Limits,
    string DataHash,
    IReadOnlyList<long> IncludedSubgroupIds,
    IReadOnlyList<BaselineExclusion> Exclusions,
    int PassCount,
    DateTime CalculatedAt)
{
    public static BaselineResult Failure(string error) =>
        new(false, error, null, string.Empty, [], [], 0, DateTime.UtcNow);
}

/// <summary>
/// Phase I 冻结基线的纯算法实现（方案 5.2 / 5.4）：六步流程中的「稳定性迭代检验 +
/// 数据指纹」部分。只产出 PENDING 版本的计算依据，审批发布由 M2 服务层完成。
/// 关键纪律：仅 R1~R4；命中点若无已确认特殊原因立即拒绝建限；剔除后仍需 ≥20 组。
/// </summary>
public static class BaselineService
{
    public static BaselineResult Freeze(BaselineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidates.Count < BaselineRequest.MinimumSubgroups)
            return BaselineResult.Failure(
                $"候选子组不足：{request.Candidates.Count} < {BaselineRequest.MinimumSubgroups}，禁止建立基线。");

        // 同一张控制图的子组必须按事件时间严格有序（N-01 / 附录 A）。
        var work = request.Candidates
            .OrderBy(c => c.EventTime)
            .ThenBy(c => c.Seq)
            .ThenBy(c => c.Id)
            .ToList();

        var exclusions = new List<BaselineExclusion>();
        ControlLimits? limits = null;
        int pass = 0;

        for (pass = 1; pass <= BaselineRequest.MaximumPasses; pass++)
        {
            var stats = SubgroupFactory.FromCandidates(work.Select(c => (c.Id, c.Seq, c.EventTime, c.Values)));
            limits = LimitCalculator.Compute(request.ChartType, request.SubgroupSize, stats);

            // 主图与变异图分别标准化，两套 z 序列都要做 R1~R4 稳定性检验。
            var zMain = stats.Select(s => limits.Standardize(s.MainStatistic)).ToArray();
            var zVar = stats.Select(s => limits.StandardizeVariability(s.VariabilityStatistic(request.ChartType))).ToArray();
            if (request.ChartType == ChartType.IMR && zVar.Length > 0)
                zVar[0] = 0.0; // 首个 MR 无定义，不参与判异

            var hits = RuleEngine.EvaluatePhaseI(zMain)
                .Concat(RuleEngine.EvaluatePhaseI(zVar))
                .GroupBy(h => h.Index)
                .Select(g => g.OrderBy(h => h.Code).First())
                .OrderBy(h => h.Index)
                .ToArray();

            if (hits.Length == 0) break; // 收敛

            // 试算限会被特殊原因点污染，连带把其它正常点判为命中。
            // 每轮只剔除「有已确认特殊原因」的点并重算；若本轮一个可剔除点都没有，
            // 说明存在无法解释的异常，直接判「过程未受控」并终止建限（方案 5.3 / 5.4）。
            var toRemove = new HashSet<long>();
            foreach (var hit in hits)
            {
                var candidate = work[hit.Index];
                if (string.IsNullOrWhiteSpace(candidate.ConfirmedCause) ||
                    string.IsNullOrWhiteSpace(candidate.ConfirmedBy))
                    continue; // 无原因点保留，下一轮用干净的重算限重新检验

                toRemove.Add(candidate.Id);
                exclusions.Add(new BaselineExclusion(
                    pass, candidate.Id, candidate.Seq, hit.Code,
                    candidate.ConfirmedCause!, candidate.ConfirmedBy!));
            }

            if (toRemove.Count == 0)
            {
                var first = work[hits[0].Index];
                return BaselineResult.Failure(
                    $"过程未受控：子组 Seq={first.Seq} 命中 {hits[0].Code.Code()} 但无已确认特殊原因/确认人，" +
                    "禁止无理由删点，本次建限终止（方案 5.3 / 5.4）。");
            }

            work.RemoveAll(c => toRemove.Contains(c.Id));
            if (work.Count < BaselineRequest.MinimumSubgroups)
                return BaselineResult.Failure(
                    $"剔除后有效子组数 {work.Count} < {BaselineRequest.MinimumSubgroups}，本次建限作废。");

            limits = null;
        }

        if (limits is null)
            return BaselineResult.Failure(
                $"稳定性迭代超过 {BaselineRequest.MaximumPasses} 轮仍未收敛，禁止建立基线。");

        var finalStats = SubgroupFactory.FromCandidates(work.Select(c => (c.Id, c.Seq, c.EventTime, c.Values)));
        limits = LimitCalculator.Compute(request.ChartType, request.SubgroupSize, finalStats);

        var includedIds = work.Select(c => c.Id).ToArray();
        return new BaselineResult(
            true, string.Empty, limits,
            BaselineHasher.Compute(request, work),
            includedIds, exclusions, pass, DateTime.UtcNow);
    }
}

/// <summary>SHA256 数据集指纹：子组 ID + 每个量测值 + 计算参数，保证可复现（方案 N-03 / A-02）。</summary>
public static class BaselineHasher
{
    public static string Compute(BaselineRequest request, IReadOnlyList<BaselineCandidate> included)
    {
        var sb = new StringBuilder();
        sb.Append("SPC-BASELINE-V1")
          .Append('|').Append((int)request.ChartType)
          .Append('|').Append(request.SubgroupSize)
          .Append('|').Append(request.RuleSetVer)
          .Append('|').Append(request.CalcParams)
          .Append('|').Append(AiagConstants.Version)
          .Append('|').Append(request.DataFrom.ToString("O", CultureInfo.InvariantCulture))
          .Append('|').Append(request.DataTo.ToString("O", CultureInfo.InvariantCulture));

        foreach (var c in included.OrderBy(x => x.EventTime).ThenBy(x => x.Seq).ThenBy(x => x.Id))
        {
            sb.Append('|').Append(c.Id)
              .Append(':').Append(c.Seq)
              .Append(':').Append(c.EventTime.ToString("O", CultureInfo.InvariantCulture))
              .Append(':');
            foreach (var v in c.Values)
                sb.Append(v.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
