using System.Text;

namespace Spc.Infrastructure;

/// <summary>
/// Domain 层保持零第三方依赖：实体不带 [SugarTable]/[SugarColumn] 特性，
/// 由 SqlSugar 的 ConfigureExternalServices 统一做 PascalCase -> UPPER_SNAKE_CASE 映射。
/// SpcChart -> SPC_CHART，ChartId -> CHART_ID，SigmaHat -> SIGMA_HAT。
/// </summary>
public static class DbNaming
{
    public static string Convert(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) &&
                (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append('_');
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public static string Table(string className) => Convert(className);
    public static string Column(string propertyName) => Convert(propertyName);
}
