using Spc.Domain;

namespace Spc.Application;

public sealed record RuleSetDefinition(
    string Version,
    IReadOnlyList<RuleCode> Rules,
    IReadOnlyDictionary<RuleCode, Severity> Severity,
    string Description);

/// <summary>
/// 规则集按图配置（方案 6.3 / 风险「规则集假警报过多」）。
/// Phase II 才启用全套八条；Phase I 恒用 R1~R4，不受此目录影响。
/// 变异图命中在上层服务里提升一个严重级别。
/// </summary>
public static class RuleSetCatalog
{
    public static readonly string DefaultVersion = "WECO-8-V1";

    private static Severity[] BaseSeverity =>
    [
        Severity.Critical, // R1 单点超 3σ
        Severity.Major,    // R2 连续 9 点同侧
        Severity.Major,    // R3 连续 6 点趋势
        Severity.Warning,  // R4 连续 14 点交替
        Severity.Warning,  // R5
        Severity.Warning,  // R6
        Severity.Warning,  // R7
        Severity.Major     // R8
    ];

    private static readonly RuleSetDefinition Full = new(
        DefaultVersion,
        EnumText.PhaseIIRules,
        BuildSeverityMap(EnumText.PhaseIIRules, 0),
        "WECO / Nelson 全套八条规则，Phase II 实时监控默认配置");

    private static readonly RuleSetDefinition Conservative = new(
        "WECO-4-V1",
        EnumText.PhaseIRules,
        BuildSeverityMap(EnumText.PhaseIRules, 0),
        "仅 R1~R4，抑制 R5~R8 的假警报，适用于告警疲劳的产线");

    private static readonly RuleSetDefinition Strict = new(
        "WECO-8-STRICT",
        EnumText.PhaseIIRules,
        BuildSeverityMap(EnumText.PhaseIIRules, 1),
        "全套八条且严重级别整体上调一级，适用于关键特性");

    private static readonly Dictionary<string, RuleSetDefinition> All = new(StringComparer.OrdinalIgnoreCase)
    {
        [Full.Version] = Full,
        [Conservative.Version] = Conservative,
        [Strict.Version] = Strict
    };

    public static IReadOnlyList<RuleSetDefinition> List() => [Full, Conservative, Strict];

    public static RuleSetDefinition Resolve(string? version) =>
        string.IsNullOrWhiteSpace(version) ? Full :
        All.TryGetValue(version, out var set) ? set : Full;

    public static string SeverityLabel(Severity s) => s switch
    {
        Severity.Critical => "CRITICAL",
        Severity.Major => "MAJOR",
        Severity.Warning => "WARNING",
        _ => "INFO"
    };

    private static IReadOnlyDictionary<RuleCode, Severity> BuildSeverityMap(
        IReadOnlyList<RuleCode> rules, int boost)
    {
        var map = new Dictionary<RuleCode, Severity>();
        for (int i = 0; i < rules.Count; i++)
        {
            int value = Math.Min((int)BaseSeverity[i] + boost, (int)Severity.Critical);
            map[rules[i]] = (Severity)value;
        }
        return map;
    }
}
