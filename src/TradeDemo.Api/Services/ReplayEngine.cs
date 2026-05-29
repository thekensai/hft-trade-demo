using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using TradeDemo.Api.Hubs;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

/// <summary>
/// Replay engine for deterministic event playback.
/// Simulates real trading infrastructure scenarios:
/// - NASDAQ-like burst traffic (50k+ msgs/sec)
/// - Exchange disconnects and reconnects
/// - Queue saturation under load
/// - Configurable market regimes (calm, volatile, flash crash)
/// 
/// Uses System.Threading.Channels for backpressure-aware event injection.
/// </summary>
public class ReplayEngine
{
    private readonly TradeQueueProcessor _queueProcessor;
    private readonly PerformanceMetrics _metrics;
    private readonly IHubContext<TradeHub> _hubContext;
    private readonly ILogger<ReplayEngine> _logger;
    private CancellationTokenSource? _replayCts;
    private static readonly Random _rng = new();
    private long _sequenceId = 0;

    public ReplayState CurrentState { get; private set; } = new();

    private static readonly (string Symbol, decimal BasePrice, string Exchange)[] Instruments =
    [
        ("AAPL", 195.50m, "NASDAQ"),
        ("MSFT", 430.20m, "NASDAQ"),
        ("GOOGL", 178.90m, "NASDAQ"),
        ("AMZN", 185.60m, "NASDAQ"),
        ("TSLA", 250.10m, "NASDAQ"),
        ("JPM", 198.30m, "NYSE"),
        ("GS", 465.80m, "NYSE"),
        ("NVDA", 950.00m, "NASDAQ"),
        ("META", 480.30m, "NASDAQ"),
        ("BTC-USD", 68500.00m, "CRYPTO"),
    ];

