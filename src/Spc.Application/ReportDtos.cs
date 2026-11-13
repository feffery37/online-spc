using Spc.Algorithm;
using Spc.Domain;

namespace Spc.Application;

public sealed class CapabilityReportDto
{
    public long ChartId { get; set; }
    public string ChartCode { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int SampleCount { get; set; }
    public HistogramResult? Histogram { get; set; }
    public double? Usl { get; set; }
    public double? Lsl { get; set; }
    public double? Target { get; set; }
    public string Unit { get; set; } = string.Empty;
    public CapabilityResultDto? Capability { get; set; }
    public string SpecLineNote { get; set; } =
        "规格线 USL/LSL 只画在能力直方图上；子组均值波动比单值小 √n 倍，禁止画在 Xbar 控制图上（方案 4.5）。";
}

public sealed record PeriodCapability(
    string Period,
    DateTime From,
    DateTime To,
    int SampleCount,
    double Mean,
    double SigmaOverall,
    double? Cp,
    double? Cpk,
    double? Pp,
    double? Ppk,
    bool IsControlled,
    string ControlGateReason,
    int? LimitVersionNo);

public sealed class PeriodReportDto
{
    public string Granularity { get; set; } = "day";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public IReadOnlyList<PeriodCapability> Periods { get; set; } = [];
    public string Note { get; set; } = string.Empty;
}

public sealed record ComplianceCheckItem(string Item, bool Pass, string Evidence);

public sealed class CompliancePackageDto
{
    public long ChartId { get; set; }
    public string ChartCode { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public SpcChart? Chart { get; set; }
    public IReadOnlyList<SpcLimitVersion> Versions { get; set; } = [];
    public IReadOnlyList<SpcBaselineExclude> Exclusions { get; set; } = [];
    public int RuleOutCount { get; set; }
    public int OcapCount { get; set; }
    public int MesActionCount { get; set; }
    public int AuditEventCount { get; set; }
    public int SnapshotCount { get; set; }
    public IReadOnlyList<ComplianceCheckItem> Checklist { get; set; } = [];
    public bool Ready { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class AuditReportDto
{
    public long ChartId { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int Total { get; set; }
    public IReadOnlyList<SpcAuditLog> Events { get; set; } = [];
    public object ByType { get; set; } = new { };
}

public sealed class ReportScheduleDto
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; }
    public DateTime? LastRun { get; set; }
    public int GeneratedCount { get; set; }
    public string Note { get; set; } = string.Empty;
}
