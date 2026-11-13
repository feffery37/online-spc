using System.Collections.Concurrent;
using Spc.Application;

namespace Spc.WebApi;

/// <summary>
/// 按「机台+特性」即 ChartId 分区：每个分区一个信号量，保证同一张图串行消费；
/// 不同图并行。聚合时统一按 EVENT_TIME 重取，天然修正乱序与断网补传。
/// </summary>
public sealed class SubgroupWorker(
    IOrderedQueue queue,
    SubgroupAggregator aggregator,
    ILogger<SubgroupWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _gates = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("子组聚合 Worker 已启动（按 ChartId 分区串行）。");
        await foreach (var signal in queue.ReadAsync(stoppingToken))
        {
            var chartId = signal.ChartId;
            var gate = _gates.GetOrAdd(chartId, _ => new SemaphoreSlim(1, 1));
            _ = Task.Run(async () =>
            {
                try
                {
                    await gate.WaitAsync(stoppingToken);
                    try
                    {
                        int closed = await aggregator.ProcessAsync(chartId, stoppingToken);
                        if (closed > 0)
                            logger.LogDebug("ChartId={ChartId} 闭合 {Closed} 个子组", chartId, closed);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ChartId={ChartId} 子组聚合失败", chartId);
                }
            }, stoppingToken);
        }
    }
}
