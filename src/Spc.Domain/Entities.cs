namespace Spc.Domain;

/// <summary>
/// 领域实体全部为无特性 POCO，列名/表名由 Infrastructure/DbNaming.cs 统一按
/// PascalCase -> UPPER_SNAKE_CASE 映射，Domain 层保持零第三方依赖。
/// 所有状态字段均为整数，与方案 8.4 的枚举约定一致。
/// </summary>
public class SpcChart
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ChartType { get; set; }
    public int SubgroupSize { get; set; }
    public double? Usl { get; set; }
    public double? Lsl { get; set; }
    public double? Target { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Process { get; set; } = string.Empty;
    public string Recipe { get; set; } = string.Empty;
    public string RuleSetVer { get; set; } = "WECO-8-V1";
    public int IsEnabled { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>原始量测点。PROC_TAG 为工艺参数标签 JSON，缺一不可（方案 7.2 / 附录 A）。</summary>
public class SpcMeasure
{
    public long Id { get; set; }
    public long ChartId { get; set; }
    public DateTime EventTime { get; set; }
    public double MeasureValue { get; set; }
    public string LotId { get; set; } = string.Empty;
    public string Sn { get; set; } = string.Empty;
    public string WorkOrder { get; set; } = string.Empty;
    public string Shift { get; set; } = string.Empty;
    public string Recipe { get; set; } = string.Empty;
    public string Equipment { get; set; } = string.Empty;
    public string ProcTag { get; set; } = string.Empty;
    public int Source { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public DateTime IngestTime { get; set; }
    public long? SubgroupId { get; set; }
}

public class SpcSubgroup
{
    public long Id { get; set; }
    public long ChartId { get; set; }
    public long Seq { get; set; }
    public DateTime EventTimeFrom { get; set; }
    public DateTime EventTimeTo { get; set; }
    public int SampleCount { get; set; }
    public double Xbar { get; set; }
    public double RangeVal { get; set; }
    public double StddevVal { get; set; }
    public double MrVal { get; set; }
    public long? LimitVerId { get; set; }
    public int Status { get; set; }
    public DateTime ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 冻结限值版本。只 INSERT，绝不修改 CL/UCL/LCL 等计算字段；
/// 发布时仅允许更新 Status / EffectiveFrom / ExpiredTime 两个生命周期字段（方案 5.6 / 7.2）。
/// </summary>
public class SpcLimitVersion
{
    public long Id { get; set; }
    public long ChartId { get; set; }
    public int VersionNo { get; set; }
    /// <summary>建限当时的图型快照，保证历史回放不依赖 SPC_CHART 的当前定义。</summary>
    public int ChartType { get; set; }
    public double Cl { get; set; }
    public double Ucl { get; set; }
    public double Lcl { get; set; }
    public double WarnUcl { get; set; }
    public double WarnLcl { get; set; }
    public double VarCl { get; set; }
    public double VarUcl { get; set; }
    public double VarLcl { get; set; }
    public double SigmaHat { get; set; }
    public int SubgroupSize { get; set; }
    public int SubgroupCount { get; set; }
    public DateTime DataFrom { get; set; }
    public DateTime DataTo { get; set; }
    public string DataHash { get; set; } = string.Empty;
    public string SubgroupIds { get; set; } = string.Empty;
    public string RuleSetVer { get; set; } = string.Empty;
    public string CalcParams { get; set; } = string.Empty;
    public int Iterations { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public int Status { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? ExpiredTime { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SpcBaselineExclude
{
    public long Id { get; set; }
    public long ChartId { get; set; }
    public long SubgroupId { get; set; }
    public long Seq { get; set; }
    public int RuleCode { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ConfirmedBy { get; set; } = string.Empty;
    public DateTime ConfirmedAt { get; set; }
    public int Iteration { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SpcRuleOut
{
    public long Id { get; set; }
    public long ChartId { get; set; }
    public long SubgroupId { get; set; }
    public long LimitVerId { get; set; }
    public int RuleCode { get; set; }
    public int Severity { get; set; }
    public double StatValue { get; set; }
    public double ZValue { get; set; }
    public DateTime EventTime { get; set; }
    public int OcapStatus { get; set; }
    public string LotId { get; set; } = string.Empty;
    public string Sn { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class SpcOcap
{
    public long Id { get; set; }
    public long RuleOutId { get; set; }
    public string OcapNo { get; set; } = string.Empty;
    public int Status { get; set; }
    public string Assignee { get; set; } = string.Empty;
    public string Cause { get; set; } = string.Empty;
    public string Countermeasure { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public long? VerificationSubgroupId { get; set; }
    public string SignedBy { get; set; } = string.Empty;
    public DateTime? DueTime { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SpcMesAction
{
    public long Id { get; set; }
    public long RuleOutId { get; set; }
    public int ActionType { get; set; }
    public int Status { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string MesRef { get; set; } = string.Empty;
    public string AckPayload { get; set; } = string.Empty;
    public DateTime RequestTime { get; set; }
    public DateTime? AckTime { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SpcAuditLog
{
    public long Id { get; set; }
    public DateTime EventTime { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public long ChartId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>M5 报表快照：定时/手工生成的日报周报月报与合规取证包，带哈希保证可复现、可取证。</summary>
public class SpcReportSnapshot
{
    public long Id { get; set; }
    public long ChartId { get; set; }
    public string ReportType { get; set; } = string.Empty;
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string DataHash { get; set; } = string.Empty;
    public string GeneratedBy { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
}
