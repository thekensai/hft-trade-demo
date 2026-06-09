namespace TradeDemo.Api.Journal;

public sealed class TickJournalOptions
{
    public bool Enabled { get; init; }
    public string Provider { get; init; } = "None";
    public int BatchSize { get; init; } = 4096;
    public int FlushIntervalMilliseconds { get; init; } = 250;
    public bool FailFast { get; init; }
    public LocalTickJournalOptions Local { get; init; } = new();
    public AzureBlobTickJournalOptions Blob { get; init; } = new();
    public EventHubsTickJournalOptions EventHubs { get; init; } = new();
}

public sealed class LocalTickJournalOptions
{
    public string DirectoryPath { get; init; } = "data/tick-journal";
    public long MaxSegmentBytes { get; init; } = 128L * 1024 * 1024;
    public bool FsyncOnFlush { get; init; }
}

public sealed class AzureBlobTickJournalOptions
{
    public string? ConnectionString { get; init; }
    public string? AccountUri { get; init; }
    public string ContainerName { get; init; } = "tick-journal";
    public string Prefix { get; init; } = "ticks";
    public long MaxSegmentBytes { get; init; } = 128L * 1024 * 1024;
}

public sealed class EventHubsTickJournalOptions
{
    public string? ConnectionString { get; init; }
    public string? FullyQualifiedNamespace { get; init; }
    public string EventHubName { get; init; } = "ticks";
    public string ConsumerGroup { get; init; } = "$Default";
    public int MaxBatchBytes { get; init; } = 900 * 1024;
    public string PartitionKeyStrategy { get; init; } = "Symbol";
}
