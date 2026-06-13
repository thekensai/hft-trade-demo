namespace TradeDemo.Api.Models;

public sealed record Order
{
    public Guid OrderId { get; init; } = Guid.NewGuid();
    public string Symbol { get; init; } = "ES";
    public string Side { get; init; } = "BUY";
    public int Quantity { get; init; }
    public string OrderType { get; init; } = "Market";
    public decimal? LimitPrice { get; init; }
    public string Status { get; init; } = "New";
    public string Owner { get; init; } = "Manual";
    public bool UseMarketMakerLiquidity { get; init; }
    public int FilledQuantity { get; init; }
    public int RemainingQuantity { get; init; }
    public decimal? FilledPrice { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; init; }
    public string? RejectReason { get; init; }
}

public sealed record ExecutionReport(
    Guid ExecutionReportId,
    Guid OrderId,
    string Symbol,
    string Status,
    string Message,
    DateTime Timestamp,
    Fill? Fill = null,
    int CumQuantity = 0,
    int LeavesQuantity = 0,
    decimal? AverageFillPrice = null
);

public sealed record Fill(
    Guid FillId,
    Guid OrderId,
    string Symbol,
    string Side,
    int Quantity,
    decimal Price,
    DateTime Timestamp,
    string Owner = "Manual"
);

public sealed record Position(
    string Symbol,
    int Quantity,
    decimal AveragePrice,
    decimal MarkPrice,
    decimal RealizedPnl,
    decimal UnrealizedPnl,
    DateTime UpdatedAt
);

public sealed record OrderLifecycleEvent(
    Guid EventId,
    Guid OrderId,
    string Stage,
    string Message,
    DateTime Timestamp,
    int? Quantity = null,
    decimal? Price = null
);

public sealed record DepthLevel(
    decimal Price,
    int Quantity
);

public sealed record DepthSnapshot(
    string Symbol,
    decimal MidPrice,
    decimal BestBid,
    decimal BestAsk,
    IReadOnlyList<DepthLevel> Bids,
    IReadOnlyList<DepthLevel> Asks,
    DateTime Timestamp
);

public sealed record ExecutionStats(
    int OrdersSent,
    int OrdersFilled,
    int OpenOrders,
    int Cancels,
    int Rejections,
    int TotalFilledQuantity,
    decimal GrossNotional,
    decimal AverageFillPrice,
    double FillRatio,
    double AverageLatencyMs,
    decimal PnL,
    DateTime UpdatedAt
);

public sealed record LatencyBreakdown(
    double RiskCheckMs,
    double RouteMs,
    double ExchangeMs,
    double FillMs,
    double OtherMs,
    double TotalMs
);

public sealed record MarketMakerState(
    int Inventory,
    int InventoryLimit,
    string Status,
    bool BidEnabled,
    bool AskEnabled,
    DateTime UpdatedAt
);

public sealed record ModifyOrderRequest(
    int? Quantity,
    decimal? LimitPrice
);

public sealed record OrderResult(
    Order Order,
    IReadOnlyList<ExecutionReport> ExecutionReports,
    Fill? Fill,
    Position? Position,
    IReadOnlyList<OrderLifecycleEvent>? LifecycleEvents = null,
    IReadOnlyList<Fill>? Fills = null,
    IReadOnlyList<Fill>? ConsumedLiquidity = null,
    DepthSnapshot? Depth = null,
    ExecutionStats? Stats = null,
    LatencyBreakdown? Latency = null
);
