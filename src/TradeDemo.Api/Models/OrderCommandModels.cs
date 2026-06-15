namespace TradeDemo.Api.Models;

public abstract record OrderCommand(Guid CommandId, DateTime CreatedAt);

public sealed record SubmitOrderCommand(Order Order, Guid CommandId, DateTime CreatedAt) : OrderCommand(CommandId, CreatedAt)
{
    public SubmitOrderCommand(Order order)
        : this(order, Guid.NewGuid(), DateTime.UtcNow)
    {
    }
}

public sealed record CancelOrderCommand(Guid OrderId, Guid CommandId, DateTime CreatedAt) : OrderCommand(CommandId, CreatedAt)
{
    public CancelOrderCommand(Guid orderId)
        : this(orderId, Guid.NewGuid(), DateTime.UtcNow)
    {
    }
}

public sealed record ModifyOrderCommand(Guid OrderId, ModifyOrderRequest Request, Guid CommandId, DateTime CreatedAt) : OrderCommand(CommandId, CreatedAt)
{
    public ModifyOrderCommand(Guid orderId, ModifyOrderRequest request)
        : this(orderId, request, Guid.NewGuid(), DateTime.UtcNow)
    {
    }
}

public enum OrderCommandOutcomeStatus
{
    Completed,
    NotFound,
    Failed,
    Canceled
}

public sealed record OrderCommandOutcome(
    OrderCommandOutcomeStatus Status,
    OrderResult? Result,
    string? ErrorMessage,
    TimeSpan QueueWait,
    TimeSpan ProcessingTime,
    DateTime CompletedAt)
{
    public static OrderCommandOutcome Completed(OrderResult result, TimeSpan queueWait, TimeSpan processingTime) =>
        new(OrderCommandOutcomeStatus.Completed, result, null, queueWait, processingTime, DateTime.UtcNow);

    public static OrderCommandOutcome NotFound(TimeSpan queueWait, TimeSpan processingTime) =>
        new(OrderCommandOutcomeStatus.NotFound, null, null, queueWait, processingTime, DateTime.UtcNow);

    public static OrderCommandOutcome Failed(string errorMessage, TimeSpan queueWait, TimeSpan processingTime) =>
        new(OrderCommandOutcomeStatus.Failed, null, errorMessage, queueWait, processingTime, DateTime.UtcNow);

    public static OrderCommandOutcome Canceled(TimeSpan queueWait, TimeSpan processingTime) =>
        new(OrderCommandOutcomeStatus.Canceled, null, "Order command processing was canceled.", queueWait, processingTime, DateTime.UtcNow);
}

public sealed record OrderCommandQueueStats(
    long EnqueuedTotal,
    long DequeuedTotal,
    long ProcessedTotal,
    long FailedTotal,
    long CanceledTotal,
    int QueueDepth,
    double AverageQueueWaitMs,
    double MaxQueueWaitMs,
    double AverageProcessingMs,
    double MaxProcessingMs,
    DateTime Timestamp);
