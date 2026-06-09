using System.Runtime.CompilerServices;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Options;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Journal;

public sealed class EventHubsTickJournal : ITickJournal
{
    private readonly EventHubsTickJournalOptions _options;
    private readonly ILogger<EventHubsTickJournal> _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private EventHubProducerClient? _producer;
    private EventHubConsumerClient? _consumer;
    private bool _disposed;

    public EventHubsTickJournal(IOptions<TickJournalOptions> options, ILogger<EventHubsTickJournal> logger)
    {
        _options = options.Value.EventHubs;
        _logger = logger;
    }

    public string ProviderName => "EventHubs";

    public async ValueTask AppendAsync(TradeSignal signal, CancellationToken ct = default)
    {
        await AppendBatchAsync([signal], ct);
    }

    public async ValueTask AppendBatchAsync(IReadOnlyList<TradeSignal> signals, CancellationToken ct = default)
    {
        if (signals.Count == 0)
        {
            return;
        }

        var producer = GetProducer();
        await _sendLock.WaitAsync(ct);
        try
        {
            var batch = await producer.CreateBatchAsync(new CreateBatchOptions(), ct);
            foreach (var signal in signals)
            {
                var eventData = CreateEventData(signal);
                if (!batch.TryAdd(eventData))
                {
                    await producer.SendAsync(batch, ct);
                    batch.Dispose();
                    batch = await producer.CreateBatchAsync(new CreateBatchOptions(), ct);
                    if (!batch.TryAdd(eventData))
                    {
                        throw new InvalidOperationException($"Tick journal Event Hubs event is too large for sequence {signal.SequenceId}.");
                    }
                }
            }

            if (batch.Count > 0)
            {
                await producer.SendAsync(batch, ct);
            }
            batch.Dispose();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<TradeSignal> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var consumer = GetConsumer();
        await foreach (var partitionEvent in consumer.ReadEventsAsync(ct))
        {
            if (TryDecode(partitionEvent.Data, out var signal))
            {
                yield return signal;
            }
        }
    }

    public async IAsyncEnumerable<TradeSignal> ReadFromSequenceAsync(long fromSequenceId, int? maxCount = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var emitted = 0;
        await foreach (var signal in ReadAllAsync(ct))
        {
            if (signal.SequenceId < fromSequenceId)
            {
                continue;
            }

            yield return signal;
            emitted++;
            if (maxCount is not null && emitted >= maxCount.Value)
            {
                yield break;
            }
        }
    }

    public async IAsyncEnumerable<TradeSignal> ReadRangeAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var consumer = GetConsumer();
        var partitionIds = await consumer.GetPartitionIdsAsync(ct);
        foreach (var partitionId in partitionIds)
        {
            await foreach (var partitionEvent in consumer.ReadEventsFromPartitionAsync(
                partitionId,
                EventPosition.FromEnqueuedTime(fromUtc),
                cancellationToken: ct))
            {
                if (partitionEvent.Data.EnqueuedTime > toUtc)
                {
                    break;
                }

                if (TryDecode(partitionEvent.Data, out var signal))
                {
                    yield return signal;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_producer is not null)
        {
            await _producer.DisposeAsync();
        }
        if (_consumer is not null)
        {
            await _consumer.DisposeAsync();
        }
        _sendLock.Dispose();
    }

    private EventHubProducerClient GetProducer()
    {
        if (_producer is not null)
        {
            return _producer;
        }

        _producer = !string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? new EventHubProducerClient(_options.ConnectionString, _options.EventHubName)
            : new EventHubProducerClient(_options.FullyQualifiedNamespace, _options.EventHubName, new DefaultAzureCredential());
        return _producer;
    }

    private EventHubConsumerClient GetConsumer()
    {
        if (_consumer is not null)
        {
            return _consumer;
        }

        _consumer = !string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? new EventHubConsumerClient(_options.ConsumerGroup, _options.ConnectionString, _options.EventHubName)
            : new EventHubConsumerClient(_options.ConsumerGroup, _options.FullyQualifiedNamespace, _options.EventHubName, new DefaultAzureCredential());
        return _consumer;
    }

    private EventData CreateEventData(TradeSignal signal)
    {
        var data = new EventData(TickJournalBinaryCodec.EncodePayload(signal));
        data.Properties["format"] = "TDJ1";
        data.Properties["schemaVersion"] = TickJournalBinaryCodec.SegmentVersion;
        data.Properties["sequenceId"] = signal.SequenceId;
        data.Properties["symbol"] = signal.Symbol;
        data.Properties["timestampUtcTicks"] = signal.Timestamp.Kind == DateTimeKind.Utc ? signal.Timestamp.Ticks : signal.Timestamp.ToUniversalTime().Ticks;
        return data;
    }

    private bool TryDecode(EventData data, out TradeSignal signal)
    {
        try
        {
            signal = TickJournalBinaryCodec.DecodePayload(data.EventBody.ToMemory().Span);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping invalid Event Hubs tick journal event.");
            signal = default;
            return false;
        }
    }
}
