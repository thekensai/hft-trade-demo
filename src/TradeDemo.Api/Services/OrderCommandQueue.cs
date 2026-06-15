using System.Threading.Channels;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public sealed class OrderCommandQueue : IOrderCommandQueue
{
    private const int ChannelCapacity = 4_096;

    private readonly Channel<QueuedOrderCommand> _channel;
    private long _enqueuedTotal;
    private long _dequeuedTotal;
    private long _processedTotal;
    private long _failedTotal;
    private long _canceledTotal;
    private long _totalQueueWaitTicks;
    private long _maxQueueWaitTicks;
    private long _totalProcessingTicks;
    private long _maxProcessingTicks;

    public OrderCommandQueue()
    {
        _channel = Channel.CreateBounded<QueuedOrderCommand>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    internal ChannelReader<QueuedOrderCommand> Reader => _channel.Reader;

    public async ValueTask<OrderCommandOutcome> EnqueueAsync(OrderCommand command, CancellationToken cancellationToken = default)
    {
        var queued = new QueuedOrderCommand(command);
        await _channel.Writer.WriteAsync(queued, cancellationToken);
        Interlocked.Increment(ref _enqueuedTotal);

        return await queued.Completion.Task.WaitAsync(cancellationToken);
    }

    public OrderCommandQueueStats GetStats()
    {
        var processed = Interlocked.Read(ref _processedTotal);
        var totalQueueWaitTicks = Interlocked.Read(ref _totalQueueWaitTicks);
        var totalProcessingTicks = Interlocked.Read(ref _totalProcessingTicks);

        return new OrderCommandQueueStats(
            EnqueuedTotal: Interlocked.Read(ref _enqueuedTotal),
            DequeuedTotal: Interlocked.Read(ref _dequeuedTotal),
            ProcessedTotal: processed,
            FailedTotal: Interlocked.Read(ref _failedTotal),
            CanceledTotal: Interlocked.Read(ref _canceledTotal),
            QueueDepth: _channel.Reader.Count,
            AverageQueueWaitMs: processed == 0 ? 0 : TicksToMilliseconds(totalQueueWaitTicks) / processed,
            MaxQueueWaitMs: TicksToMilliseconds(Interlocked.Read(ref _maxQueueWaitTicks)),
            AverageProcessingMs: processed == 0 ? 0 : TicksToMilliseconds(totalProcessingTicks) / processed,
            MaxProcessingMs: TicksToMilliseconds(Interlocked.Read(ref _maxProcessingTicks)),
            Timestamp: DateTime.UtcNow);
    }

    internal void RecordDequeued() => Interlocked.Increment(ref _dequeuedTotal);

    internal void RecordCompleted(OrderCommandOutcome outcome)
    {
        Interlocked.Increment(ref _processedTotal);
        if (outcome.Status == OrderCommandOutcomeStatus.Failed)
        {
            Interlocked.Increment(ref _failedTotal);
        }
        else if (outcome.Status == OrderCommandOutcomeStatus.Canceled)
        {
            Interlocked.Increment(ref _canceledTotal);
        }

        var queueWaitTicks = outcome.QueueWait.Ticks;
        var processingTicks = outcome.ProcessingTime.Ticks;
        Interlocked.Add(ref _totalQueueWaitTicks, queueWaitTicks);
        Interlocked.Add(ref _totalProcessingTicks, processingTicks);
        UpdateMax(ref _maxQueueWaitTicks, queueWaitTicks);
        UpdateMax(ref _maxProcessingTicks, processingTicks);
    }

    internal void CancelPending()
    {
        while (_channel.Reader.TryRead(out var queued))
        {
            RecordDequeued();
            var outcome = OrderCommandOutcome.Canceled(DateTime.UtcNow - queued.Command.CreatedAt, TimeSpan.Zero);
            queued.Complete(outcome);
            RecordCompleted(outcome);
        }
    }

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var original = Interlocked.CompareExchange(ref target, value, current);
            if (original == current)
            {
                return;
            }
            current = original;
        }
    }

    private static double TicksToMilliseconds(long ticks) =>
        TimeSpan.FromTicks(ticks).TotalMilliseconds;
}

internal sealed class QueuedOrderCommand
{
    public QueuedOrderCommand(OrderCommand command)
    {
        Command = command;
    }

    public OrderCommand Command { get; }

    public TaskCompletionSource<OrderCommandOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Complete(OrderCommandOutcome outcome) => Completion.TrySetResult(outcome);
}
