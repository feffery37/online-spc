namespace Spc.Domain;

/// <summary>
/// 控制图类型。全部使用整数枚举，避免字符串枚举在 SqlSugar / Oracle 下的序列化差异（方案 8.4）。
/// </summary>
public enum ChartType
{
    XbarR = 1,
    XbarS = 2,
    IMR = 3,
    P = 4,
    Np = 5,
    C = 6,
    U = 7
}

/// <summary>限值版本状态机：DRAFT -> PENDING -> ACTIVE -> EXPIRED（方案 5.6）。</summary>
public enum LimitVersionStatus
{
    Draft = 1,
    Pending = 2,
    Active = 3,
    Expired = 4
}

public enum SubgroupStatus
{
    Collecting = 1,
    Closed = 2,
    Evaluated = 3
}

/// <summary>WECO / Nelson 八条判异规则。Phase I 仅启用 R1~R4，Phase II 启用全套。</summary>
public enum RuleCode
{
    R1 = 1,
    R2 = 2,
    R3 = 3,
    R4 = 4,
    R5 = 5,
    R6 = 6,
    R7 = 7,
    R8 = 8
}

public enum Severity
{
    Info = 1,
    Warning = 2,
    Major = 3,
    Critical = 4
}

/// <summary>Rule Out 与 OCAP 的处置状态（方案 6.3 闭环）。</summary>
public enum OcapStatus
{
    Detected = 1,
    Alerted = 2,
    ActionSent = 3,
    MesAcked = 4,
    InReview = 5,
    Verified = 6,
    Closed = 7,
    Escalated = 8
}

public enum MesActionType
{
    LotHold = 1,
    EqpHold = 2,
    StopLine = 3,
    Notify = 4
}

public enum MesActionStatus
{
    Pending = 1,
    Sent = 2,
    Acked = 3,
    Failed = 4
}

public enum MeasureSource
{
    Simulator = 1,
    Csv = 2,
    OpcUa = 3,
    Mqtt = 4,
    SecsGem = 5,
    Serial = 6,
    MesEdc = 7
}

public static class EnumText
{
    public static string Code(this RuleCode code) => "R" + (int)code;

    public static RuleCode[] PhaseIRules => [RuleCode.R1, RuleCode.R2, RuleCode.R3, RuleCode.R4];

    public static RuleCode[] PhaseIIRules =>
    [
        RuleCode.R1, RuleCode.R2, RuleCode.R3, RuleCode.R4,
        RuleCode.R5, RuleCode.R6, RuleCode.R7, RuleCode.R8
    ];

    public static bool IsVariable(this ChartType type) =>
        type is ChartType.XbarR or ChartType.XbarS or ChartType.IMR;

    public static string Label(this ChartType type) => type switch
    {
        ChartType.XbarR => "Xbar-R",
        ChartType.XbarS => "Xbar-S",
        ChartType.IMR => "I-MR",
        ChartType.P => "p",
        ChartType.Np => "np",
        ChartType.C => "c",
        ChartType.U => "u",
        _ => type.ToString()
    };
}
