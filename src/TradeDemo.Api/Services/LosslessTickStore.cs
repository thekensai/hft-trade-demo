using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TradeDemo.Api.Journal;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

/// <summary>
/// In-process authoritative tick log for replay-oriented consumers.
///
/// This deliberately sits beside the SignalR dashboard pipeline. The dashboard may
/// coalesce millions of raw ticks into human-friendly snapshots, while this store
/// keeps every accepted tick in sequence order for recent deterministic replay and
/// gap accounting. In production this role would be backed by Event Hubs/Kafka and
/// durable storage; this class is the local demo equivalent.
/// </summary>
public sealed class LosslessTickStore : BackgroundService, IMarketDataSubscriber
{
    private const int ChannelCapacity = 1_000_000;
    private const int RecentTickCapacity = 500_000;

    private readonly Channel<TradeSignal> _channel;
    private readonly ILogger<LosslessTickStore> _logger;
    private readonly ITickJournalWriter _journalWriter;
    private readonly TickJournalOptions _journalOptions;
    private readonly object _sync = new();
    private readonly TradeSignal[] _recentTicks = new TradeSignal[RecentTickCapacity];

    private int _nextWriteIndex;
    private int _recentCount;
    private long _acceptedCount;
    private long _droppedCount;
    private long _gapCount;
    private long _lastSequenceId;

    public LosslessTickStore(ILogger<LosslessTickStore> logger, ITickJournalWriter journalWriter, IOptions<TickJournalOptions> journalOptions)
    {
        _logger = logger;
        _journalWriter = journalWriter;
        _journalOptions = journalOptions.Value;
        _channel = Channel.CreateBounded<TradeSignal>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void OnMarketData(TradeSignal signal) => TryAppend(signal);

    public bool TryAppend(TradeSignal signal)
    {
        if (_channel.Writer.TryWrite(signal))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public async ValueTask AppendAsync(TradeSignal signal, CancellationToken ct = default)
    {
        await _channel.Writer.WriteAsync(signal, ct);
    }

    public LosslessTickStoreStats GetStats() => new(
        AcceptedTotal: Interlocked.Read(ref _acceptedCount),
        DroppedTotal: Interlocked.Read(ref _droppedCount),
        GapTotal: Interlocked.Read(ref _gapCount),
        LastSequenceId: Interlocked.Read(ref _lastSequenceId),
        QueueDepth: _channel.Reader.Count,
        RecentTickCount: Volatile.Read(ref _recentCount),
        RecentTickCapacity: RecentTickCapacity);

    public IReadOnlyList<TradeSignal> GetRecentTicks(int maxCount)
    {
        maxCount = Math.Clamp(maxCount, 0, RecentTickCapacity);

        lock (_sync)
        {
            var count = Math.Min(maxCount, _recentCount);
            var result = new TradeSignal[count];
            var start = (_nextWriteIndex - count + RecentTickCapacity) % RecentTickCapacity;

            for (var i = 0; i < count; i++)
            {
                result[i] = _recentTicks[(start + i) % RecentTickCapacity];
            }

            return result;
        }
    }

    public IReadOnlyList<TradeSignal> GetTicksFromSequence(long fromSequenceId, int maxCount)
    {
        maxCount = Math.Clamp(maxCount, 0, RecentTickCapacity);

        lock (_sync)
        {
            var result = new List<TradeSignal>(Math.Min(maxCount, _recentCount));
            var start = (_nextWriteIndex - _recentCount + RecentTickCapacity) % RecentTickCapacity;

            for (var i = 0; i < _recentCount && result.Count < maxCount; i++)
            {
                var signal = _recentTicks[(start + i) % RecentTickCapacity];
                if (signal.SequenceId >= fromSequenceId)
                {
                    result.Add(signal);
                }
            }

            return result;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "LosslessTickStore started — channel capacity={ChannelCapacity:N0}, recent tick window={RecentTickCapacity:N0}",
            ChannelCapacity,
            RecentTickCapacity);

        var journalBatch = new List<TradeSignal>(Math.Max(1, _journalOptions.BatchSize));
        var lastFlushAt = DateTime.UtcNow;
        var flushInterval = TimeSpan.FromMilliseconds(Math.Max(1, _journalOptions.FlushIntervalMilliseconds));

        try
        {
            await foreach (var signal in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                AppendToRecentLog(signal);

                if (_journalOptions.Enabled)
                {
                    journalBatch.Add(signal);
                    var shouldFlush = journalBatch.Count >= _journalOptions.BatchSize || DateTime.UtcNow - lastFlushAt >= flushInterval;
                    if (shouldFlush)
                    {
                        await FlushJournalBatchAsync(journalBatch, stoppingToken);
                        lastFlushAt = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        finally
        {
            if (journalBatch.Count > 0)
            {
                await FlushJournalBatchAsync(journalBatch, CancellationToken.None);
            }
            await _journalWriter.FlushAsync(CancellationToken.None);
        }
    }

    private async Task FlushJournalBatchAsync(List<TradeSignal> journalBatch, CancellationToken ct)
    {
        if (journalBatch.Count == 0)
        {
            return;
        }

        try
        {
            await _journalWriter.AppendBatchAsync(journalBatch, ct);
            journalBatch.Clear();
        }
        catch (Exception ex) when (!_journalOptions.FailFast)
        {
            _logger.LogError(ex, "Tick journal write failed for {Count:N0} signals; continuing because FailFast=false.", journalBatch.Count);
            journalBatch.Clear();
        }
    }

    private void AppendToRecentLog(TradeSignal signal)
    {
        lock (_sync)
        {
            var previous = _lastSequenceId;
            if (previous > 0 && signal.SequenceId > previous + 1)
            {
                Interlocked.Add(ref _gapCount, signal.SequenceId - previous - 1);
            }

            _lastSequenceId = Math.Max(previous, signal.SequenceId);
            _recentTicks[_nextWriteIndex] = signal;
            _nextWriteIndex = (_nextWriteIndex + 1) % RecentTickCapacity;
            if (_recentCount < RecentTickCapacity)
            {
                _recentCount++;
            }
        }

        Interlocked.Increment(ref _acceptedCount);
    }
}

public sealed record LosslessTickStoreStats(
    long AcceptedTotal,
    long DroppedTotal,
    long GapTotal,
    long LastSequenceId,
    int QueueDepth,
    int RecentTickCount,
    int RecentTickCapacity);
