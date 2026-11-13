using Spc.Domain;

namespace Spc.Application;

public sealed class ChartService(ISpcRepository repository)
{
    public async Task<(bool Ok, string Error, SpcChart? Chart)> UpsertAsync(ChartUpsertRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return (false, "控制图编码不能为空。", null);
        if (!Enum.IsDefined(typeof(ChartType), request.ChartType)) return (false, "不支持的控制图类型。", null);
        var type = (ChartType)request.ChartType;
        if (type != ChartType.IMR && (request.SubgroupSize < 2 || request.SubgroupSize > 10))
            return (false, "计量型子组大小须为 2~10（I-MR 固定 n=1）。", null);
        if (request.Usl.HasValue && request.Lsl.HasValue && request.Usl <= request.Lsl)
            return (false, "USL 必须大于 LSL。", null);

        var existing = await repository.GetChartByCodeAsync(request.Code, ct);
        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            existing.Name = request.Name;
            existing.ChartType = request.ChartType;
            existing.SubgroupSize = request.SubgroupSize;
            existing.Usl = request.Usl;
            existing.Lsl = request.Lsl;
            existing.Target = request.Target;
            existing.Unit = request.Unit;
            existing.Machine = request.Machine;
            existing.Product = request.Product;
            existing.Process = request.Process;
            existing.Recipe = request.Recipe;
            existing.RuleSetVer = request.RuleSetVer;
            existing.IsEnabled = request.IsEnabled;
            existing.UpdatedAt = now;
            await repository.UpdateChartAsync(existing, ct);
            return (true, string.Empty, existing);
        }

        var chart = new SpcChart
        {
            Code = request.Code,
            Name = request.Name,
            ChartType = request.ChartType,
            SubgroupSize = request.SubgroupSize,
            Usl = request.Usl,
            Lsl = request.Lsl,
            Target = request.Target,
            Unit = request.Unit,
            Machine = request.Machine,
            Product = request.Product,
            Process = request.Process,
            Recipe = request.Recipe,
            RuleSetVer = request.RuleSetVer,
            IsEnabled = request.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.InsertChartAsync(chart, ct);
        return (true, string.Empty, chart);
    }
}
