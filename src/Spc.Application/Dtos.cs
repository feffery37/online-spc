namespace Spc.Application;

public sealed class MeasureIngestItem
{
    public string ChartCode { get; set; } = string.Empty;
    public DateTime EventTime { get; set; }
    public double Value { get; set; }
    public string LotId { get; set; } = string.Empty;
    public string Sn { get; set; } = string.Empty;
    public string WorkOrder { get; set; } = string.Empty;
    public string Shift { get; set; } = string.Empty;
    public string Recipe { get; set; } = string.Empty;
    public string Equipment { get; set; } = string.Empty;
    /// <summary>工艺参数标签（温度/压力/速度等），判异后做根因分析的必需字段。</summary>
    public Dictionary<string, double> ProcTag { get; set; } = [];
    public int Source { get; set; } = (int)Domain.MeasureSource.MesEdc;
}

public sealed class IngestBatchRequest
{
    public List<MeasureIngestItem> Items { get; set; } = [];
}

public sealed class IngestResult
{
    public int Accepted { get; set; }
    public int Duplicated { get; set; }
    public int Rejected { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed class ChartUpsertRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ChartType { get; set; } = (int)Domain.ChartType.XbarR;
    public int SubgroupSize { get; set; } = 5;
    public double? Usl { get; set; }
    public double? Lsl { get; set; }
    public double? Target { get; set; }
    public string Unit { get; set; } = "mm";
    public string Machine { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Process { get; set; } = string.Empty;
    public string Recipe { get; set; } = string.Empty;
    public string RuleSetVer { get; set; } = "WECO-8-V1";
    public int IsEnabled { get; set; } = 1;
}

public sealed class SimulatorRequest
{
    public string ChartCode { get; set; } = "CH-DEMO-01";
    public string Scenario { get; set; } = "stable";
    public int IntervalMs { get; set; } = 400;
    public int Burst { get; set; } = 1;
}

public sealed class SimulatorState
{
    public bool Running { get; set; }
    public string Scenario { get; set; } = "stable";
    public int IntervalMs { get; set; }
    public long Generated { get; set; }
    public string ChartCode { get; set; } = string.Empty;
}
