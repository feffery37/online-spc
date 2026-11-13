using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi;

public static class DbSeeder
{
    public static async Task EnsureDemoChartAsync(ChartService charts, CancellationToken ct)
    {
        await charts.UpsertAsync(new ChartUpsertRequest
        {
            Code = "CH-DEMO-01",
            Name = "缸套外径 · 精车工序 · CA6140",
            ChartType = (int)ChartType.XbarR,
            SubgroupSize = 5,
            Target = 10.0,
            Usl = 10.10,
            Lsl = 9.90,
            Unit = "mm",
            Machine = "CA6140-01",
            Product = "CYL-LINER-A",
            Process = "FINISH-TURN",
            Recipe = "RCP-STD-01",
            RuleSetVer = "WECO-8-V1",
            IsEnabled = 1
        }, ct);
    }
}
