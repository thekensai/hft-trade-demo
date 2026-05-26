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

    public MarketDataSimulator(ILogger<MarketDataSimulator> logger, TradeQueueProcessor queueProcessor)
    {
        _logger = logger;
        _queueProcessor = queueProcessor;
        _currentPrices = Instruments.Select(i => i.BasePrice).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataSimulator started - generating {Count} instruments", Instruments.Length);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Generate a burst of 5-20 events per tick to simulate high throughput
            var burstSize = _rng.Next(5, 21);

            for (int i = 0; i < burstSize; i++)
            {
                var idx = _rng.Next(Instruments.Length);
                var (symbol, _, exchange) = Instruments[idx];

                // Random walk price
                var delta = (decimal)(_rng.NextDouble() - 0.498) * _currentPrices[idx] * 0.002m;
                _currentPrices[idx] = Math.Max(0.01m, _currentPrices[idx] + delta);
                var price = Math.Round(_currentPrices[idx], 2);
                var change = Math.Round(price - Instruments[idx].BasePrice, 2);
                var changePct = Math.Round((double)(change / Instruments[idx].BasePrice * 100), 3);

                var signal = new TradeSignal(
                    Symbol: symbol,
                    Price: price,
                    Change: change,
                    ChangePercent: changePct,
                    Volume: _rng.NextInt64(100, 50000),
                    Exchange: exchange,
                    Timestamp: DateTime.UtcNow,
                    Direction: _rng.NextDouble() > 0.5 ? "BUY" : "SELL"
                );

                await _queueProcessor.EnqueueAsync(signal, stoppingToken);
            }

            // Tick interval: 20-80ms to simulate realistic market feed rates
            await Task.Delay(_rng.Next(20, 80), stoppingToken);
        }
    }
}
