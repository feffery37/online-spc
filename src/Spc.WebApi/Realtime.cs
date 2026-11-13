using Microsoft.AspNetCore.SignalR;
using Spc.Application;

namespace Spc.WebApi;

public sealed class SignalRPublisher(IHubContext<SpcHub> hub) : IRealtimePublisher
{
    public Task PublishSubgroupAsync(object payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync("subgroup", payload, ct);

    public Task PublishMeasureAsync(object payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync("measure", payload, ct);

    public Task PublishNoticeAsync(object payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync("notice", payload, ct);

    public Task PublishRuleOutAsync(object payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync("ruleout", payload, ct);

    public Task PublishDispositionAsync(object payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync("disposition", payload, ct);

    public Task PublishReportAsync(object payload, CancellationToken ct) =>
        hub.Clients.All.SendAsync("report", payload, ct);
}
