using System.Collections.Concurrent;
using TradeDemo.Wpf.Models;

namespace TradeDemo.Wpf.Services;

public sealed class TerminalFeedProcessor
{
    private readonly ConcurrentQueue<TradeSignalDto> _pendingSignals = new();

    public bool IsEmpty => _pendingSignals.IsEmpty;

    public void Enqueue(TradeSignalDto signal) => _pendingSignals.Enqueue(signal);

    public void EnqueueRange(IEnumerable<TradeSignalDto> signals)
    {
        foreach (var signal in signals)
        {
            _pendingSignals.Enqueue(signal);
        }
    }

    public void Clear()
    {
        while (_pendingSignals.TryDequeue(out _))
        {
        }
    }

    public FeedDrainResult Drain(int maxFeedRowsPerFrame)
    {
        var latestBySymbol = new Dictionary<string, TradeSignalDto>(StringComparer.OrdinalIgnoreCase);
        var feedWindow = new Queue<TradeSignalDto>(maxFeedRowsPerFrame);
        var dequeued = 0;

        while (_pendingSignals.TryDequeue(out var signal))
        {
            dequeued++;
            latestBySymbol[signal.Symbol] = signal;

            if (feedWindow.Count >= maxFeedRowsPerFrame)
            {
                feedWindow.Dequeue();
            }

            feedWindow.Enqueue(signal);
        }

        return new FeedDrainResult(dequeued, latestBySymbol.Values.ToArray(), feedWindow.Reverse().ToArray());
    }
}

public sealed record FeedDrainResult(int DequeuedCount, IReadOnlyList<TradeSignalDto> LatestBySymbol, IReadOnlyList<TradeSignalDto> FeedSignals);
