using TradeDemo.Api.Models;
using TradeDemo.Api.Services;

namespace TradeDemo.Tests;

public sealed class ExchangeSimulatorTests
{
    [Fact]
    public async Task SubmitOrderAsync_MarketBuyFillsAgainstDepth()
    {
        var positions = new PositionManager();
        var simulator = CreateSimulator(positions);
        var order = new Order { Symbol = "ES", Side = "BUY", Quantity = 10, OrderType = "Market" };

        var result = await simulator.SubmitOrderAsync(order);

        Assert.Equal("Filled", result.Order.Status);
        Assert.Equal(10, result.Order.FilledQuantity);
        Assert.Equal(0, result.Order.RemainingQuantity);
        Assert.NotNull(result.Fill);
        Assert.NotNull(result.Position);
        Assert.Equal(10, result.Position.Quantity);
        Assert.Equal(1, result.Stats?.OrdersFilled);
        Assert.Equal(10, positions.GetPosition("ES")?.Quantity);
    }

    [Fact]
    public async Task SubmitOrderAsync_FarLimitBuyRestsAsOpenOrder()
    {
        var simulator = CreateSimulator();
        var order = new Order { Symbol = "ES", Side = "BUY", Quantity = 10, OrderType = "Limit", LimitPrice = 5_800m };

        var result = await simulator.SubmitOrderAsync(order);

        Assert.Equal("Working", result.Order.Status);
        Assert.Equal(10, result.Order.RemainingQuantity);
        Assert.Empty(result.Fills ?? []);
        Assert.Single(simulator.GetOpenOrders());
        Assert.Equal(1, result.Stats?.OpenOrders);
    }

    [Fact]
    public async Task SubmitOrderAsync_RejectsTooLargeQuantity()
    {
        var simulator = CreateSimulator();
        var order = new Order { Symbol = "ES", Side = "BUY", Quantity = 1_001, OrderType = "Market" };

        var result = await simulator.SubmitOrderAsync(order);

        Assert.Equal("Rejected", result.Order.Status);
        Assert.Null(result.Fill);
        Assert.Contains("quantity", result.Order.RejectReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Stats?.Rejections);
    }

    [Fact]
    public async Task CancelOrder_CancelsRestingOrder()
    {
        var simulator = CreateSimulator();
        var resting = await simulator.SubmitOrderAsync(new Order
        {
            Symbol = "ES",
            Side = "BUY",
            Quantity = 10,
            OrderType = "Limit",
            LimitPrice = 5_800m
        });

        var result = simulator.CancelOrder(resting.Order.OrderId);

        Assert.NotNull(result);
        Assert.Equal("Canceled", result.Order.Status);
        Assert.Empty(simulator.GetOpenOrders());
        Assert.Equal(1, result.Stats?.Cancels);
    }

    [Fact]
    public async Task ModifyOrderAsync_UpdatesRestingOrderQuantityAndLimitPrice()
    {
        var simulator = CreateSimulator();
        var resting = await simulator.SubmitOrderAsync(new Order
        {
            Symbol = "ES",
            Side = "BUY",
            Quantity = 10,
            OrderType = "Limit",
            LimitPrice = 5_800m
        });

        var result = await simulator.ModifyOrderAsync(resting.Order.OrderId, new ModifyOrderRequest(20, 5_790m));

        Assert.NotNull(result);
        Assert.Equal("Working", result.Order.Status);
        Assert.Equal(20, result.Order.Quantity);
        Assert.Equal(20, result.Order.RemainingQuantity);
        Assert.Equal(5_790m, result.Order.LimitPrice);
        var openOrder = Assert.Single(simulator.GetOpenOrders());
        Assert.Equal(20, openOrder.Quantity);
    }

    private static ExchangeSimulator CreateSimulator(PositionManager? positions = null) =>
        new(new RiskEngine(), positions ?? new PositionManager());
}
