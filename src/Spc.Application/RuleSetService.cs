using System.Text.Json;
using Spc.Domain;

namespace Spc.Application;

public sealed class RuleSetSaveRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>启用的规则码，如 ["R1","R2"]。</summary>
    public List<string> Rules { get; set; } = [];
    /// <summary>逐条严重级别，如 {"R1":"CRITICAL"}；未指定默认 WARNING。</summary>
    public Dictionary<string, string> Severity { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string CreatedBy { get; set; } = "engineer";
}

public sealed record RuleSetView(
    string Version,
    string Name,
    string Description,
    IReadOnlyList<string> Rules,
    IReadOnlyDictionary<string, string> Severity,
    bool BuiltIn,
    bool Enabled,
    string CreatedBy,
    DateTime? UpdatedAt);

/// <summary>
/// 规则集配置服务（方案 4.3 / 6.3「Phase II 按图配置规则子集与严重级别」）。
/// 内置预设只读且优先；自定义规则集持久化在 SPC_RULE_SET，变更写审计日志。
/// 规则阈值（连 9 点 / 6 点 / 14 点等）是 WECO/Nelson 标准常量，不开放修改，
/// 避免配出没有统计意义的规则；可配置的是「启用哪些规则」与「命中后的严重级别」。
/// </summary>
public sealed class RuleSetService(ISpcRepository repository)
{
    public async Task<IReadOnlyList<RuleSetView>> ListAsync(CancellationToken ct)
    {
        var views = RuleSetCatalog.List()
            .Select(s => ToView(s, s.Version, s.Description, true, true, "built-in", null))
            .ToList();

        foreach (var row in await repository.GetRuleSetsAsync(ct))
        {
            var parsed = Parse(row);
            if (parsed is null) continue;
            views.Add(ToView(parsed, row.Name, row.Description, false,
                row.IsEnabled == 1, row.CreatedBy, row.UpdatedAt));
        }
        return views;
    }

    /// <summary>判异唯一解析入口：内置优先 → 自定义 → 回退默认，永不因配置丢失而静默放行。</summary>
    public async Task<RuleSetDefinition> ResolveAsync(string? version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(version))
            return RuleSetCatalog.Resolve(null);

        var builtIn = RuleSetCatalog.TryGet(version);
        if (builtIn is not null) return builtIn;

