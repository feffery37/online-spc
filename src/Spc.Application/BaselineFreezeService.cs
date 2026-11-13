using System.Text.Json;
using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

/// <summary>
/// Phase I 六步流程的服务层（方案 5.2）：圈定候选子组 → 试算 → 稳定性迭代 →
/// 冻结快照 + 数据指纹 → 生成 PENDING 版本 → （人工审批在 BaselinePublishService）。
/// 关键门禁：冻结服务绝不把版本置为 ACTIVE。
/// </summary>
public sealed class BaselineFreezeService(ISpcRepository repository, IRealtimePublisher publisher)
{
    private sealed record CandidateSet(
        IReadOnlyList<BaselineCandidate> Candidates,
        IReadOnlyList<SpcSubgroup> Subgroups,
        DateTime DataFrom,
        DateTime DataTo);

    public async Task<BaselinePrecheckResult> PrecheckAsync(BaselineFreezeRequest request, CancellationToken ct)
    {
        var (chart, set, error) = await BuildCandidatesAsync(request, ct);
        if (chart is null || set is null)
            return new BaselinePrecheckResult { Ok = false, Error = error ?? "候选数据不可用。" };

        var stats = SubgroupFactory.FromCandidates(
            set.Candidates.Select(c => (c.Id, c.Seq, c.EventTime, c.Values)));
        var limits = LimitCalculator.Compute((ChartType)chart.ChartType, SubgroupSize(chart), stats);
        var zMain = stats.Select(s => limits.Standardize(s.MainStatistic)).ToArray();
        var zVar = stats.Select(s => limits.StandardizeVariability(s.VariabilityStatistic((ChartType)chart.ChartType))).ToArray();
        if ((ChartType)chart.ChartType == ChartType.IMR && zVar.Length > 0) zVar[0] = 0.0;

        var hits = RuleEngine.EvaluatePhaseI(zMain)
            .Concat(RuleEngine.EvaluatePhaseI(zVar))
            .GroupBy(h => h.Index)
            .Select(g => new PrecheckHit
            {
                SubgroupId = set.Candidates[g.Key].Id,
                Seq = set.Candidates[g.Key].Seq,
                EventTime = set.Candidates[g.Key].EventTime,
                Xbar = stats[g.Key].Xbar,
                RangeVal = stats[g.Key].RangeVal,
                Rules = g.Select(h => h.Code.Code()).Distinct().Order().ToList()
            })
            .OrderBy(h => h.Seq)
            .ToList();

        return new BaselinePrecheckResult
        {
            Ok = true,
            CandidateCount = set.Candidates.Count,
            TrialLimits = limits,
            Hits = hits,
            Note = hits.Count == 0
                ? "试算控制限下 R1~R4 全部受控，可直接冻结（第 1 轮收敛）。"
                : "存在判异点：只有能追溯到已确认特殊原因并登记的子组才允许剔除；无原因点将导致建限被拒绝。"
        };
    }

    public async Task<BaselineFreezeResult> FreezeAsync(BaselineFreezeRequest request, CancellationToken ct)
    {
        var (chart, set, error) = await BuildCandidatesAsync(request, ct);
        if (chart is null || set is null)
            return new BaselineFreezeResult { Ok = false, Error = error ?? "候选数据不可用。" };

        if (string.IsNullOrWhiteSpace(request.CreatedBy))
            return new BaselineFreezeResult { Ok = false, Error = "必须填写创建人（审批链留痕）。" };

        var type = (ChartType)chart.ChartType;
        var calcParams = $"chartType={type};n={SubgroupSize(chart)};constants={AiagConstants.Version};" +
                         $"rules=R1-R4;maxPass={BaselineRequest.MaximumPasses};candidates={set.Candidates.Count}";

        var algorithmRequest = new BaselineRequest(
            type, SubgroupSize(chart), set.Candidates,
            chart.RuleSetVer, calcParams, request.CreatedBy, set.DataFrom, set.DataTo);

        var result = BaselineService.Freeze(algorithmRequest);
        if (!result.Success || result.Limits is null)
        {
            await repository.InsertAuditAsync(new SpcAuditLog
            {
                EventTime = DateTime.UtcNow,
                EventType = "BASELINE_REJECTED",
                EntityType = nameof(SpcLimitVersion),
                EntityId = 0,
                ChartId = chart.Id,
                Payload = JsonSerializer.Serialize(new { request.CreatedBy, result.Error }),
                Actor = request.CreatedBy,
                CreatedAt = DateTime.UtcNow
            }, ct);
            return new BaselineFreezeResult
            {
                Ok = false, Error = result.Error, CandidateCount = set.Candidates.Count
            };
        }

        var limits = result.Limits;
        int versionNo = (int)(await repository.GetMaxVersionNoAsync(chart.Id, ct) + 1);
        var version = new SpcLimitVersion
        {
            ChartId = chart.Id,
            VersionNo = versionNo,
            ChartType = chart.ChartType,
            Cl = limits.Cl,
            Ucl = limits.Ucl,
            Lcl = limits.Lcl,
            WarnUcl = limits.WarnUcl,
            WarnLcl = limits.WarnLcl,
            VarCl = limits.VarCl,
            VarUcl = limits.VarUcl,
            VarLcl = limits.VarLcl,
            SigmaHat = limits.SigmaHat,
            SubgroupSize = limits.SubgroupSize,
            SubgroupCount = result.IncludedSubgroupIds.Count,
            DataFrom = set.DataFrom,
            DataTo = set.DataTo,
            DataHash = result.DataHash,
            SubgroupIds = JsonSerializer.Serialize(result.IncludedSubgroupIds),
            RuleSetVer = chart.RuleSetVer,
            CalcParams = calcParams,
            Iterations = result.PassCount,
            ChangeReason = request.ChangeReason,
            CreatedBy = request.CreatedBy,
            ApprovedBy = string.Empty,
            Status = (int)LimitVersionStatus.Pending,
            EffectiveFrom = null,
            ExpiredTime = null,
            CreatedAt = DateTime.UtcNow
        };
        await repository.InsertLimitVersionAsync(version, ct);

        if (result.Exclusions.Count > 0)
        {
            await repository.InsertBaselineExcludesAsync(result.Exclusions.Select(e => new SpcBaselineExclude
            {
                ChartId = chart.Id,
                SubgroupId = e.SubgroupId,
                Seq = e.Seq,
                RuleCode = (int)e.Rule,
                Reason = e.Reason,
                ConfirmedBy = e.ConfirmedBy,
                ConfirmedAt = result.CalculatedAt,
                Iteration = e.Iteration,
                CreatedAt = DateTime.UtcNow
            }).ToArray(), ct);
        }

        await repository.InsertAuditAsync(new SpcAuditLog
        {
            EventTime = DateTime.UtcNow,
            EventType = "BASELINE_FROZEN",
            EntityType = nameof(SpcLimitVersion),
            EntityId = version.Id,
            ChartId = chart.Id,
            Payload = JsonSerializer.Serialize(new
            {
                version.VersionNo, version.DataHash, version.Cl, version.Ucl, version.Lcl,
                version.SigmaHat, version.SubgroupCount, result.PassCount, result.Exclusions.Count
            }),
            Actor = request.CreatedBy,
            CreatedAt = DateTime.UtcNow
        }, ct);

        await publisher.PublishNoticeAsync(new
        {
            type = "notice",
            at = DateTime.UtcNow,
            message = $"Phase I 冻结完成：V{versionNo} 状态 PENDING，数据指纹 {result.DataHash[..12]}…，等待人工审批发布"
        }, ct);

        return new BaselineFreezeResult
        {
            Ok = true,
            Version = version,
            Limits = limits,
            Exclusions = result.Exclusions,
            CandidateCount = set.Candidates.Count,
            IncludedCount = result.IncludedSubgroupIds.Count,
            Note = "版本状态 PENDING，必须经质量工程师人工审批才能置为 ACTIVE；系统不会自动激活、不会滚动重算。"
        };
    }

