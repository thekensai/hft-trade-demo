using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using TradeDemo.Api.Hubs;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

/// <summary>
/// High-performance queue processor demonstrating trading infrastructure patterns:
///
/// - Bounded Channel&lt;T&gt; (100K capacity) with DropOldest backpressure — mirrors
///   Service Bus queue semantics with competing consumers
/// - Latest-state aggregation per symbol — the consumer drains the channel as fast
///   as possible into a small per-symbol cache, then broadcasts a coalesced snapshot
///   on a fixed cadence (~100ms). This matches how every real market-data UI scales:
///   1.3M raw ticks/sec collapse to a few hundred symbols × 10 snapshots/sec, instead
///   of trying to push every individual tick over the wire.
/// - Interlocked counters for lock-free stats — no contention on hot path
/// - Stopwatch-based latency measurement per broadcast — feeds percentile tracker
/// - Accurate dropped-count via enqueue/processed/depth accounting, since
///   DropOldest silently makes room (TryWrite always returns true)
///
/// Threading model:
///   Producer threads → Channel.Writer.TryWrite (lock-free)
///   Consumer thread  → drain-then-snapshot loop on a fixed cadence
///   Stats broadcast  → periodic, piggybacks on consumer loop
/// </summary>
public class TradeQueueProcessor : BackgroundService
{
    private const int ChannelCapacity = 100_000;
    private const int BroadcastIntervalMs = 33;         // ~30 snapshots/sec (monitor refresh rate)
    private const int StatsIntervalMs = 500;            // ~2 stats updates/sec

    private readonly Channel<TradeSignal> _channel;
    private readonly IHubContext<TradeHub> _hubContext;
    private readonly ILogger<TradeQueueProcessor> _logger;
    private readonly PerformanceMetrics _metrics;
    private readonly GenerationStats? _generationStats;

    // Lock-free counters on the hot path
    private long _enqueuedCount;     // total TryWrite attempts (incl. silently-dropped-by-DropOldest)
    private long _processedCount;    // total raw signals drained from the channel
    private long _broadcastCount;    // total snapshot broadcasts sent
    private long _broadcastItemCount; // total unique symbols sent across all broadcasts
    private long _coalescedCount;    // total signals coalesced (overwritten in cache)
    private long _processedPerSecX100;
    private long _snapshotsPerSecX100;
    private long _coalescedPerSecX100;

    public long ProcessedCount => Interlocked.Read(ref _processedCount);
    public long BroadcastCount => Interlocked.Read(ref _broadcastCount);
    public double ProcessedPerSec => Interlocked.Read(ref _processedPerSecX100) / 100.0;
    public double SnapshotsPerSec => Interlocked.Read(ref _snapshotsPerSecX100) / 100.0;
    public double CoalescedPerSec => Interlocked.Read(ref _coalescedPerSecX100) / 100.0;
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>
    /// Accurate dropped count, inferred from accounting:
    /// dropped = enqueued − processed − currently-in-queue.
    /// Channel.CreateBounded(DropOldest) silently discards items, so TryWrite
    /// always returns true and can't be used to count drops directly.
    /// </summary>
    public long DroppedCount
    {
        get
        {
            var enqueued = Interlocked.Read(ref _enqueuedCount);
            var processed = Interlocked.Read(ref _processedCount);
            var depth = _channel.Reader.Count;
            return Math.Max(0, enqueued - processed - depth);
        }
    }

    // Expose the channel writer for high-performance producers in the same assembly
    // (keeps the channel itself private while allowing TryWrite without reflection)
    internal ChannelWriter<TradeSignal> Writer => _channel.Writer;

    /// <summary>
    /// Fast enqueue path for high-throughput producers. Call this instead of Writer.TryWrite()
    /// to ensure enqueue metrics are tracked correctly.
    /// </summary>
    public bool TryEnqueue(TradeSignal signal)
    {
        Interlocked.Increment(ref _enqueuedCount);
        return _channel.Writer.TryWrite(signal);
    }

