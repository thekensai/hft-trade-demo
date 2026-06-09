namespace TradeDemo.Api.Services;

/// <summary>
/// Allocates process-wide monotonic tick sequence numbers.
///
/// A single sequencer lets the lossless/replay path detect gaps across every
/// producer instead of each producer maintaining its own overlapping counter.
/// </summary>
public sealed class TickSequencer
{
    private long _sequenceId;

    public long Next() => Interlocked.Increment(ref _sequenceId);

    public long Current => Interlocked.Read(ref _sequenceId);
}
