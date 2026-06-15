using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public interface IOrderCommandQueue
{
    ValueTask<OrderCommandOutcome> EnqueueAsync(OrderCommand command, CancellationToken cancellationToken = default);

    OrderCommandQueueStats GetStats();
}
