using TradeDemo.Api.Hubs;
using TradeDemo.Api.Journal;
using TradeDemo.Api.Models;
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
builder.Services.AddSingleton<GenerationStats>();
builder.Services.AddTickJournal(builder.Configuration);
builder.Services.AddSingleton<TickSequencer>();
builder.Services.AddSingleton<RiskEngine>();
builder.Services.AddSingleton<PositionManager>();
builder.Services.AddSingleton<ExchangeSimulator>();
builder.Services.AddSingleton<LosslessTickStore>();
builder.Services.AddSingleton<MarketDataSimulator>();
builder.Services.AddSingleton<TradeQueueProcessor>();
builder.Services.AddSingleton<ReplayEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LosslessTickStore>());
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

app.MapGet("/api/queue/stats", (TradeQueueProcessor processor, LosslessTickStore tickStore) => Results.Ok(new
{
    processed = processor.ProcessedCount,
    dropped = processor.DroppedCount,
    queueDepth = processor.QueueDepth,
    lossless = tickStore.GetStats(),
    timestamp = DateTime.UtcNow
}));

app.MapGet("/api/ticks/recent", (LosslessTickStore tickStore, int count = 1000) =>
    Results.Ok(tickStore.GetRecentTicks(count)));

app.MapGet("/api/ticks/from/{sequenceId:long}", (LosslessTickStore tickStore, long sequenceId, int count = 1000) =>
    Results.Ok(tickStore.GetTicksFromSequence(sequenceId, count)));

app.MapGet("/api/ticks/stats", (LosslessTickStore tickStore) => Results.Ok(tickStore.GetStats()));

app.MapGet("/api/system/metrics", (PerformanceMetrics metrics) => Results.Ok(metrics.GetSnapshot()));

// ── Order Flow Endpoints ──

app.MapPost("/api/orders", async (Order order, ExchangeSimulator exchange, CancellationToken ct) =>
{
    var result = await exchange.SubmitOrderAsync(order, ct);
    return Results.Ok(result);
});

app.MapGet("/api/orders", (ExchangeSimulator exchange) => Results.Ok(exchange.GetOrders()));

app.MapGet("/api/orders/open", (ExchangeSimulator exchange) => Results.Ok(exchange.GetOpenOrders()));

app.MapPost("/api/demo/reset", (ExchangeSimulator exchange) =>
{
    exchange.ResetDemo();
    return Results.Ok(new { Reset = true });
});

app.MapDelete("/api/orders/{orderId:guid}", (ExchangeSimulator exchange, Guid orderId) =>
{
    var result = exchange.CancelOrder(orderId);
    if (result is null)
    {
        return Results.NotFound();
    }

    return result.Order.Status == "Canceled" ? Results.Ok(result) : Results.Conflict(result);
});

app.MapPut("/api/orders/{orderId:guid}", async (ExchangeSimulator exchange, Guid orderId, ModifyOrderRequest request, CancellationToken ct) =>
{
    var result = await exchange.ModifyOrderAsync(orderId, request, ct);
    if (result is null)
    {
        return Results.NotFound();
    }

    return result.ExecutionReports.Any(r => r.Status == "ModifyRejected") ? Results.Conflict(result) : Results.Ok(result);
});

app.MapGet("/api/executions", (ExchangeSimulator exchange) => Results.Ok(exchange.GetExecutions()));

app.MapGet("/api/lifecycle/recent", (ExchangeSimulator exchange, int count = 80) => Results.Ok(exchange.GetRecentLifecycleEvents(count)));

app.MapGet("/api/execution-stats", (ExchangeSimulator exchange) => Results.Ok(exchange.GetExecutionStats()));

app.MapGet("/api/market-maker/state", (ExchangeSimulator exchange) => Results.Ok(exchange.GetMarketMakerState()));

app.MapGet("/api/depth/{symbol}", (ExchangeSimulator exchange, string symbol) => Results.Ok(exchange.GetDepth(symbol)));

app.MapGet("/api/positions", (PositionManager positions) => Results.Ok(positions.GetPositions()));

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

app.MapPost("/api/replay/recent", async (ReplayEngine engine, int count = 10_000, double speed = 1) =>
{
    await engine.StartRecentTicksReplayAsync(count, speed);
    return Results.Ok(engine.CurrentState);
});

app.MapPost("/api/replay/journal/from/{sequenceId:long}", async (ReplayEngine engine, long sequenceId, int count = 10_000, double speed = 1) =>
{
    await engine.StartJournalReplayFromSequenceAsync(sequenceId, count, speed);
    return Results.Ok(engine.CurrentState);
});

app.MapPost("/api/replay/stop", async (ReplayEngine engine) =>
{
    await engine.StopAsync();
    return Results.Ok(engine.CurrentState);
});

app.MapGet("/api/replay/state", (ReplayEngine engine) => Results.Ok(engine.CurrentState));

app.Run();
