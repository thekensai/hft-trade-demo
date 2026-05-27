using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using TradeDemo.Api.Hubs;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

/// <summary>
/// Simulates high-throughput market data sources (exchanges) pushing events into a queue.
/// </summary>
public class MarketDataSimulator : BackgroundService
{
    private readonly ILogger<MarketDataSimulator> _logger;
    private readonly TradeQueueProcessor _queueProcessor;
    private static readonly Random _rng = new();
    private long _sequenceId = 0;

    private static readonly (string Symbol, decimal BasePrice, string Exchange)[] Instruments =
    [
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

    // Event clustering: Hawkes process-like state
    private double _intensity = 1.0;
    private int _sameSideStreak = 0;
    private string? _lastDirection;

    // Box-Muller transform for Gaussian random numbers
    private double NextGaussian()
    {
        // Use polar form of Box-Muller transform
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return randStdNormal;
    }

    public MarketDataSimulator(ILogger<MarketDataSimulator> logger, TradeQueueProcessor queueProcessor)
    {
        _logger = logger;
        _queueProcessor = queueProcessor;
        _currentPrices = Instruments.Select(i => i.BasePrice).ToArray();
    }

    // Event clustering: Hawkes-like delay calculation with bursty periods
    private int GetEventClusterDelay()
    {
        // Probability of being in burst mode based on current intensity
        var isInBurst = _rng.NextDouble() < Math.Min(_intensity / 5.0, 0.3);

        if (isInBurst)
        {
            // Burst mode: very short delays (0-5ms), higher intensity
            _intensity = Math.Min(_intensity * 1.5, 10.0);
            return _rng.Next(0, 5);
        }

        // Quiet mode: longer delays, intensity decays
        _intensity = Math.Max(_intensity * 0.9, 0.5);

        // Occasional random bursts (news events, volatility spikes)
        if (_rng.NextDouble() < 0.05)
        {
            _intensity = 8.0; // Sudden spike in activity
            return _rng.Next(1, 10);
        }

        // Normal operation: exponential-like distribution
        // Most delays are short, occasional long pauses
        var delay = (int)Math.Round(-Math.Log(_rng.NextDouble()) * 50);
        return Math.Clamp(delay, 5, 300);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataSimulator started - generating {Count} instruments", Instruments.Length);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Hawkes-like burst sizing: intensity affects event count
            var burstSize = Math.Max(1, (int)(NextGaussian() * 3 + 5 * _intensity));
            burstSize = Math.Clamp(burstSize, 1, 30);

            for (int i = 0; i < burstSize; i++)
            {
                var idx = _rng.Next(Instruments.Length);
                var (symbol, _, exchange) = Instruments[idx];

                // Random walk price
                var delta = (decimal)(_rng.NextDouble() - 0.498) * _currentPrices[idx] * 0.002m;
                _currentPrices[idx] = Math.Max(0.01m, _currentPrices[idx] + delta);
                var price = Math.Round(_currentPrices[idx], 2);

                // Direction with autocorrelation: same-side streaks during momentum
                string direction;
                if (_lastDirection != null && _sameSideStreak > 0 && _rng.NextDouble() < 0.7)
                {
                    direction = _lastDirection;
                    _sameSideStreak++;
                }
                else
                {
                    direction = _rng.NextDouble() > 0.5 ? "BUY" : "SELL";
                    _lastDirection = direction;
                    _sameSideStreak = _rng.Next(1, 5); // Start a new streak
                }

                // Spread: bid/ask prices with realistic spread (typically 0.01-0.05% of price)
                var spread = _currentPrices[idx] * (decimal)(_rng.NextDouble() * 0.0002 + 0.0001);
                var bidPrice = Math.Round(price - spread / 2, 2);
                var askPrice = Math.Round(price + spread / 2, 2);
                var midPrice = Math.Round((bidPrice + askPrice) / 2, 2);

                // Trades execute at bid or ask depending on direction
                var tradePrice = direction == "BUY" ? askPrice : bidPrice;
                var change = Math.Round(tradePrice - Instruments[idx].BasePrice, 2);
                var changePct = Math.Round((double)(change / Instruments[idx].BasePrice * 100), 3);

                var signal = new TradeSignal(
                    Symbol: symbol,
                    BidPrice: bidPrice,
                    AskPrice: askPrice,
                    MidPrice: midPrice,
                    Change: change,
                    ChangePercent: changePct,
                    Volume: _rng.NextInt64(100, 50000),
                    Exchange: exchange,
                    Timestamp: DateTime.UtcNow,
                    Direction: direction,
                    SequenceId: Interlocked.Increment(ref _sequenceId)
                );

                await _queueProcessor.EnqueueAsync(signal, stoppingToken);
            }

            // Event clustering: Hawkes-like timing with bursts and pauses
            var delay = GetEventClusterDelay();
            await Task.Delay(delay, stoppingToken);
        }
    }
}