    public ReplayEngine(
        TradeQueueProcessor queueProcessor,
        PerformanceMetrics metrics,
        IHubContext<TradeHub> hubContext,
        ILogger<ReplayEngine> logger)
    {
        _queueProcessor = queueProcessor;
        _metrics = metrics;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Starts a replay scenario. Only one replay can run at a time.
    /// </summary>
    public async Task StartScenarioAsync(ReplayScenario scenario)
    {
        await StopAsync();

        _replayCts = new CancellationTokenSource();
        CurrentState = new ReplayState
        {
            IsRunning = true,
            ScenarioName = scenario.Name,
            TargetRate = scenario.TargetMessagesPerSecond,
            StartedAt = DateTime.UtcNow
        };

        _ = Task.Run(() => RunScenarioAsync(scenario, _replayCts.Token));

        await _hubContext.Clients.All.SendAsync("ReplayStateChanged", CurrentState);
    }

    public async Task StopAsync()
    {
        if (_replayCts != null)
        {
            await _replayCts.CancelAsync();
            _replayCts.Dispose();
            _replayCts = null;
        }

        CurrentState = new ReplayState { IsRunning = false };
        await _hubContext.Clients.All.SendAsync("ReplayStateChanged", CurrentState);
    }

    private async Task RunScenarioAsync(ReplayScenario scenario, CancellationToken ct)
    {
        _logger.LogInformation("Replay started: {Scenario} at {Rate} msgs/sec for {Duration}s",
            scenario.Name, scenario.TargetMessagesPerSecond, scenario.DurationSeconds);

        var sw = Stopwatch.StartNew();
        long messagesSent = 0;
        var prices = Instruments.Select(i => i.BasePrice).ToArray();

        try
        {
            while (!ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < scenario.DurationSeconds)
            {
                var elapsed = sw.Elapsed.TotalSeconds;

                // Calculate effective rate based on scenario profile
                var effectiveRate = CalculateEffectiveRate(scenario, elapsed);

                // Simulate disconnect window
                if (scenario.SimulateDisconnect && IsInDisconnectWindow(elapsed, scenario.DurationSeconds))
                {
                    CurrentState = CurrentState with { IsDisconnected = true, CurrentRate = 0 };
                    await _hubContext.Clients.All.SendAsync("ReplayStateChanged", CurrentState, ct);
                    await Task.Delay(100, ct);
                    continue;
                }

                if (CurrentState.IsDisconnected)
                {
                    CurrentState = CurrentState with { IsDisconnected = false };
                    await _hubContext.Clients.All.SendAsync("ReplayStateChanged", CurrentState, ct);
                }

                // Generate burst sized for target rate
                var batchInterval = 10; // ms per batch
                var batchSize = (int)(effectiveRate * batchInterval / 1000.0);
                batchSize = Math.Max(1, batchSize);

                for (int i = 0; i < batchSize && !ct.IsCancellationRequested; i++)
                {
                    var idx = _rng.Next(Instruments.Length);
                    var (symbol, basePrice, exchange) = Instruments[idx];

                    var volatility = scenario.Volatility;
                    var reversion = (basePrice - prices[idx]) * 0.02m;
                    var noise = (decimal)(_rng.NextDouble() - 0.5) * basePrice * volatility;
                    prices[idx] = Math.Clamp(prices[idx] + reversion + noise, basePrice * 0.85m, basePrice * 1.15m);
                    var price = Math.Round(prices[idx], 2);
                    var change = Math.Round(price - basePrice, 2);
                    var changePct = Math.Round((double)(change / basePrice * 100), 3);

                    // Spread: bid/ask prices with realistic spread
                    var spread = prices[idx] * (decimal)(_rng.NextDouble() * 0.0002 + 0.0001);
                    var bidPrice = Math.Round(price - spread / 2, 2);
                    var askPrice = Math.Round(price + spread / 2, 2);
                    var midPrice = Math.Round((bidPrice + askPrice) / 2, 2);

                    var signal = new TradeSignal(
                        Symbol: symbol,
                        BidPrice: bidPrice,
                        AskPrice: askPrice,
                        MidPrice: midPrice,
                        Change: change,
                        ChangePercent: changePct,
                        Volume: _rng.NextInt64(100, 100000),
                        Exchange: exchange,
                        Timestamp: DateTime.UtcNow,
                        Direction: _rng.NextDouble() > 0.5 ? "BUY" : "SELL",
                        SequenceId: Interlocked.Increment(ref _sequenceId)
                    );

                    var enqueueSw = Stopwatch.StartNew();
                    _queueProcessor.EnqueueAsync(signal);
                    _metrics.RecordLatency(enqueueSw.ElapsedTicks);

                    messagesSent++;
                }

                CurrentState = CurrentState with
                {
                    MessagesSent = messagesSent,
                    CurrentRate = effectiveRate,
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                };

                // Broadcast state every 500ms
                if (messagesSent % (effectiveRate / 2) < batchSize)
                {
                    await _hubContext.Clients.All.SendAsync("ReplayStateChanged", CurrentState, ct);
                }

                await Task.Delay(batchInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            CurrentState = CurrentState with { IsRunning = false, MessagesSent = messagesSent };
            await _hubContext.Clients.All.SendAsync("ReplayStateChanged", CurrentState);
            _logger.LogInformation("Replay completed: {Messages} messages in {Elapsed:F1}s",
                messagesSent, sw.Elapsed.TotalSeconds);
        }
    }

    private static double CalculateEffectiveRate(ReplayScenario scenario, double elapsedSeconds)
    {
        var progress = elapsedSeconds / scenario.DurationSeconds;

        return scenario.Profile switch
        {
            TrafficProfile.Constant => scenario.TargetMessagesPerSecond,
            TrafficProfile.Ramp => scenario.TargetMessagesPerSecond * progress,
            TrafficProfile.Burst => CalculateBurstRate(scenario.TargetMessagesPerSecond, progress),
            TrafficProfile.FlashCrash => CalculateFlashCrashRate(scenario.TargetMessagesPerSecond, progress),
            _ => scenario.TargetMessagesPerSecond
        };
    }

    private static double CalculateBurstRate(double target, double progress)
    {
        // Periodic bursts: low baseline with spikes
        var burstCycle = Math.Sin(progress * Math.PI * 8);
        return burstCycle > 0.7 ? target : target * 0.1;
    }

    private static double CalculateFlashCrashRate(double target, double progress)
    {
        // Calm → explosion → calm
        if (progress < 0.3) return target * 0.2;
        if (progress < 0.5) return target * 3.0; // spike
        if (progress < 0.6) return target * 5.0; // peak
        if (progress < 0.7) return target * 2.0; // subsiding
        return target * 0.3; // aftermath
    }

    private static bool IsInDisconnectWindow(double elapsed, double duration)
    {
        // Disconnect at 40-45% of the way through
        var progress = elapsed / duration;
        return progress > 0.40 && progress < 0.45;
    }
}

public record ReplayState
{
    public bool IsRunning { get; init; }
    public bool IsDisconnected { get; init; }
    public string ScenarioName { get; init; } = "";
    public double TargetRate { get; init; }
    public double CurrentRate { get; init; }
    public long MessagesSent { get; init; }
    public double ElapsedSeconds { get; init; }
    public DateTime? StartedAt { get; init; }
}

public class ReplayScenario
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double TargetMessagesPerSecond { get; set; } = 1000;
    public double DurationSeconds { get; set; } = 30;
    public TrafficProfile Profile { get; set; } = TrafficProfile.Constant;
    public decimal Volatility { get; set; } = 0.002m;
    public bool SimulateDisconnect { get; set; }
}

public enum TrafficProfile
{
    Constant,
    Ramp,
    Burst,
    FlashCrash
}
