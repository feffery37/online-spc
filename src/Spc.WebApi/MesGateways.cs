using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi;

/// <summary>
/// 内置 MES 模拟器：真实复刻 LOT Hold / EQP Hold / 停机 / 通知的下发与 Hold 状态，
/// 让处置闭环在本机即可端到端演练；生产切到 HttpMesGateway 时业务代码零改动。
/// </summary>
public sealed class SimulatedMesGateway : IMesGateway
{
    private readonly ConcurrentDictionary<string, SimulatedHoldState> _holds = new();
    private long _seq;

    public IReadOnlyList<SimulatedHoldState> Snapshot() =>
        _holds.Values.OrderByDescending(h => h.RequestTime).ToArray();

    public Task<MesHoldReceipt> SendHoldAsync(MesHoldRequest request, CancellationToken ct)
    {
        long seq = Interlocked.Increment(ref _seq);
        string mesRef = $"MES-{DateTime.UtcNow:yyyyMMdd}-{seq:D6}";
        var state = new SimulatedHoldState(
            mesRef, request.RuleOutId, request.ActionType, request.ChartCode,
            request.LotId, request.Sn, request.Equipment,
            "HELD", request.RequestTime, null, "mes-simulator");
        _holds[mesRef] = state;

        string raw = JsonSerializer.Serialize(new
        {
            mesRef,
            action = request.ActionType.ToString(),
            request.ChartCode,
            request.LotId,
            request.Sn,
            request.Equipment,
            reason = request.Reason
        });
        return Task.FromResult(new MesHoldReceipt(true, mesRef, "HELD", "mes-simulator", null, raw));
    }

    public Task<MesHoldReceipt?> QueryAsync(string mesRef, CancellationToken ct)
    {
        if (!_holds.TryGetValue(mesRef, out var state)) return Task.FromResult<MesHoldReceipt?>(null);
        return Task.FromResult<MesHoldReceipt?>(new MesHoldReceipt(
            true, mesRef, state.Status, state.Operator, state.ReleaseTime,
            JsonSerializer.Serialize(state)));
    }

    public Task<MesHoldReceipt?> ReleaseAsync(string mesRef, string operatorName, CancellationToken ct)
    {
        if (!_holds.TryGetValue(mesRef, out var state)) return Task.FromResult<MesHoldReceipt?>(null);
        var released = state with
        {
            Status = "RELEASED",
            ReleaseTime = DateTime.UtcNow,
            Operator = operatorName
        };
        _holds[mesRef] = released;
        return Task.FromResult<MesHoldReceipt?>(new MesHoldReceipt(
            true, mesRef, "RELEASED", operatorName, released.ReleaseTime,
            JsonSerializer.Serialize(released)));
    }
}

/// <summary>真实 MES 的 HTTP 适配器骨架。约定 POST {HoldUrl} 返回 MesHoldReceipt JSON。</summary>
public sealed class HttpMesGateway(HttpClient client, string holdUrl) : IMesGateway
{
    public async Task<MesHoldReceipt> SendHoldAsync(MesHoldRequest request, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(holdUrl, request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MesHoldReceipt>(ct)
               ?? new MesHoldReceipt(false, string.Empty, "NO_RECEIPT", "mes-http", null, string.Empty);
    }

    public Task<MesHoldReceipt?> QueryAsync(string mesRef, CancellationToken ct) =>
        Task.FromResult<MesHoldReceipt?>(null); // 由 MES 侧回调推送，不主动轮询

    public Task<MesHoldReceipt?> ReleaseAsync(string mesRef, string operatorName, CancellationToken ct) =>
        Task.FromResult<MesHoldReceipt?>(null);
}
