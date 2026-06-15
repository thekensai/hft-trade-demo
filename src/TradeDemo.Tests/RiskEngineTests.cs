using TradeDemo.Api.Models;
using TradeDemo.Api.Services;

namespace TradeDemo.Tests;

public sealed class RiskEngineTests
{
    private readonly RiskEngine _riskEngine = new();

    [Fact]
    public void Check_AcceptsValidBuyOrder()
    {
        var result = _riskEngine.Check(new Order { Side = "BUY", Quantity = 10 }, referencePrice: 100m);

        Assert.True(result.IsAccepted);
        Assert.Null(result.RejectReason);
    }

    [Fact]
    public void Check_RejectsNonPositiveQuantity()
    {
        var result = _riskEngine.Check(new Order { Side = "BUY", Quantity = 0 }, referencePrice: 100m);

        Assert.False(result.IsAccepted);
        Assert.Contains("Quantity", result.RejectReason);
    }

    [Fact]
    public void Check_RejectsUnsupportedSide()
    {
        var result = _riskEngine.Check(new Order { Side = "HOLD", Quantity = 10 }, referencePrice: 100m);

        Assert.False(result.IsAccepted);
        Assert.Contains("Side", result.RejectReason);
    }

    [Fact]
    public void Check_RejectsQuantityAboveLimit()
    {
        var result = _riskEngine.Check(new Order { Side = "BUY", Quantity = 1_001 }, referencePrice: 100m);

        Assert.False(result.IsAccepted);
        Assert.Contains("quantity", result.RejectReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_RejectsNotionalAboveLimit()
    {
        var result = _riskEngine.Check(new Order { Side = "BUY", Quantity = 1_000 }, referencePrice: 10_001m);

        Assert.False(result.IsAccepted);
        Assert.Contains("notional", result.RejectReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_RejectsLimitPriceOutsideReferenceBand()
    {
        var order = new Order { Side = "BUY", Quantity = 10, LimitPrice = 106m };

        var result = _riskEngine.Check(order, referencePrice: 100m);

        Assert.False(result.IsAccepted);
        Assert.Contains("Fat Finger", result.RejectReason);
    }

    [Fact]
    public void Check_RejectsPositionLimitBreach()
    {
        var order = new Order { Side = "BUY", Quantity = 10 };

        var result = _riskEngine.Check(order, referencePrice: 100m, currentPosition: 995);

        Assert.False(result.IsAccepted);
        Assert.Contains("Position", result.RejectReason);
    }
}
