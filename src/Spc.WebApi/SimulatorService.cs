using Spc.Application;
using Spc.Domain;

namespace Spc.WebApi;

/// <summary>
/// 数据源模拟器（替代现场 OPC UA / MQTT / SECS-GEM / 串口 / MES EDC，接口一致）。
/// 始终经 IngestService 写入，携带完整上下文标签与 PROC_TAG（方案 F-02）。
/// </summary>
public sealed class SimulatorService(
    IngestService ingest,
    IRealtimePublisher publisher,
    ISpcRepository repository,
    ILogger<SimulatorService> logger)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private SimulatorRequest _config = new();
    private long _generated;
    private long _index;

    public SimulatorState GetState() => new()
    {
        Running = _cts is not null,
        Scenario = _config.Scenario,
        IntervalMs = _config.IntervalMs,
        ChartCode = _config.ChartCode,
        Generated = Interlocked.Read(ref _generated)
    };

    public bool Start(SimulatorRequest request)
    {
        lock (_gate)
        {
            if (_cts is not null) return false;
            request.IntervalMs = Math.Clamp(request.IntervalMs, 50, 10_000);
            request.Burst = Math.Clamp(request.Burst, 1, 20);
            _config = request;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_config, _cts.Token));
        }
        logger.LogInformation("模拟器启动：{Chart} / {Scenario} / {IntervalMs}ms",
            request.ChartCode, request.Scenario, request.IntervalMs);
        return true;
    }

    public bool Stop()
    {
        lock (_gate)
        {
            if (_cts is null) return false;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
        return true;
    }

    /// <summary>
    /// 断网续传演练：生成一批事件时间在过去窗口内的点，故意打乱到达顺序提交，
    /// 验证聚合按 EVENT_TIME 而非到达时间形成子组（方案 N-02）。
    /// </summary>
    public async Task<IngestResult> ReplayOutOfOrderAsync(string chartCode, int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 5, 500);
        var rnd = new Random(20261113);
        var anchor = DateTime.UtcNow.AddMinutes(-3);
        var items = Enumerable.Range(0, count)
            .Select(i => BuildItem(chartCode, anchor.AddSeconds(i * 0.35), i, "stable", rnd))
            .OrderBy(_ => rnd.Next()) // 故意乱序到达
            .ToList();

        await publisher.PublishNoticeAsync(new
        {
            type = "notice",
            at = DateTime.UtcNow,
            message = $"断网续传演练：{count} 个点乱序到达，聚合须按 EVENT_TIME 重放"
        }, ct);

        return await ingest.IngestAsync(new IngestBatchRequest { Items = items }, ct);
    }

    /// <summary>
    /// 一键生成 Phase I 候选数据：确定性无趋势稳定噪声（不含 R2/R3/R4 模式），
    /// 按控制图的子组大小生成足量合理子组，事件时间严格有序并带完整上下文标签。
    /// </summary>
    public async Task<object> GenerateBaselineDataAsync(string chartCode, int subgroups, CancellationToken ct)
    {
        subgroups = Math.Clamp(subgroups, 20, 200);
        var chart = await repository.GetChartByCodeAsync(chartCode, ct);
        if (chart is null) return new { ok = false, error = $"控制图 {chartCode} 不存在。" };

        int n = (ChartType)chart.ChartType == ChartType.IMR ? 1 : Math.Clamp(chart.SubgroupSize, 2, 10);
        var anchor = DateTime.UtcNow.AddMinutes(-subgroups - 5);
        var items = new List<MeasureIngestItem>(subgroups * n);
        long index = 0;

        for (int g = 0; g < subgroups; g++)
        {
            for (int j = 0; j < n; j++)
            {
                index++;
                double raw = Math.Sin((g * 127.1 + j * 311.7 + 13.7) * 0.0174532925199433) * 43758.5453;
                double noise = (raw - Math.Floor(raw)) - 0.5;
                var eventTime = anchor.AddMinutes(g).AddSeconds(j * 6);
                items.Add(new MeasureIngestItem
                {
                    ChartCode = chartCode,
                    EventTime = eventTime,
                    Value = Math.Round(10.0 + 0.09 * noise, 6),
                    LotId = "LOT-BASELINE-01",
                    Sn = $"SN-BL-{index:D8}",
                    WorkOrder = "WO-BASELINE-01",
                    Shift = "A",
                    Recipe = chart.Recipe,
                    Equipment = chart.Machine,
                    Source = (int)MeasureSource.Simulator,
                    ProcTag = new Dictionary<string, double>
                    {
                        ["temperature"] = Math.Round(22.5 + 0.4 * noise, 3),
                        ["pressure"] = Math.Round(0.62 + 0.01 * noise, 4),
                        ["spindle_speed"] = Math.Round(1200 + 8 * noise, 1)
                    }
                });
            }
        }

        var result = await ingest.IngestAsync(new IngestBatchRequest { Items = items }, ct);
        await publisher.PublishNoticeAsync(new
        {
            type = "notice",
            at = DateTime.UtcNow,
            message = $"Phase I 候选数据已生成：{subgroups} 个子组 × n={n}，共 {items.Count} 点（稳定过程，按事件时间有序）"
        }, ct);

        return new
        {
            ok = result.Rejected == 0,
            chartCode,
            subgroupCount = subgroups,
            subgroupSize = n,
            points = items.Count,
            result,
            note = "数据就绪后到「基线管理」执行预检 → 冻结 → 人工审批发布。"
        };
    }

    private async Task LoopAsync(SimulatorRequest config, CancellationToken ct)
    {
        var rnd = new Random(Environment.TickCount);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = new List<MeasureIngestItem>();
                for (int b = 0; b < config.Burst; b++)
                {
                    long idx = Interlocked.Increment(ref _index);
                    batch.Add(BuildItem(config.ChartCode,
                        DateTime.UtcNow.AddMilliseconds(b), idx, config.Scenario, rnd));
                }

                var result = await ingest.IngestAsync(new IngestBatchRequest { Items = batch }, ct);
                Interlocked.Add(ref _generated, result.Accepted);
                await Task.Delay(config.IntervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "模拟器异常停止");
            Stop();
        }
    }

    private static MeasureIngestItem BuildItem(
        string chartCode, DateTime eventTime, long index, string scenario, Random rnd)
    {
        double noise = Gaussian(rnd, 0, 0.03);
        double value = 10.0 + noise;
        // 每个点都带工艺参数标签，判异后可做根因关联（附录 A）。
        double temperature = 22.5 + Gaussian(rnd, 0, 0.4);
        double pressure = 0.62 + Gaussian(rnd, 0, 0.01);
        double speed = 1200 + Gaussian(rnd, 0, 8);

        switch (scenario)
        {
            case "drift": // 均值突变：模拟换料/换刀后的持续偏移
                if (index > 40) { value += 0.16; temperature += 3.5; }
                break;
            case "trend": // 趋势性漂移：刀具磨损
                value += Math.Min(index, 400) * 0.00035;
                speed += Math.Min(index, 400) * 0.05;
                break;
            case "oscillation": // 系统性振荡
                value += (index % 2 == 0 ? 0.14 : -0.14);
                break;
            case "spike": // 突发大偏移
                if (index % 47 == 0) { value += 0.45; pressure += 0.12; }
                break;
        }

        return new MeasureIngestItem
        {
            ChartCode = chartCode,
            EventTime = eventTime,
            Value = Math.Round(value, 6),
            LotId = "LOT-20261113-01",
            Sn = $"SN-{index:D8}",
            WorkOrder = "WO-2026-1113-01",
            Shift = eventTime.Hour < 12 ? "A" : "B",
            Recipe = "RCP-STD-01",
            Equipment = "CA6140-01",
            Source = (int)MeasureSource.Simulator,
            ProcTag = new Dictionary<string, double>
            {
                ["temperature"] = Math.Round(temperature, 3),
                ["pressure"] = Math.Round(pressure, 4),
                ["spindle_speed"] = Math.Round(speed, 1)
            }
        };
    }

    private static double Gaussian(Random rnd, double mean, double sd)
    {
        double u1 = Math.Max(rnd.NextDouble(), 1e-9), u2 = rnd.NextDouble();
        return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