        var row = await repository.GetRuleSetAsync(version, ct);
        if (row is not null && row.IsEnabled == 1)
        {
            var parsed = Parse(row);
            if (parsed is not null) return parsed;
        }
        return RuleSetCatalog.Resolve(null);
    }

    public async Task<(bool Ok, string Error, RuleSetView? View)> SaveAsync(
        RuleSetSaveRequest request, CancellationToken ct)
    {
        string code = request.Code.Trim();
        if (code.Length is < 2 or > 32 ||
            !code.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            return (false, "规则集编码须为 2~32 位字母、数字、下划线或连字符。", null);
        if (code.StartsWith("WECO-", StringComparison.OrdinalIgnoreCase))
            return (false, "WECO- 前缀为内置预设保留，自定义规则集请使用其它编码。", null);
        if (string.IsNullOrWhiteSpace(request.Name))
            return (false, "规则集名称不能为空。", null);

        var rules = new List<RuleCode>();
        foreach (var raw in request.Rules)
        {
            var parsed = ParseCode(raw);
            if (parsed is null) return (false, $"非法规则码：{raw}（只允许 R1~R8）。", null);
            if (!rules.Contains(parsed.Value)) rules.Add(parsed.Value);
        }
        if (rules.Count == 0) return (false, "至少需要启用一条判异规则。", null);
        rules.Sort();

        var severity = new Dictionary<string, string>();
        foreach (var rule in rules)
        {
            string key = rule.Code();
            string level = request.Severity.TryGetValue(key, out var v) ? v : "WARNING";
            if (!Enum.TryParse<Severity>(level, true, out var parsed) ||
                !Enum.IsDefined(typeof(Severity), parsed))
                return (false, $"{key} 的严重级别 {level} 非法（INFO / WARNING / MAJOR / CRITICAL）。", null);
            severity[key] = parsed.ToString().ToUpperInvariant();
        }

        var now = DateTime.UtcNow;
        var existing = await repository.GetRuleSetAsync(code, ct);
        var row = existing ?? new SpcRuleSet { Code = code, CreatedAt = now };
        row.Name = request.Name.Trim();
        row.Description = request.Description.Trim();
        row.Rules = JsonSerializer.Serialize(rules.Select(r => r.Code()).ToArray());
        row.Severity = JsonSerializer.Serialize(severity);
        row.IsEnabled = request.Enabled ? 1 : 0;
        row.CreatedBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? "engineer" : request.CreatedBy.Trim();
        row.UpdatedAt = now;

        if (existing is null) await repository.InsertRuleSetAsync(row, ct);
        else await repository.UpdateRuleSetAsync(row, ct);

        await AuditAsync(0, "RULE_SET_SAVED", row.Id, new
        {
            code, row.Name, rules = row.Rules, severity = row.Severity, row.IsEnabled, row.CreatedBy
        }, ct);

        var definition = Parse(row)!;
        return (true, string.Empty, ToView(definition, row.Name, row.Description,
            false, row.IsEnabled == 1, row.CreatedBy, row.UpdatedAt));
    }

    /// <summary>把规则集绑定到控制图；绑定动作必须留审计（体系审核要回答「何时换了规则」）。</summary>
    public async Task<(bool Ok, string Error)> BindAsync(long chartId, string code, CancellationToken ct)
    {
        var chart = await repository.GetChartAsync(chartId, ct);
        if (chart is null) return (false, "控制图不存在。");
        var definition = await ResolveAsync(code, ct);
        if (!string.Equals(definition.Version, code, StringComparison.OrdinalIgnoreCase))
            return (false, $"规则集 {code} 不存在或已停用。");

        string previous = chart.RuleSetVer;
        chart.RuleSetVer = definition.Version;
        chart.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateChartAsync(chart, ct);
        await AuditAsync(chartId, "RULE_SET_BOUND", chart.Id,
            new { previous, current = definition.Version, chart.Code, rules = definition.Rules.Select(r => r.Code()) }, ct);
        return (true, string.Empty);
    }

    private static RuleSetView ToView(
        RuleSetDefinition s, string name, string description,
        bool builtIn, bool enabled, string createdBy, DateTime? updatedAt) => new(
        s.Version,
        name,
        description,
        s.Rules.Select(r => r.Code()).ToArray(),
        s.Severity.ToDictionary(k => k.Key.Code(), v => RuleSetCatalog.SeverityLabel(v.Value)),
        builtIn, enabled, createdBy, updatedAt);

    private static RuleSetDefinition? Parse(SpcRuleSet row)
    {
        try
        {
            var codes = JsonSerializer.Deserialize<List<string>>(row.Rules) ?? [];
            var levels = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Severity) ?? [];
            var rules = codes.Select(ParseCode).Where(c => c.HasValue)
                .Select(c => c!.Value).Distinct().Order().ToArray();
            if (rules.Length == 0) return null;

            var map = new Dictionary<RuleCode, Severity>();
            foreach (var rule in rules)
            {
                string key = rule.Code();
                map[rule] = levels.TryGetValue(key, out var level) &&
                            Enum.TryParse<Severity>(level, true, out var parsed) &&
                            Enum.IsDefined(typeof(Severity), parsed)
                    ? parsed
                    : Severity.Warning;
            }
            return new RuleSetDefinition(row.Code, rules, map, row.Description);
        }
        catch
        {
            return null;
        }
    }

    private static RuleCode? ParseCode(string? raw)
    {
        string text = (raw ?? string.Empty).Trim();
        if (text.StartsWith('R') || text.StartsWith('r')) text = text[1..];
        return int.TryParse(text, out int n) && Enum.IsDefined(typeof(RuleCode), n)
            ? (RuleCode)n
            : null;
    }

    private async Task AuditAsync(long chartId, string eventType, long entityId, object payload, CancellationToken ct)
        => await repository.InsertAuditAsync(new SpcAuditLog
        {
            EventTime = DateTime.UtcNow,
            EventType = eventType,
            EntityType = nameof(SpcRuleSet),
            EntityId = entityId,
            ChartId = chartId,
            Payload = JsonSerializer.Serialize(payload),
            Actor = "rule-config",
            CreatedAt = DateTime.UtcNow
        }, ct);
}
