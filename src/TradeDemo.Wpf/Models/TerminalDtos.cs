using System.Text.Json.Serialization;

namespace TradeDemo.Wpf.Models;

public sealed class TradeSignalDto
{
    public string Symbol { get; set; } = "";
    public decimal BidPrice { get; set; }
    public decimal AskPrice { get; set; }
    public decimal MidPrice { get; set; }
    public decimal Change { get; set; }
    public double ChangePercent { get; set; }
    public long Volume { get; set; }
    public string Exchange { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Direction { get; set; } = "";
    public long SequenceId { get; set; }
}

public sealed class QueueStatsDto
{
    public long Processed { get; set; }
    public long Dropped { get; set; }
    public int QueueDepth { get; set; }
}

public sealed class HubStatsDto
{
    public long ProcessedTotal { get; set; }
    public long DroppedTotal { get; set; }
    public long CoalescedTotal { get; set; }
    public double CoalescedPerSec { get; set; }
    public int QueueDepth { get; set; }
    public long BroadcastTotal { get; set; }
    public double SnapshotsPerSec { get; set; }
    public long ServerGeneratedTotal { get; set; }
    public double ServerGenerationRatePerSec { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class SystemMetricsDto
{
    public long TotalMessages { get; set; }
    public double MessagesPerSecond { get; set; }
    public double CpuUsagePercent { get; set; }
    public long MemoryUsageBytes { get; set; }
    public long WorkingSetBytes { get; set; }
    public int ThreadCount { get; set; }
}

public sealed class OrderDto
{
    public Guid OrderId { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "ES";
    public string Side { get; set; } = "BUY";
    public int Quantity { get; set; }
    public string OrderType { get; set; } = "Market";
    public decimal? LimitPrice { get; set; }
    public string Status { get; set; } = "New";
    public string Owner { get; set; } = "Manual";
    public bool UseMarketMakerLiquidity { get; set; }
    public int FilledQuantity { get; set; }
    public int RemainingQuantity { get; set; }
    public decimal? FilledPrice { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? RejectReason { get; set; }
}

public sealed class ExecutionReportDto
{
    public Guid ExecutionReportId { get; set; }
    public Guid OrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public FillDto? Fill { get; set; }
    public int CumQuantity { get; set; }
    public int LeavesQuantity { get; set; }
    public decimal? AverageFillPrice { get; set; }
}

public sealed class FillDto
{
    public Guid FillId { get; set; }
    public Guid OrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public DateTime Timestamp { get; set; }
    public string Owner { get; set; } = "Manual";
}

public sealed class PositionDto
{
    public string Symbol { get; set; } = "";
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OrderLifecycleEventDto
{
    public Guid EventId { get; set; }
    public Guid OrderId { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int? Quantity { get; set; }
    public decimal? Price { get; set; }
    public string? Symbol { get; set; }
}

public sealed class DepthLevelDto
{
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

public sealed class DepthSnapshotDto
{
    public string Symbol { get; set; } = "";
    public decimal MidPrice { get; set; }
    public decimal BestBid { get; set; }
    public decimal BestAsk { get; set; }
    public List<DepthLevelDto> Bids { get; set; } = [];
    public List<DepthLevelDto> Asks { get; set; } = [];
    public DateTime Timestamp { get; set; }
    public long Sequence { get; set; }
}

public sealed class ExecutionStatsDto
{
    public int OrdersSent { get; set; }
    public int OrdersFilled { get; set; }
    public int OpenOrders { get; set; }
    public int Cancels { get; set; }
    public int Rejections { get; set; }
    public int TotalFilledQuantity { get; set; }
    public decimal GrossNotional { get; set; }
    public decimal AverageFillPrice { get; set; }
    public double FillRatio { get; set; }
    public double AverageLatencyMs { get; set; }
    [JsonPropertyName("pnl")]
    public decimal PnL { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TradeMonitorRowDto
{
    public Guid OrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Status { get; set; } = "";
    public string Venue { get; set; } = "";
    public double? LatencyMs { get; set; }
    public double FillPercent { get; set; }
    [JsonPropertyName("pnl")]
    public decimal PnL { get; set; }
    public string Health { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public sealed class LatencyBreakdownDto
{
    public double RiskCheckMs { get; set; }
    public double RouteMs { get; set; }
    public double ExchangeMs { get; set; }
    public double FillMs { get; set; }
    public double OtherMs { get; set; }
    public double TotalMs { get; set; }
}

public sealed class SlippageMetricsDto
{
    public decimal ArrivalPrice { get; set; }
    public decimal AverageFillPrice { get; set; }
    public decimal SlippagePoints { get; set; }
    public decimal SlippageDollars { get; set; }
    public string Symbol { get; set; } = "";
}

public sealed class MarketMakerStateDto
{
    public int Inventory { get; set; }
    public int InventoryLimit { get; set; }
    public string Status { get; set; } = "NORMAL";
    public bool BidEnabled { get; set; }
    public bool AskEnabled { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class OrderResultDto
{
    public OrderDto? Order { get; set; }
    public List<ExecutionReportDto> ExecutionReports { get; set; } = [];
    public FillDto? Fill { get; set; }
    public PositionDto? Position { get; set; }
    public List<OrderLifecycleEventDto>? LifecycleEvents { get; set; }
    public List<FillDto>? Fills { get; set; }
    public List<FillDto>? ConsumedLiquidity { get; set; }
    public DepthSnapshotDto? Depth { get; set; }
    public ExecutionStatsDto? Stats { get; set; }
    public LatencyBreakdownDto? Latency { get; set; }
    public SlippageMetricsDto? Slippage { get; set; }
}
