using System.Runtime.CompilerServices;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Journal;

public sealed class NullTickJournal : ITickJournal
{
    public static readonly NullTickJournal Instance = new();

    private NullTickJournal() { }

    public string ProviderName => "None";

    public ValueTask AppendAsync(TradeSignal signal, CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask AppendBatchAsync(IReadOnlyList<TradeSignal> signals, CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async IAsyncEnumerable<TradeSignal> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<TradeSignal> ReadFromSequenceAsync(long fromSequenceId, int? maxCount = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<TradeSignal> ReadRangeAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
