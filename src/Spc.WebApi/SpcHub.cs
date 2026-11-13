using Microsoft.AspNetCore.SignalR;

namespace Spc.WebApi;

/// <summary>SignalR 推送：子组、量测、Rule Out、Hold 回执共用同一 Hub（方案 8.1）。</summary>
public sealed class SpcHub : Hub
{
    public override Task OnConnectedAsync() => base.OnConnectedAsync();
}
