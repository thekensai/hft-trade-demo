using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public sealed class RiskEngine
{
    private const int MaxOrderQuantity = 1_000;
    private const int MaxPosition = 1_000;
    private const decimal MaxOrderNotional = 10_000_000m;
    private const decimal FatFingerBand = 0.05m;

    public RiskCheckResult Check(Order order, decimal referencePrice, int currentPosition = 0)
    {
        if (order.Quantity <= 0)
        {
            return RiskCheckResult.Rejected("Quantity must be positive");
        }

        if (!IsSupportedSide(order.Side))
        {
            return RiskCheckResult.Rejected("Side must be BUY or SELL");
        }

        if (order.Quantity > MaxOrderQuantity)
        {
            return RiskCheckResult.Rejected($"Risk Limit Breach - max order quantity exceeded ({MaxOrderQuantity:N0})");
        }

        if (order.Quantity * referencePrice > MaxOrderNotional)
        {
            return RiskCheckResult.Rejected($"Risk Limit Breach - max order notional exceeded ({MaxOrderNotional:C0})");
        }

        if (order.LimitPrice is not null && Math.Abs(order.LimitPrice.Value - referencePrice) / referencePrice > FatFingerBand)
        {
            return RiskCheckResult.Rejected("Fat Finger Check - limit price outside 5% reference band");
        }

        var signedQuantity = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? order.Quantity : -order.Quantity;
        if (Math.Abs(currentPosition + signedQuantity) > MaxPosition)
        {
            return RiskCheckResult.Rejected($"Position Limit Exceeded ({MaxPosition:N0})");
        }

        if (ShouldDemoReject(order))
        {
            return RiskCheckResult.Rejected("Risk Limit Breach - simulated venue throttle");
        }

        return RiskCheckResult.Accepted();
    }

    private static bool ShouldDemoReject(Order order) =>
        order.Quantity <= 5 && Math.Abs(order.OrderId.GetHashCode()) % 29 == 0;

    private static bool IsSupportedSide(string side) =>
        side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ||
        side.Equals("SELL", StringComparison.OrdinalIgnoreCase);
}

public sealed record RiskCheckResult(bool IsAccepted, string? RejectReason)
{
    public static RiskCheckResult Accepted() => new(true, null);

    public static RiskCheckResult Rejected(string reason) => new(false, reason);
}
