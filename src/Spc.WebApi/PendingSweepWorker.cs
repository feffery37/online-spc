using Spc.Application;

namespace Spc.WebApi;

/// <summary>
/// 迟到水位线的兜底重扫。事件时间略超前于到达时刻、或断网补传的点会被聚合器暂时跳过，
/// 若没有新信号触发就可能永远不闭组。本 Worker 周期性对所有启用控制图发一次重扫信号，
/// 保证水位线追上后 ≤5 秒内完成补聚合（N-02 / A-06）。
/// </summary>
public sealed class PendingSweepWorker(
    ISpcRepository repository,
    IOrderedQueue queue,
    ILogger<PendingSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var charts = await repository.GetChartsAsync(stoppingToken);
                foreach (var chart in charts.Where(c => c.IsEnabled == 1))
                    queue.Publish(new MeasureSignal(chart.Id, DateTime.UtcNow, 0));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "待处理量测点重扫失败");
            }
        }
    }
}
