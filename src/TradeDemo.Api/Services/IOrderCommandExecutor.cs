using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public interface IOrderCommandExecutor
{
    Task<OrderResult> SubmitAsync(Order order, CancellationToken cancellationToken = default);

    OrderResult? Cancel(Guid orderId);

    Task<OrderResult?> ModifyAsync(Guid orderId, ModifyOrderRequest request, CancellationToken cancellationToken = default);
}

public sealed class ExchangeOrderCommandExecutor : IOrderCommandExecutor
{
    private readonly ExchangeSimulator _exchange;

    public ExchangeOrderCommandExecutor(ExchangeSimulator exchange)
    {
        _exchange = exchange;
    }

    public Task<OrderResult> SubmitAsync(Order order, CancellationToken cancellationToken = default) =>
        _exchange.SubmitOrderAsync(order, cancellationToken);

    public OrderResult? Cancel(Guid orderId) =>
        _exchange.CancelOrder(orderId);

    public Task<OrderResult?> ModifyAsync(Guid orderId, ModifyOrderRequest request, CancellationToken cancellationToken = default) =>
        _exchange.ModifyOrderAsync(orderId, request, cancellationToken);
}
