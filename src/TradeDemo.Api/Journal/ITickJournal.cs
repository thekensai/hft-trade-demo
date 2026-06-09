using TradeDemo.Api.Models;

namespace TradeDemo.Api.Journal;

public interface ITickJournalWriter : IAsyncDisposable
{
    ValueTask AppendAsync(TradeSignal signal, CancellationToken ct = default);
    ValueTask AppendBatchAsync(IReadOnlyList<TradeSignal> signals, CancellationToken ct = default);
    ValueTask FlushAsync(CancellationToken ct = default);
}

public interface ITickJournalReader
{
    IAsyncEnumerable<TradeSignal> ReadAllAsync(CancellationToken ct = default);
    IAsyncEnumerable<TradeSignal> ReadFromSequenceAsync(long fromSequenceId, int? maxCount = null, CancellationToken ct = default);
    IAsyncEnumerable<TradeSignal> ReadRangeAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default);
}

public interface ITickJournal : ITickJournalWriter, ITickJournalReader
{
    string ProviderName { get; }
}