    private async Task<(SpcChart? Chart, CandidateSet? Set, string? Error)> BuildCandidatesAsync(
        BaselineFreezeRequest request, CancellationToken ct)
    {
        var chart = await repository.GetChartAsync(request.ChartId, ct);
        if (chart is null) return (null, null, "控制图不存在。");
        var type = (ChartType)chart.ChartType;
        if (!type.IsVariable()) return (null, null, "M2 仅支持计量型 Xbar-R / Xbar-S / I-MR 建限。");

        DateTime from = request.DataFrom ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        DateTime to = request.DataTo ?? DateTime.UtcNow;
        var subgroups = await repository.GetClosedSubgroupsInWindowAsync(chart.Id, from, to, ct);
        var ordered = subgroups.OrderBy(s => s.EventTimeTo).ThenBy(s => s.Seq).ToArray();
        if (request.DataFrom is null && request.DataTo is null)
            ordered = ordered.TakeLast(Math.Clamp(request.SubgroupCount, 20, 500)).ToArray();
        else
            ordered = ordered.TakeLast(500).ToArray();

        if (ordered.Length < BaselineRequest.MinimumSubgroups)
            return (null, null,
                $"候选子组不足：{ordered.Length} < {BaselineRequest.MinimumSubgroups}。请先采集至少 20 个已闭合子组（约 {20 * SubgroupSize(chart)} 个量测点）。");

        var ids = ordered.Select(s => s.Id).ToArray();
        var measures = await repository.GetMeasuresBySubgroupIdsAsync(chart.Id, ids, ct);
        var byGroup = measures
            .Where(m => m.SubgroupId.HasValue)
            .GroupBy(m => m.SubgroupId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.EventTime).ThenBy(m => m.Id)
                .Select(m => m.MeasureValue).ToArray());

        var causes = request.ConfirmedCauses
            .Where(c => !string.IsNullOrWhiteSpace(c.Reason) && !string.IsNullOrWhiteSpace(c.ConfirmedBy))
            .ToDictionary(c => c.SubgroupId, c => c);

        var candidates = ordered.Select(s =>
        {
            causes.TryGetValue(s.Id, out var cause);
            return new BaselineCandidate(
                s.Id, s.Seq, DateTime.SpecifyKind(s.EventTimeTo, DateTimeKind.Utc),
                byGroup.TryGetValue(s.Id, out var values) ? values : [],
                cause?.Reason, cause?.ConfirmedBy);
        }).Where(c => c.Values.Count > 0).ToArray();

        if (candidates.Length < BaselineRequest.MinimumSubgroups)
            return (null, null, $"能追溯到原始量测值的子组不足 {BaselineRequest.MinimumSubgroups} 组。");

        return (chart, new CandidateSet(candidates, ordered,
            candidates.Min(c => c.EventTime), candidates.Max(c => c.EventTime)), null);
    }

    private static int SubgroupSize(SpcChart chart) =>
        (ChartType)chart.ChartType == ChartType.IMR ? 1 : chart.SubgroupSize;
}
