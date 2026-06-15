using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public sealed class OrderCommandProcessor : BackgroundService
{
    private readonly OrderCommandQueue _queue;
    private readonly IOrderCommandExecutor _executor;
    private readonly ILogger<OrderCommandProcessor> _logger;

    public OrderCommandProcessor(OrderCommandQueue queue, IOrderCommandExecutor executor, ILogger<OrderCommandProcessor> logger)
    {
        _queue = queue;
        _executor = executor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrderCommandProcessor started — FIFO order command queue enabled");

        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(queued, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
        finally
        {
            _queue.CancelPending();
        }
    }

    private async Task ProcessAsync(QueuedOrderCommand queued, CancellationToken stoppingToken)
    {
        _queue.RecordDequeued();
        var queueWait = DateTime.UtcNow - queued.Command.CreatedAt;
        var startedAt = DateTime.UtcNow;

        OrderCommandOutcome outcome;
        try
        {
            outcome = await DispatchAsync(queued.Command, queueWait, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            outcome = OrderCommandOutcome.Canceled(queueWait, DateTime.UtcNow - startedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order command {CommandId} failed", queued.Command.CommandId);
            outcome = OrderCommandOutcome.Failed(ex.Message, queueWait, DateTime.UtcNow - startedAt);
        }

        queued.Complete(outcome);
        _queue.RecordCompleted(outcome);
    }

    private async Task<OrderCommandOutcome> DispatchAsync(OrderCommand command, TimeSpan queueWait, CancellationToken stoppingToken)
    {
        var startedAt = DateTime.UtcNow;
        switch (command)
        {
            case SubmitOrderCommand submit:
            {
                var result = await _executor.SubmitAsync(submit.Order, stoppingToken);
                return OrderCommandOutcome.Completed(result, queueWait, DateTime.UtcNow - startedAt);
            }
            case CancelOrderCommand cancel:
            {
                var result = _executor.Cancel(cancel.OrderId);
                var processingTime = DateTime.UtcNow - startedAt;
                return result is null
                    ? OrderCommandOutcome.NotFound(queueWait, processingTime)
                    : OrderCommandOutcome.Completed(result, queueWait, processingTime);
            }
            case ModifyOrderCommand modify:
            {
                var result = await _executor.ModifyAsync(modify.OrderId, modify.Request, stoppingToken);
                var processingTime = DateTime.UtcNow - startedAt;
                return result is null
                    ? OrderCommandOutcome.NotFound(queueWait, processingTime)
                    : OrderCommandOutcome.Completed(result, queueWait, processingTime);
            }
            default:
                return OrderCommandOutcome.Failed($"Unsupported order command type {command.GetType().Name}", queueWait, DateTime.UtcNow - startedAt);
        }
    }
}
