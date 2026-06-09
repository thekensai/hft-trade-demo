using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Journal;

public sealed class LocalSegmentTickJournal : ITickJournal
{
    private readonly LocalTickJournalOptions _options;
    private readonly ILogger<LocalSegmentTickJournal> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private FileStream? _currentStream;
    private string? _currentPath;
    private long _currentBytes;
    private bool _disposed;

    public LocalSegmentTickJournal(IOptions<TickJournalOptions> options, ILogger<LocalSegmentTickJournal> logger)
    {
        _options = options.Value.Local;
        _logger = logger;
        Directory.CreateDirectory(_options.DirectoryPath);
    }

    public string ProviderName => "Local";

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
                    await RotateAsync(signal.SequenceId, ct);
                }

                await _currentStream!.WriteAsync(record, ct);
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
            if (_currentStream is null)
            {
                return;
            }

            await _currentStream.FlushAsync(ct);
            if (_options.FsyncOnFlush)
            {
                _currentStream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<TradeSignal> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var path in EnumerateSegments())
        {
            await foreach (var signal in ReadSegmentAsync(path, ct))
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
        await _writeLock.WaitAsync();
        try
        {
            if (_currentStream is not null)
            {
                await _currentStream.FlushAsync();
                await _currentStream.DisposeAsync();
                _currentStream = null;
            }
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }

    private void EnsureWritableSegment(long firstSequenceId)
    {
        if (_currentStream is not null)
        {
            return;
        }

        OpenNewSegment(firstSequenceId);
    }

    private async Task RotateAsync(long firstSequenceId, CancellationToken ct)
    {
        if (_currentStream is not null)
        {
            await _currentStream.FlushAsync(ct);
            await _currentStream.DisposeAsync();
            _logger.LogInformation("Closed tick journal segment {Path} ({Bytes:N0} bytes)", _currentPath, _currentBytes);
        }

        OpenNewSegment(firstSequenceId);
    }

    private void OpenNewSegment(long firstSequenceId)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var fileName = $"ticks-{timestamp}-{firstSequenceId:D18}.tjseg";
        _currentPath = Path.Combine(_options.DirectoryPath, fileName);
        _currentStream = new FileStream(_currentPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        TickJournalBinaryCodec.WriteSegmentHeader(_currentStream, DateTime.UtcNow.Ticks, firstSequenceId);
        _currentBytes = TickJournalBinaryCodec.SegmentHeaderLength;
        _logger.LogInformation("Opened tick journal segment {Path}", _currentPath);
    }

    private IEnumerable<string> EnumerateSegments()
    {
        if (!Directory.Exists(_options.DirectoryPath))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_options.DirectoryPath, "*.tjseg").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            yield return path;
        }
    }

    private async IAsyncEnumerable<TradeSignal> ReadSegmentAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!TickJournalBinaryCodec.TryReadSegmentHeader(stream, out _))
        {
            _logger.LogWarning("Skipping invalid tick journal segment header: {Path}", path);
            yield break;
        }

        while (!ct.IsCancellationRequested && TickJournalBinaryCodec.TryReadRecord(stream, out var signal))
        {
            yield return signal;
        }
    }
}
