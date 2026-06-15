using Microsoft.Extensions.Logging.Abstractions;
using TradeDemo.Api.Models;
using TradeDemo.Api.Services;

namespace TradeDemo.Tests;

public sealed class OrderCommandProcessorTests
{
    [Fact]
    public async Task SubmitCommand_CompletesWithOrderResult()
    {
        var executor = new FakeOrderCommandExecutor();
        await using var harness = await OrderProcessorHarness.StartAsync(executor);
        var order = new Order { Symbol = "ES", Side = "BUY", Quantity = 10 };

        var outcome = await harness.Queue.EnqueueAsync(new SubmitOrderCommand(order));

        Assert.Equal(OrderCommandOutcomeStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.Result);
        Assert.Equal("Filled", outcome.Result.Order.Status);
        Assert.Equal(1, harness.Queue.GetStats().ProcessedTotal);
    }

    [Fact]
    public async Task CancelMissingOrder_ReturnsNotFound()
    {
        var executor = new FakeOrderCommandExecutor { CancelResult = null };
        await using var harness = await OrderProcessorHarness.StartAsync(executor);

        var outcome = await harness.Queue.EnqueueAsync(new CancelOrderCommand(Guid.NewGuid()));

        Assert.Equal(OrderCommandOutcomeStatus.NotFound, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.Equal(1, harness.Queue.GetStats().ProcessedTotal);
    }

    [Fact]
    public async Task ModifyMissingOrder_ReturnsNotFound()
    {
        var executor = new FakeOrderCommandExecutor { ModifyResult = null };
        await using var harness = await OrderProcessorHarness.StartAsync(executor);

        var outcome = await harness.Queue.EnqueueAsync(new ModifyOrderCommand(Guid.NewGuid(), new ModifyOrderRequest(20, 5_790m)));

        Assert.Equal(OrderCommandOutcomeStatus.NotFound, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.Equal(1, harness.Queue.GetStats().ProcessedTotal);
    }

    [Fact]
    public async Task Processor_SurvivesFailedCommandAndProcessesNextCommand()
    {
        var executor = new FakeOrderCommandExecutor { FailNextSubmit = true };
        await using var harness = await OrderProcessorHarness.StartAsync(executor);

        var failed = await harness.Queue.EnqueueAsync(new SubmitOrderCommand(new Order { Quantity = 10 }));
        var completed = await harness.Queue.EnqueueAsync(new SubmitOrderCommand(new Order { Quantity = 10 }));

        Assert.Equal(OrderCommandOutcomeStatus.Failed, failed.Status);
        Assert.Equal(OrderCommandOutcomeStatus.Completed, completed.Status);
        var stats = harness.Queue.GetStats();
        Assert.Equal(2, stats.ProcessedTotal);
        Assert.Equal(1, stats.FailedTotal);
    }

    [Fact]
    public async Task QueueStats_TrackEnqueuedProcessedAndTimings()
    {
        var executor = new FakeOrderCommandExecutor();
        await using var harness = await OrderProcessorHarness.StartAsync(executor);

        await harness.Queue.EnqueueAsync(new SubmitOrderCommand(new Order { Quantity = 10 }));
        await harness.Queue.EnqueueAsync(new CancelOrderCommand(Guid.NewGuid()));

        var stats = harness.Queue.GetStats();
        Assert.Equal(2, stats.EnqueuedTotal);
        Assert.Equal(2, stats.DequeuedTotal);
        Assert.Equal(2, stats.ProcessedTotal);
        Assert.Equal(0, stats.FailedTotal);
        Assert.Equal(0, stats.QueueDepth);
        Assert.True(stats.AverageQueueWaitMs >= 0);
        Assert.True(stats.MaxProcessingMs >= 0);
    }

    [Fact]
    public async Task EnqueueAsync_CallerCancellationDoesNotCancelAcceptedCommand()
    {
        var executor = new FakeOrderCommandExecutor { SubmitDelay = TimeSpan.FromMilliseconds(50) };
        await using var harness = await OrderProcessorHarness.StartAsync(executor);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await harness.Queue.EnqueueAsync(new SubmitOrderCommand(new Order { Quantity = 10 }), cts.Token));

        await WaitForAsync(() => harness.Queue.GetStats().ProcessedTotal == 1);
        Assert.Equal(1, executor.SubmitCount);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class OrderProcessorHarness : IAsyncDisposable
    {
        private readonly OrderCommandProcessor _processor;

        private OrderProcessorHarness(OrderCommandQueue queue, OrderCommandProcessor processor)
        {
            Queue = queue;
            _processor = processor;
        }

        public OrderCommandQueue Queue { get; }

        public static async Task<OrderProcessorHarness> StartAsync(IOrderCommandExecutor executor)
        {
            var queue = new OrderCommandQueue();
            var processor = new OrderCommandProcessor(queue, executor, NullLogger<OrderCommandProcessor>.Instance);
            await processor.StartAsync(CancellationToken.None);
            return new OrderProcessorHarness(queue, processor);
        }

        public async ValueTask DisposeAsync()
        {
            await _processor.StopAsync(CancellationToken.None);
            _processor.Dispose();
        }
    }

    private sealed class FakeOrderCommandExecutor : IOrderCommandExecutor
    {
        public bool FailNextSubmit { get; set; }

        public TimeSpan SubmitDelay { get; set; }

        public int SubmitCount { get; private set; }

        public OrderResult? CancelResult { get; set; } = CreateResult("Canceled");

        public OrderResult? ModifyResult { get; set; } = CreateResult("Working");

        public async Task<OrderResult> SubmitAsync(Order order, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            if (FailNextSubmit)
            {
                FailNextSubmit = false;
                throw new InvalidOperationException("simulated failure");
            }

            if (SubmitDelay > TimeSpan.Zero)
            {
                await Task.Delay(SubmitDelay, cancellationToken);
            }

            return CreateResult("Filled", order);
        }

        public OrderResult? Cancel(Guid orderId) => CancelResult;

        public Task<OrderResult?> ModifyAsync(Guid orderId, ModifyOrderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModifyResult);

        private static OrderResult CreateResult(string status, Order? order = null)
        {
            order ??= new Order { Quantity = 10 };
            return new OrderResult(order with { Status = status }, [], null, null);
        }
    }
}
