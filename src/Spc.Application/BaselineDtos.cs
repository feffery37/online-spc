using Spc.Algorithm;

namespace Spc.Application;

public sealed class ConfirmedCauseDto
{
    public long SubgroupId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ConfirmedBy { get; set; } = string.Empty;
}

/// <summary>
/// Phase I 建限请求。候选窗口两种取法：指定 DataFrom/DataTo，或取最新 SubgroupCount 个已闭合子组。
/// ConfirmedCauses 是「已确认特殊原因」登记，只有登记过的命中点才允许剔除（方案 5.3）。
/// </summary>
public sealed class BaselineFreezeRequest
{
    public long ChartId { get; set; }
    public DateTime? DataFrom { get; set; }
    public DateTime? DataTo { get; set; }
    public int SubgroupCount { get; set; } = 25;
    public string CreatedBy { get; set; } = string.Empty;
    public string ChangeReason { get; set; } = "首次建立基线";
    public List<ConfirmedCauseDto> ConfirmedCauses { get; set; } = [];
}

public sealed class PrecheckHit
{
    public long SubgroupId { get; set; }
    public long Seq { get; set; }
    public DateTime EventTime { get; set; }
    public double Xbar { get; set; }
    public double RangeVal { get; set; }
    public List<string> Rules { get; set; } = [];
}

public sealed class BaselinePrecheckResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
    public ControlLimits? TrialLimits { get; set; }
    public List<PrecheckHit> Hits { get; set; } = [];
    public string Note { get; set; } = string.Empty;
}

public sealed class BaselineFreezeResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = string.Empty;
    public Domain.SpcLimitVersion? Version { get; set; }
    public ControlLimits? Limits { get; set; }
    public IReadOnlyList<BaselineExclusion> Exclusions { get; set; } = [];
    public int CandidateCount { get; set; }
    public int IncludedCount { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class BaselinePublishRequest
{
    public long ChartId { get; set; }
    public long VersionId { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public string ChangeReason { get; set; } = string.Empty;
    public DateTime? EffectiveFrom { get; set; }
}

public sealed class BaselinePublishResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = string.Empty;
    public Domain.SpcLimitVersion? Version { get; set; }
    public long? ExpiredVersionId { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class CapabilityResultDto
{
    public bool Available { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long? LimitVerId { get; set; }
    public int? VersionNo { get; set; }
    public object? Indices { get; set; }
    public string ControlBasis { get; set; } = string.Empty;
}
