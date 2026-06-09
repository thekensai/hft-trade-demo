using System.Runtime.CompilerServices;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Journal;

public sealed class AzureBlobTickJournal : ITickJournal
{
    private readonly AzureBlobTickJournalOptions _options;
    private readonly ILogger<AzureBlobTickJournal> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly MemoryStream _segment = new();
    private BlobContainerClient? _container;
    private string? _currentBlobName;
    private long _currentBytes;
    private bool _disposed;

    public AzureBlobTickJournal(IOptions<TickJournalOptions> options, ILogger<AzureBlobTickJournal> logger)
    {
        _options = options.Value.Blob;
        _logger = logger;
    }

    public string ProviderName => "Blob";

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

        await _writeLock.WaitAsync(ct);
        try
        {
            EnsureWritableSegment(signals[0].SequenceId);
            foreach (var signal in signals)
            {
                var record = TickJournalBinaryCodec.EncodeRecord(signal);
                if (_currentBytes > TickJournalBinaryCodec.SegmentHeaderLength && _currentBytes + record.Length > _options.MaxSegmentBytes)
                {
                    await UploadAndResetSegmentAsync(signal.SequenceId, ct);
                }

                await _segment.WriteAsync(record, ct);
                _currentBytes += record.Length;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_currentBytes > TickJournalBinaryCodec.SegmentHeaderLength)
            {
                await UploadCurrentSegmentAsync(ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<TradeSignal> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        await foreach (var item in container.GetBlobsAsync(prefix: NormalizePrefix(_options.Prefix), cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".tjseg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await foreach (var signal in ReadBlobAsync(container, item.Name, ct))
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
        var fromTicks = fromUtc.UtcDateTime.Ticks;
        var toTicks = toUtc.UtcDateTime.Ticks;
        await foreach (var signal in ReadAllAsync(ct))
        {
            var ticks = signal.Timestamp.Kind == DateTimeKind.Utc ? signal.Timestamp.Ticks : signal.Timestamp.ToUniversalTime().Ticks;
            if (ticks >= fromTicks && ticks <= toTicks)
            {
                yield return signal;
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
        await FlushAsync();
        _segment.Dispose();
        _writeLock.Dispose();
    }

    private void EnsureWritableSegment(long firstSequenceId)
    {
        if (_currentBytes > 0)
        {
            return;
        }

        _segment.SetLength(0);
        var now = DateTime.UtcNow;
        _currentBlobName = BuildBlobName(now, firstSequenceId);
        TickJournalBinaryCodec.WriteSegmentHeader(_segment, now.Ticks, firstSequenceId);
        _currentBytes = TickJournalBinaryCodec.SegmentHeaderLength;
    }

    private async Task UploadAndResetSegmentAsync(long nextFirstSequenceId, CancellationToken ct)
    {
        await UploadCurrentSegmentAsync(ct);
        EnsureWritableSegment(nextFirstSequenceId);
    }

    private async Task UploadCurrentSegmentAsync(CancellationToken ct)
    {
        if (_currentBlobName is null || _currentBytes <= TickJournalBinaryCodec.SegmentHeaderLength)
        {
            return;
        }

        var container = await GetContainerAsync(ct);
        _segment.Position = 0;
        await container.GetBlobClient(_currentBlobName).UploadAsync(_segment, overwrite: true, cancellationToken: ct);
        _logger.LogInformation("Uploaded tick journal segment blob {BlobName} ({Bytes:N0} bytes)", _currentBlobName, _currentBytes);
        _segment.SetLength(0);
        _segment.Position = 0;
        _currentBytes = 0;
        _currentBlobName = null;
    }

    private async Task<BlobContainerClient> GetContainerAsync(CancellationToken ct)
    {
        if (_container is not null)
        {
            return _container;
        }

        BlobContainerClient container;
        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            container = new BlobContainerClient(_options.ConnectionString, _options.ContainerName);
        }
        else if (!string.IsNullOrWhiteSpace(_options.AccountUri))
        {
            var service = new BlobServiceClient(new Uri(_options.AccountUri), new DefaultAzureCredential());
            container = service.GetBlobContainerClient(_options.ContainerName);
        }
        else
        {
            throw new InvalidOperationException("Blob journal requires Blob.ConnectionString or Blob.AccountUri.");
        }

        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        _container = container;
        return container;
    }

    private static string NormalizePrefix(string prefix) => string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/');

    private string BuildBlobName(DateTime utcNow, long firstSequenceId)
    {
        var prefix = NormalizePrefix(_options.Prefix);
        var path = $"year={utcNow:yyyy}/month={utcNow:MM}/day={utcNow:dd}/hour={utcNow:HH}/ticks-{utcNow:yyyyMMddTHHmmssfffZ}-{firstSequenceId:D18}.tjseg";
        return string.IsNullOrEmpty(prefix) ? path : $"{prefix}/{path}";
    }

    private static async IAsyncEnumerable<TradeSignal> ReadBlobAsync(BlobContainerClient container, string blobName, [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await container.GetBlobClient(blobName).DownloadStreamingAsync(cancellationToken: ct);
        await using var stream = response.Value.Content;
        if (!TickJournalBinaryCodec.TryReadSegmentHeader(stream, out _))
        {
            yield break;
        }

        while (!ct.IsCancellationRequested && TickJournalBinaryCodec.TryReadRecord(stream, out var signal))
        {
            yield return signal;
        }
    }
}
