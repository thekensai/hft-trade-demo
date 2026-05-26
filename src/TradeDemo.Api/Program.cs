using TradeDemo.Api.Hubs;
using TradeDemo.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddSingleton<PerformanceMetrics>();
builder.Services.AddSingleton<MarketDataSimulator>();
builder.Services.AddSingleton<TradeQueueProcessor>();
builder.Services.AddSingleton<ReplayEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketDataSimulator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TradeQueueProcessor>());

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<TradeHub>("/tradehub");

// ── API Endpoints ──

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapGet("/api/metrics", (PerformanceMetrics metrics) => Results.Ok(metrics.GetSnapshot()));

app.MapGet("/api/metrics/stream", async (PerformanceMetrics metrics, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.ContentType = "text/event-stream";
    while (!ct.IsCancellationRequested)
    {
        var snapshot = metrics.GetSnapshot();
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        await Task.Delay(1000, ct);
    }
});

app.MapGet("/api/queue/stats", (TradeQueueProcessor processor) => Results.Ok(new
{
    processed = processor.ProcessedCount,
    dropped = processor.DroppedCount,
    queueDepth = processor.QueueDepth,
    timestamp = DateTime.UtcNow
}));

// ── Replay Engine Endpoints ──

app.MapGet("/api/replay/scenarios", () => Results.Ok(new[]
{
    new ReplayScenario { Name = "Calm Market", Description = "Steady-state trading at 200 msgs/sec", TargetMessagesPerSecond = 200, DurationSeconds = 30, Profile = TrafficProfile.Constant, Volatility = 0.001m },
    new ReplayScenario { Name = "NASDAQ Burst", Description = "Exchange burst traffic ramping to 5K msgs/sec with periodic spikes", TargetMessagesPerSecond = 5000, DurationSeconds = 30, Profile = TrafficProfile.Burst, Volatility = 0.003m },
    new ReplayScenario { Name = "Flash Crash", Description = "Simulates a flash crash: calm → explosion → peak → aftermath", TargetMessagesPerSecond = 10000, DurationSeconds = 20, Profile = TrafficProfile.FlashCrash, Volatility = 0.015m },
    new ReplayScenario { Name = "Ramp to Saturation", Description = "Linear ramp from 0 to 20K msgs/sec to test queue saturation", TargetMessagesPerSecond = 20000, DurationSeconds = 30, Profile = TrafficProfile.Ramp, Volatility = 0.005m },
    new ReplayScenario { Name = "Exchange Disconnect", Description = "Steady feed with a 5% disconnect window simulating exchange outage + reconnect", TargetMessagesPerSecond = 3000, DurationSeconds = 30, Profile = TrafficProfile.Constant, Volatility = 0.002m, SimulateDisconnect = true },
}));

app.MapPost("/api/replay/start", async (ReplayScenario scenario, ReplayEngine engine) =>
{
    await engine.StartScenarioAsync(scenario);
    return Results.Ok(engine.CurrentState);
});

app.MapPost("/api/replay/stop", async (ReplayEngine engine) =>
{
    await engine.StopAsync();
    return Results.Ok(engine.CurrentState);
});

app.MapGet("/api/replay/state", (ReplayEngine engine) => Results.Ok(engine.CurrentState));

app.Run();
