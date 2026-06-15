using TradeDemo.Api.Models;
using TradeDemo.Api.Services;

namespace TradeDemo.Tests;

public sealed class PositionManagerTests
{
    [Fact]
    public void ApplyFill_OpeningLongPositionCalculatesAveragePrice()
    {
        var manager = new PositionManager();

        var position = manager.ApplyFill("ES", 2, 100m);

        Assert.Equal("ES", position.Symbol);
        Assert.Equal(2, position.Quantity);
        Assert.Equal(100m, position.AveragePrice);
        Assert.Equal(0m, position.RealizedPnl);
    }

    [Fact]
    public void UpdateMarkPrice_CalculatesUnrealizedPnlWithContractMultiplier()
    {
        var manager = new PositionManager();
        manager.ApplyFill("ES", 2, 100m);

        var position = manager.UpdateMarkPrice("ES", 101m);

        Assert.Equal(100m, position.UnrealizedPnl);
    }

    [Fact]
    public void ApplyFill_PartialCloseRealizesPnlAndKeepsAveragePrice()
    {
        var manager = new PositionManager();
        manager.ApplyFill("ES", 2, 100m);

        var position = manager.ApplyFill("ES", -1, 102m);

        Assert.Equal(1, position.Quantity);
        Assert.Equal(100m, position.AveragePrice);
        Assert.Equal(100m, position.RealizedPnl);
    }

    [Fact]
    public void ApplyFill_ReversalRealizesPnlAndUsesFillPriceAsAveragePrice()
    {
        var manager = new PositionManager();
        manager.ApplyFill("ES", 2, 100m);

        var position = manager.ApplyFill("ES", -3, 98m);

        Assert.Equal(-1, position.Quantity);
        Assert.Equal(98m, position.AveragePrice);
        Assert.Equal(-200m, position.RealizedPnl);
    }

    [Fact]
    public void ApplyFills_WeightsMultipleSameSideFills()
    {
        var manager = new PositionManager();
        var orderId = Guid.NewGuid();
        var fills = new[]
        {
            new Fill(Guid.NewGuid(), orderId, "ES", "BUY", 1, 100m, DateTime.UtcNow),
            new Fill(Guid.NewGuid(), orderId, "ES", "BUY", 3, 104m, DateTime.UtcNow)
        };

        var position = manager.ApplyFills(fills);

        Assert.Equal(4, position.Quantity);
        Assert.Equal(103m, position.AveragePrice);
    }

    [Fact]
    public void Reset_ClearsPositions()
    {
        var manager = new PositionManager();
        manager.ApplyFill("ES", 2, 100m);

        manager.Reset();

        Assert.Empty(manager.GetPositions());
        Assert.Null(manager.GetPosition("ES"));
    }
}
