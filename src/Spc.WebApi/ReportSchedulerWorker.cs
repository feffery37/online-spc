using Spc.Application;

namespace Spc.WebApi;

/// <summary>
/// M5 定时报表（方案 9 M5「按日周月自动出报表，定时推送」）。
/// 周期性为所有启用控制图生成带指纹的快照并 SignalR 推送；间隔由配置控制，可手动触发演练。
/// </summary>
public sealed class ReportSchedulerWorker(
    ISpcRepository repository,
    ReportService report,
    IConfiguration configuration,
    ILogger<ReportSchedulerWorker> logger) : BackgroundService
{
    private int _generated;
    private DateTime? _lastRun;

    public bool Enabled => configuration.GetValue<bool>("Spc:Reports:Enabled", true);
    public int IntervalMinutes => Math.Max(1, configuration.GetValue<int>("Spc:Reports:IntervalMinutes", 5));

    public ReportScheduleDto GetState() => new()
    {
        Enabled = Enabled,
        IntervalMinutes = IntervalMinutes,
        LastRun = _lastRun,
        GeneratedCount = _generated,
        Note = "定时生成 DAILY 报表快照并推送 report 事件；可用 POST /api/spc/reports/schedule/run 手动触发"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try
        {
            // 启动后先等 30 秒，避免空库时生成无意义的快照。
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(IntervalMinutes));
            do
            {
                await GenerateOnce(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    public async Task GenerateOnce(CancellationToken ct)
    {
        try
        {
            var charts = await repository.GetChartsAsync(ct);
            foreach (var chart in charts.Where(c => c.IsEnabled == 1))
            {
                try
                {
                    await report.GenerateSnapshotAsync(chart.Id, "DAILY", "scheduler", ct);
                    Interlocked.Increment(ref _generated);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "定时报表生成失败：ChartId={ChartId}", chart.Id);
                }
            }
            _lastRun = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 后台任务异常不得拖垮整个 Host（Program.cs 另配置 Ignore 双保险）。
            logger.LogError(ex, "定时报表任务异常");
        }
    }
}
