using System.Threading.Channels;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using TradeDemo.Api.Hubs;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

/// <summary>
/// High-throughput market data simulator optimized for 1M+ events/sec.
///
/// Key optimizations for 1M/sec:
/// - No Task.Delay: continuous generation with periodic yield
/// - No per-event await: uses TryWrite directly
/// - Batched timestamps: 1 DateTime.UtcNow per 10,000 events
/// - Readonly record struct TradeSignal: no GC pressure
/// - Huge batch sizes: 10,000 events per inner loop
/// - Preallocated string references: Symbol/Exchange/Direction reused
/// - Periodic throughput logging
/// </summary>
public class MarketDataSimulator : BackgroundService
{
    private readonly ILogger<MarketDataSimulator> _logger;
    private readonly TradeQueueProcessor _queueProcessor;
    private readonly GenerationStats _generationStats;
    private readonly LosslessTickStore _tickStore;
    private readonly TickSequencer _sequencer;
    private static readonly Random _rng = new();

    // Preallocated string references to avoid allocations
    private const string Buy = "BUY";
    private const string Sell = "SELL";

    private static readonly (string Symbol, decimal BasePrice, string Exchange)[] Instruments =
    [
        ("ES", 5982.00m, "CME"),
        ("NQ", 21850.50m, "CME"),
        ("AAPL", 195.50m, "NASDAQ"),
        ("MSFT", 430.20m, "NASDAQ"),
        ("GOOGL", 178.90m, "NASDAQ"),
        ("AMZN", 185.60m, "NASDAQ"),
        ("TSLA", 250.10m, "NASDAQ"),
        ("JPM", 198.30m, "NYSE"),
        ("GS", 465.80m, "NYSE"),
        ("BAC", 38.90m, "NYSE"),
        ("V", 278.40m, "NYSE"),
        ("BRK.B", 415.70m, "NYSE"),
        ("NVDA", 950.00m, "NASDAQ"),
        ("META", 480.30m, "NASDAQ"),
        ("BTC-USD", 68500.00m, "CRYPTO"),
        ("ETH-USD", 3750.00m, "CRYPTO"),
        ("SOL-USD", 172.50m, "CRYPTO"),
    ];

    private readonly decimal[] _currentPrices;

    // Event clustering state kept for realistic behavior
    private double _intensity = 5.0;
    private int _sameSideStreak = 0;
    private string? _lastDirection;

    public MarketDataSimulator(
        ILogger<MarketDataSimulator> logger,
        TradeQueueProcessor queueProcessor,
        GenerationStats generationStats,
        LosslessTickStore tickStore,
        TickSequencer sequencer)
    {
        _logger = logger;
        _queueProcessor = queueProcessor;
        _generationStats = generationStats;
        _tickStore = tickStore;
        _sequencer = sequencer;
        _currentPrices = Instruments.Select(i => i.BasePrice).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataSimulator started - HIGH THROUGHPUT MODE targeting 1M+ events/sec, {Count} instruments", Instruments.Length);

        // Use TryEnqueue to ensure the channel's enqueue counter increments
        // (required for accurate dropped-count accounting under DropOldest).

        var throughputTimer = Stopwatch.StartNew();
        var lastLogTime = Stopwatch.GetTimestamp();
        long lastLoggedCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Batch timestamp: 1 call per batch instead of per event
            var batchTimestamp = DateTime.UtcNow;

            // Hawkes-like: Vary batch size based on intensity!
            var burstSize = (int)(5000 + (_intensity * 5000)); // 5k to 55k events per burst
            burstSize = Math.Clamp(burstSize, 2000, 60000);

            // Random spikes to simulate news events!
            if (_rng.NextDouble() < 0.02)
            {
                _intensity = 10.0; // Max intensity for a big burst!
                burstSize = 100000; // Super-sized burst!
            }

            // Update intensity
            if (_rng.NextDouble() < 0.1)
            {
                _intensity = Math.Max(0.5, _intensity * 0.7); // Decay
            }
            else
            {
                _intensity = Math.Min(10.0, _intensity * 1.05); // Grow
            }

            for (int batchIdx = 0; batchIdx < burstSize; batchIdx++)
            {
                var idx = _rng.Next(Instruments.Length);
                var (symbol, basePrice, exchange) = Instruments[idx];

                // Fast price update
                var reversion = (basePrice - _currentPrices[idx]) * 0.005m;
                var noise = (decimal)(_rng.NextDouble() - 0.5) * basePrice * 0.0005m;
                _currentPrices[idx] = Math.Clamp(_currentPrices[idx] + reversion + noise, basePrice * 0.95m, basePrice * 1.05m);
                var price = _currentPrices[idx];

                // Fast direction selection
                string direction;
                if (_lastDirection != null && _sameSideStreak > 0 && _rng.NextDouble() < 0.6)
                {
                    direction = _lastDirection;
                    _sameSideStreak++;
                }
                else
                {
                    direction = _rng.NextDouble() > 0.5 ? Buy : Sell;
                    _lastDirection = direction;
                    _sameSideStreak = _rng.Next(1, 8);
                }

                // Simplified spread calculation (faster)
                var spread = price * 0.00015m;
                var bidPrice = price - spread;
                var askPrice = price + spread;
                var midPrice = price;
                var tradePrice = direction == Buy ? askPrice : bidPrice;
                var change = tradePrice - basePrice;
                var changePct = (double)(change / basePrice * 100);

                var signal = new TradeSignal(
                    Symbol: symbol,
                    BidPrice: bidPrice,
                    AskPrice: askPrice,
                    MidPrice: midPrice,
                    Change: change,
                    ChangePercent: changePct,
                    Volume: _rng.NextInt64(100, 50000),
                    Exchange: exchange,
                    Timestamp: batchTimestamp,
                    Direction: direction,
                    SequenceId: _sequencer.Next()
                );

                // Split the pipeline immediately:
                // - Lossless/replay path gets every accepted tick in sequence order.
                // - UI path remains coalesced and browser-friendly.
                _tickStore.TryAppend(signal);
                _queueProcessor.TryEnqueue(signal);

                _generationStats.Increment();
            }

            // Get current stats to check total generated
            var (totalGenerated, _) = _generationStats.GetSnapshot();

            // Periodically yield to let the consumer catch up and log throughput
            if (totalGenerated % 100000 < 10000)
            {
                await Task.Yield();

                // Log throughput every second
                var elapsed = Stopwatch.GetElapsedTime(lastLogTime);
                if (elapsed.TotalSeconds >= 1.0)
                {
                    var (currentTotal, currentRate) = _generationStats.GetSnapshot();
                    var eventDelta = currentTotal - lastLoggedCount;
                    var rate = eventDelta / elapsed.TotalSeconds;
                    _generationStats.UpdateRate(currentTotal, Stopwatch.GetTimestamp(), rate);
                    _logger.LogInformation("Throughput: {Rate:N0} events/sec, Total: {Total:N0}", rate, currentTotal);
                    lastLogTime = Stopwatch.GetTimestamp();
                    lastLoggedCount = currentTotal;
                }
            }

            // Occasionally decay intensity
            if (_rng.NextDouble() < 0.01)
                _intensity = Math.Max(_intensity * 0.9, 2.0);
        }

        throughputTimer.Stop();
        var (finalTotal, _) = _generationStats.GetSnapshot();
        var totalRate = finalTotal / throughputTimer.Elapsed.TotalSeconds;
        _logger.LogInformation("MarketDataSimulator stopped - Total: {Total:N0}, Avg: {Rate:N0} events/sec", finalTotal, totalRate);
    }
}
