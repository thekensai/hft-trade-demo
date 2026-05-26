using System.Buffers;
using System.Diagnostics;
using System.Runtime;

namespace TradeDemo.Api.Services;

/// <summary>
/// Lock-free, zero-allocation latency tracker using Interlocked operations.
/// Maintains a circular buffer of latency samples for percentile calculation.
/// 
/// Design choices for trading infrastructure:
/// - No locks: uses Interlocked.CompareExchange for thread-safe updates
/// - Pre-allocated circular buffer: no GC pressure during hot path
/// - ArrayPool<T> for temporary sort buffers during percentile computation
/// - Stopwatch-based high-resolution timing
/// </summary>
public sealed class PerformanceMetrics
{
    private readonly long[] _latencySamples;
    private long _sampleIndex;
    private long _totalMessages;
    private long _totalBytes;
    private long _peakLatencyTicks;

    // GC tracking
    private int _lastGen0;
    private int _lastGen1;
    private int _lastGen2;
    private long _lastAllocatedBytes;
    private readonly Stopwatch _uptimeWatch;

    // Throughput window (1-second rolling)
    private long _windowMessageCount;
    private long _windowStartTicks;

    private const int SampleBufferSize = 4096; // power of 2 for fast modulo

    public PerformanceMetrics()
    {
        _latencySamples = new long[SampleBufferSize];
        _uptimeWatch = Stopwatch.StartNew();
        _windowStartTicks = Stopwatch.GetTimestamp();
        _lastAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _lastGen0 = GC.CollectionCount(0);
        _lastGen1 = GC.CollectionCount(1);
        _lastGen2 = GC.CollectionCount(2);
    }

    /// <summary>
    /// Records a processing latency sample. Lock-free using Interlocked.
    /// </summary>
    public void RecordLatency(long elapsedTicks)
    {
        var idx = Interlocked.Increment(ref _sampleIndex) & (SampleBufferSize - 1);
        _latencySamples[idx] = elapsedTicks;
        Interlocked.Increment(ref _totalMessages);
        Interlocked.Increment(ref _windowMessageCount);

        // Track peak (lock-free CAS loop)
        long current;
        do
        {
            current = Interlocked.Read(ref _peakLatencyTicks);
            if (elapsedTicks <= current) break;
        } while (Interlocked.CompareExchange(ref _peakLatencyTicks, elapsedTicks, current) != current);
    }

    /// <summary>
    /// Records bytes processed for allocation tracking.
    /// </summary>
    public void RecordBytes(long bytes)
    {
        Interlocked.Add(ref _totalBytes, bytes);
    }

    /// <summary>
    /// Computes a snapshot of current performance metrics.
    /// Uses ArrayPool for the temporary sort buffer to avoid GC pressure.
    /// </summary>
    public PerformanceSnapshot GetSnapshot()
    {
        var sampleCount = Math.Min(Interlocked.Read(ref _sampleIndex), SampleBufferSize);
        var tickFrequency = (double)Stopwatch.Frequency;

        // Rent a buffer from ArrayPool — zero allocation for percentile computation
        var rentedBuffer = ArrayPool<long>.Shared.Rent((int)sampleCount);
        try
        {
            Array.Copy(_latencySamples, rentedBuffer, (int)sampleCount);
            Array.Sort(rentedBuffer, 0, (int)sampleCount);

            var p50Ticks = sampleCount > 0 ? rentedBuffer[(int)(sampleCount * 0.50)] : 0;
            var p95Ticks = sampleCount > 0 ? rentedBuffer[(int)(sampleCount * 0.95)] : 0;
            var p99Ticks = sampleCount > 0 ? rentedBuffer[(int)(sampleCount * 0.99)] : 0;

            // GC metrics
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            var gcMemory = GC.GetTotalMemory(false);

            // Throughput window
            var now = Stopwatch.GetTimestamp();
            var windowElapsed = (now - Interlocked.Read(ref _windowStartTicks)) / tickFrequency;
            var windowMsgs = Interlocked.Exchange(ref _windowMessageCount, 0);
            Interlocked.Exchange(ref _windowStartTicks, now);
            var throughput = windowElapsed > 0 ? windowMsgs / windowElapsed : 0;

            var allocationRate = allocatedBytes - _lastAllocatedBytes;
            _lastAllocatedBytes = allocatedBytes;

            var snapshot = new PerformanceSnapshot
            {
                P50LatencyUs = TicksToMicroseconds(p50Ticks, tickFrequency),
                P95LatencyUs = TicksToMicroseconds(p95Ticks, tickFrequency),
                P99LatencyUs = TicksToMicroseconds(p99Ticks, tickFrequency),
                PeakLatencyUs = TicksToMicroseconds(Interlocked.Read(ref _peakLatencyTicks), tickFrequency),
                TotalMessages = Interlocked.Read(ref _totalMessages),
                TotalBytes = Interlocked.Read(ref _totalBytes),
                MessagesPerSecond = Math.Round(throughput, 1),
                Gen0Collections = gen0,
                Gen1Collections = gen1,
                Gen2Collections = gen2,
                Gen0Delta = gen0 - _lastGen0,
                Gen1Delta = gen1 - _lastGen1,
                Gen2Delta = gen2 - _lastGen2,
                HeapSizeBytes = gcMemory,
                AllocationRateBytes = allocationRate,
                UptimeSeconds = _uptimeWatch.Elapsed.TotalSeconds,
                GcPauseTimeMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                IsServerGc = GCSettings.IsServerGC,
                LatencyMode = GCSettings.LatencyMode.ToString(),
                SampleCount = (int)sampleCount
            };

            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;

            return snapshot;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rentedBuffer);
        }
    }

    private static double TicksToMicroseconds(long ticks, double frequency) =>
        Math.Round(ticks / frequency * 1_000_000, 2);
}

public class PerformanceSnapshot
{
    // Latency percentiles (microseconds)
    public double P50LatencyUs { get; init; }
    public double P95LatencyUs { get; init; }
    public double P99LatencyUs { get; init; }
    public double PeakLatencyUs { get; init; }

    // Throughput
    public long TotalMessages { get; init; }
    public long TotalBytes { get; init; }
    public double MessagesPerSecond { get; init; }

    // GC metrics
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public int Gen0Delta { get; init; }
    public int Gen1Delta { get; init; }
    public int Gen2Delta { get; init; }
    public long HeapSizeBytes { get; init; }
    public long AllocationRateBytes { get; init; }
    public double GcPauseTimeMs { get; init; }
    public bool IsServerGc { get; init; }
    public string LatencyMode { get; init; } = "";

    // Meta
    public double UptimeSeconds { get; init; }
    public int SampleCount { get; init; }
}