    public TradeQueueProcessor(IHubContext<TradeHub> hubContext, ILogger<TradeQueueProcessor> logger, PerformanceMetrics metrics, GenerationStats? generationStats = null)
    {
        _hubContext = hubContext;
        _logger = logger;
        _metrics = metrics;
        _generationStats = generationStats;
        // Large channel for 1M+ events/sec — DropOldest preserves the freshest data
        // under sustained backpressure (stale quotes are worthless for trading UIs).
        // SingleWriter=false allows multiple producer threads (market feeds).
        _channel = Channel.CreateBounded<TradeSignal>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,   // single consumer drain loop
            SingleWriter = false   // multiple producers (exchange feeds)
        });
    }

    public ValueTask EnqueueAsync(TradeSignal signal, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _enqueuedCount);
        // TryWrite returns true even when DropOldest silently makes room — drops
        // are accounted for via DroppedCount, not via this return value.
        _channel.Writer.TryWrite(signal);
        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TradeQueueProcessor started — channel capacity={Capacity}, broadcast cadence={IntervalMs}ms (latest-per-symbol coalescing)",
            ChannelCapacity, BroadcastIntervalMs);

        // Latest-state cache: one entry per symbol, last writer wins.
        // Cleared after each broadcast so each snapshot contains only changed symbols.
        var latestBySymbol = new Dictionary<string, TradeSignal>(capacity: 64);

        // Pre-allocated buffer for the snapshot payload, sized to a typical symbol universe.
        // SignalR will serialize this directly — no per-batch allocation here.
        var snapshot = new List<TradeSignal>(capacity: 64);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(BroadcastIntervalMs));

        var lastStatsAt = Stopwatch.GetTimestamp();
        var lastProcessedTotal = 0L;
        var statsIntervalTicks = Stopwatch.Frequency * StatsIntervalMs / 1000;
        var broadcastsSinceLastStats = 0L;
        var coalescedSinceLastStats = 0L;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var tickStart = Stopwatch.GetTimestamp();

                // ── Drain phase: pull everything currently available, coalesce by symbol ──
                var drained = 0;
                while (_channel.Reader.TryRead(out var signal))
                {
                    latestBySymbol[signal.Symbol] = signal;
                    drained++;
                }

                if (drained > 0)
                {
                    Interlocked.Add(ref _processedCount, drained);
                }

                // ── Broadcast phase: send one coalesced snapshot per tick ──
                if (latestBySymbol.Count > 0)
                {
                    snapshot.Clear();
                    foreach (var entry in latestBySymbol.Values)
                    {
                        snapshot.Add(entry);
                    }
                    latestBySymbol.Clear();

                    Interlocked.Add(ref _broadcastItemCount, snapshot.Count);
                    Interlocked.Add(ref _coalescedCount, drained - snapshot.Count);
                    coalescedSinceLastStats += drained - snapshot.Count;

                    await _hubContext.Clients.All.SendAsync("TradeSignals", snapshot, stoppingToken);
                    Interlocked.Increment(ref _broadcastCount);
                    broadcastsSinceLastStats++;

                    // Latency = time from tick-start to send-complete; bytes ≈ snapshot count × ~128B
                    _metrics.RecordLatency(Stopwatch.GetTimestamp() - tickStart);
                    _metrics.RecordBytes(snapshot.Count * 128);
                }

                // ── Stats broadcast: piggybacks on the consumer loop ──
                if (Stopwatch.GetTimestamp() - lastStatsAt >= statsIntervalTicks)
                {
                    var now = Stopwatch.GetTimestamp();
                    var elapsedSeconds = (double)(now - lastStatsAt) / Stopwatch.Frequency;
                    var snapshotsPerSec = elapsedSeconds > 0
                        ? broadcastsSinceLastStats / elapsedSeconds
                        : 0;

                    lastStatsAt = now;
                    broadcastsSinceLastStats = 0;

                    long totalGenerated = 0;
                    double generationRate = 0;
                    if (_generationStats != null)
                    {
                        (totalGenerated, generationRate) = _generationStats.GetSnapshot();
                    }

                    var processedTotal = Interlocked.Read(ref _processedCount);
                    var processedPerSec = elapsedSeconds > 0
                        ? (processedTotal - lastProcessedTotal) / elapsedSeconds
                        : 0;
                    var coalescedTotal = Interlocked.Read(ref _coalescedCount);
                    var coalescedPerSec = elapsedSeconds > 0
                        ? coalescedSinceLastStats / elapsedSeconds
                        : 0;
                    Interlocked.Exchange(ref _processedPerSecX100, (long)(processedPerSec * 100));
                    Interlocked.Exchange(ref _snapshotsPerSecX100, (long)(snapshotsPerSec * 100));
                    Interlocked.Exchange(ref _coalescedPerSecX100, (long)(coalescedPerSec * 100));
                    lastProcessedTotal = processedTotal;

                    await _hubContext.Clients.All.SendAsync("Stats", new
                    {
                        ProcessedTotal = processedTotal,
                        DroppedTotal = DroppedCount,
                        CoalescedTotal = coalescedTotal,
                        CoalescedPerSec = coalescedPerSec,
                        QueueDepth = _channel.Reader.Count,
                        BroadcastTotal = Interlocked.Read(ref _broadcastCount),
                        SnapshotsPerSec = snapshotsPerSec,
                        ServerGeneratedTotal = totalGenerated,
                        ServerGenerationRatePerSec = generationRate,
                        Timestamp = DateTime.UtcNow
                    }, stoppingToken);

                    // Reset per-stats-window counters
                    broadcastsSinceLastStats = 0;
                    coalescedSinceLastStats = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown — PeriodicTimer.WaitForNextTickAsync throws on cancellation.
        }
    }
}
