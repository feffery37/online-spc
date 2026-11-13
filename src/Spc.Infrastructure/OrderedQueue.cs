using System.Threading.Channels;
using Spc.Application;

namespace Spc.Infrastructure;

/// <summary>
/// 进程内有序队列。物理部署时可由「机台+特性」分区的 Kafka 替换本实现（IOrderedQueue 不变）。
/// 聚合 Worker 按 ChartId 用独立信号量串行消费，保证同一张控制图的子组严格有序。
/// </summary>
public sealed class OrderedQueue : IOrderedQueue
{
    private readonly Channel<MeasureSignal> _channel =
        Channel.CreateUnbounded<MeasureSignal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public void Publish(MeasureSignal signal) => _channel.Writer.TryWrite(signal);

    public IAsyncEnumerable<MeasureSignal> ReadAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
