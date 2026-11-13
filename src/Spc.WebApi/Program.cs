using Spc.Application;
using Spc.Infrastructure;
using Spc.WebApi;

var builder = WebApplication.CreateBuilder(args);

string provider = builder.Configuration["Spc:Provider"] ?? "Sqlite";
string connection = builder.Configuration.GetConnectionString("Spc") ?? "Data Source=spc.db";

// 相对路径的 SQLite 文件解析到工作目录，保证从仓库根目录 dotnet run 时落点固定。
if (!provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase) &&
    connection.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    const string prefix = "Data Source=";
    string file = connection[prefix.Length..].Split(';')[0];
    if (!Path.IsPathRooted(file))
        connection = connection.Replace(file, Path.Combine(Directory.GetCurrentDirectory(), file),
            StringComparison.OrdinalIgnoreCase);
}

var db = SpcDatabase.Create(provider, connection);
int ddlCount = SpcDatabase.Initialize(db, provider, builder.Environment.ContentRootPath);
Console.WriteLine($"[SPC] Provider={provider}，DDL 执行 {ddlCount} 条语句。");

builder.Services.AddSingleton(db);
builder.Services.AddSingleton<ISpcRepository, SpcRepository>();
builder.Services.AddSingleton<IOrderedQueue, OrderedQueue>();
// M2 起：判异取限只走冻结版本表（FrozenLimitReader），不再使用 M1 的空实现。
builder.Services.AddSingleton<ILimitReader, FrozenLimitReader>();
builder.Services.AddSingleton<IRealtimePublisher, SignalRPublisher>();
builder.Services.AddSingleton<IngestService>();
builder.Services.AddSingleton<ChartService>();
builder.Services.AddSingleton<BaselineFreezeService>();
builder.Services.AddSingleton<BaselinePublishService>();
builder.Services.AddSingleton<CapabilityService>();

// M4：MES 网关按配置切换；模拟器始终注册，供 /api/spc/mes/state 查看 Hold 状态。
builder.Services.AddSingleton<SimulatedMesGateway>();
string mesMode = builder.Configuration["Spc:MesGateway:Mode"] ?? "Simulated";
if (mesMode.Equals("Http", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient();
    string holdUrl = builder.Configuration["Spc:MesGateway:HoldUrl"]
                     ?? "http://localhost:5090/api/mes/hold";
    builder.Services.AddSingleton<IMesGateway>(sp => new HttpMesGateway(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(), holdUrl));
}
else
{
    builder.Services.AddSingleton<IMesGateway>(sp => sp.GetRequiredService<SimulatedMesGateway>());
}
builder.Services.AddSingleton<DispositionService>();
builder.Services.AddSingleton<IDispositionTrigger>(sp => sp.GetRequiredService<DispositionService>());
builder.Services.AddSingleton<EvaluationService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<ReportSchedulerWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportSchedulerWorker>());
builder.Services.AddSingleton<SubgroupAggregator>(sp => new SubgroupAggregator(
    sp.GetRequiredService<ISpcRepository>(),
    sp.GetRequiredService<IRealtimePublisher>(),
    sp.GetRequiredService<ILimitReader>(),
    sp.GetRequiredService<EvaluationService>(),
    TimeSpan.FromSeconds(builder.Configuration.GetValue<double>("Spc:LateArrivalToleranceSeconds", 0))));
builder.Services.AddSingleton<SimulatorService>();
builder.Services.AddHostedService<SubgroupWorker>();
builder.Services.AddHostedService<PendingSweepWorker>();

builder.Services.AddControllers();
builder.Services.AddSignalR();
// 后台任务（采集/重扫/报表）异常只记录日志，不停止 Web Host。
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddCors(options => options.AddPolicy("web", policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:4173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors("web");
app.MapControllers();
app.MapHub<SpcHub>("/hubs/spc");

// 首次启动植入演示控制图：Xbar-R / n=5 / 规格限与控制限物理分离。
using (var scope = app.Services.CreateScope())
{
    await DbSeeder.EnsureDemoChartAsync(scope.ServiceProvider.GetRequiredService<ChartService>(),
        app.Lifetime.ApplicationStopping);
}

app.Urls.Add("http://localhost:5080");
app.Run();
