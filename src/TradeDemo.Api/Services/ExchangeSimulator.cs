using System.Diagnostics;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public sealed class ExchangeSimulator
{
    private const decimal EsTickSize = 0.25m;
    private const int MarketMakerInventoryLimit = 100;
    private const int MarketMakerWarningThreshold = 75;
    private static readonly TimeSpan SimulatedHop = TimeSpan.FromMilliseconds(2);

    private readonly object _sync = new();
    private readonly RiskEngine _riskEngine;
    private readonly PositionManager _positionManager;
    private readonly List<Order> _orders = [];
    private readonly List<ExecutionReport> _executions = [];
    private readonly Dictionary<Guid, List<OrderLifecycleEvent>> _lifecycleByOrderId = [];
    private readonly Dictionary<string, DepthBookState> _depthBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _marketMakerInventoryBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> _latenciesMs = [];

    public ExchangeSimulator(RiskEngine riskEngine, PositionManager positionManager)
    {
        _riskEngine = riskEngine;
        _positionManager = positionManager;
    }

    public async Task<OrderResult> SubmitOrderAsync(Order request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var order = NormalizeOrder(request);
        var preTradeDepth = GetDepth(order.Symbol);
        var lifecycle = new List<OrderLifecycleEvent>();
        var reports = new List<ExecutionReport>();
        var fills = new List<Fill>();
        Position? position = null;
        double riskMs = 0;
        double routeMs = 0;
        double exchangeMs = 0;
        double fillMs = 0;

        AddLifecycle(lifecycle, order, "Submitted", $"Order Submitted {order.Side} {order.Quantity:N0} {order.Symbol} @ {FormatOrderPrice(order)}");

        var riskStart = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(SimulatedHop, ct);
        var referencePrice = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? preTradeDepth.BestAsk : preTradeDepth.BestBid;
        var currentPosition = _positionManager.GetPosition(order.Symbol)?.Quantity ?? 0;
        var risk = _riskEngine.Check(order, referencePrice, currentPosition);
        riskMs = sw.Elapsed.TotalMilliseconds - riskStart;
        if (!risk.IsAccepted)
        {
            var rejected = order with
            {
                Status = "Rejected",
                RemainingQuantity = order.Quantity,
                UpdatedAt = DateTime.UtcNow,
                RejectReason = risk.RejectReason
            };
            AddLifecycle(lifecycle, rejected, "Rejected", risk.RejectReason ?? "Risk check rejected order");
            reports.Add(NewReport(rejected, "Rejected", risk.RejectReason ?? "Risk check rejected order"));
            var rejectedLatency = BuildLatency(riskMs, routeMs, exchangeMs, fillMs, sw.Elapsed.TotalMilliseconds);
            Save(rejected, reports, lifecycle, sw.Elapsed.TotalMilliseconds);
            return BuildResult(rejected, reports, fills, position, lifecycle, preTradeDepth, rejectedLatency);
        }

        AddLifecycle(lifecycle, order, "Risk Check Passed", $"Risk Check Passed ({riskMs:F1}ms)");

        var routeStart = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(SimulatedHop, ct);
        routeMs = sw.Elapsed.TotalMilliseconds - routeStart;

        AddLifecycle(lifecycle, order, "Routed", $"Routed to {RouteFor(order.Symbol)} ({routeMs:F1}ms)");

        var exchangeStart = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(SimulatedHop, ct);
        exchangeMs = sw.Elapsed.TotalMilliseconds - exchangeStart;

        var accepted = order with
        {
            Status = "Accepted",
            RemainingQuantity = order.Quantity,
            UpdatedAt = DateTime.UtcNow
        };
        AddLifecycle(lifecycle, accepted, "Accepted", $"Accepted ({exchangeMs:F1}ms)");
        reports.Add(NewReport(accepted, "Accepted", "Order accepted by exchange simulator"));
        Save(accepted, reports[^1], lifecycle.Last());

        await Task.Delay(SimulatedHop, ct);

        if (CanRest(accepted, preTradeDepth))
        {
            var working = accepted with { Status = "Working", UpdatedAt = DateTime.UtcNow };
            AddLifecycle(lifecycle, working, "Working", $"Resting {working.Side} {working.RemainingQuantity:N0} {working.Symbol} @ {FormatOrderPrice(working)}");
            reports.Add(NewReport(working, "Working", "Order resting in open-orders book"));
            var workingLatency = BuildLatency(riskMs, routeMs, exchangeMs, fillMs, sw.Elapsed.TotalMilliseconds);
            Save(working, reports[^1], lifecycle.Last(), sw.Elapsed.TotalMilliseconds);
            return BuildResult(working, reports, fills, position, lifecycle, preTradeDepth, workingLatency);
        }

        var fillStart = sw.Elapsed.TotalMilliseconds;
        var fillResult = FillAgainstDepth(accepted, lifecycle, reports, fills);
        foreach (var fill in fills)
        {
            position = _positionManager.ApplyFill(fill);
            ApplyMarketMakerInventory(fill);
            await Task.Delay(SimulatedHop, ct);
        }
        fillMs = sw.Elapsed.TotalMilliseconds - fillStart;

        var filledOrder = accepted with
        {
            Status = fillResult.RemainingQuantity == 0 ? "Filled" : "Working",
            FilledQuantity = fillResult.FilledQuantity,
            RemainingQuantity = fillResult.RemainingQuantity,
            FilledPrice = fills.LastOrDefault()?.Price,
            AverageFillPrice = fillResult.AverageFillPrice,
            UpdatedAt = DateTime.UtcNow
        };

        AddLifecycle(
            lifecycle,
            filledOrder,
            filledOrder.Status,
            filledOrder.Status == "Filled" ? $"Filled ({fillMs:F1}ms)" : $"Working {filledOrder.RemainingQuantity:N0} remaining");
        reports.Add(NewReport(filledOrder, filledOrder.Status, lifecycle[^1].Message, null, filledOrder.FilledQuantity, filledOrder.RemainingQuantity, filledOrder.AverageFillPrice));
        var latency = BuildLatency(riskMs, routeMs, exchangeMs, fillMs, sw.Elapsed.TotalMilliseconds);
        Save(filledOrder, reports[^1], lifecycle.Last(), sw.Elapsed.TotalMilliseconds);

        return BuildResult(filledOrder, reports, fills, position, lifecycle, GetDepth(filledOrder.Symbol), latency);
    }

    public IReadOnlyList<Order> GetOrders()
    {
        lock (_sync)
        {
            return _orders.OrderByDescending(o => o.CreatedAt).ToArray();
        }
    }

    public IReadOnlyList<Order> GetOpenOrders()
    {
        lock (_sync)
        {
            return _orders
                .Where(IsOpen)
                .OrderByDescending(o => o.CreatedAt)
                .ToArray();
        }
    }

    public IReadOnlyList<ExecutionReport> GetExecutions()
    {
        lock (_sync)
        {
            return _executions.OrderByDescending(e => e.Timestamp).ToArray();
        }
    }

    public OrderResult? CancelOrder(Guid orderId)
    {
        lock (_sync)
        {
            var order = _orders.FirstOrDefault(o => o.OrderId == orderId);
            if (order is null)
            {
                return null;
            }

            if (!IsOpen(order))
            {
                var rejectReport = NewReport(order, "CancelRejected", $"Cannot cancel {order.Status} order");
                _executions.Add(rejectReport);
                return new OrderResult(order, [rejectReport], null, null, GetLifecycleUnsafe(order.OrderId), [], [], GetDepth(order.Symbol), GetExecutionStatsUnsafe());
            }

            var canceled = order with { Status = "Canceled", UpdatedAt = DateTime.UtcNow };
            var evt = NewLifecycle(canceled, "Canceled", "Canceled by user");
            var report = NewReport(canceled, "Canceled", "Canceled by user");
            SaveUnsafe(canceled, report, evt);
            return new OrderResult(canceled, [report], null, null, GetLifecycleUnsafe(orderId), [], [], GetDepth(canceled.Symbol), GetExecutionStatsUnsafe());
        }
    }

    public async Task<OrderResult?> ModifyOrderAsync(Guid orderId, ModifyOrderRequest request, CancellationToken ct = default)
    {
        Order? modified;
        lock (_sync)
        {
            var order = _orders.FirstOrDefault(o => o.OrderId == orderId);
            if (order is null)
            {
                return null;
            }

            if (!IsOpen(order))
            {
                var rejectReport = NewReport(order, "ModifyRejected", $"Cannot modify {order.Status} order");
                _executions.Add(rejectReport);
                return new OrderResult(order, [rejectReport], null, null, GetLifecycleUnsafe(orderId), [], [], GetDepth(order.Symbol), GetExecutionStatsUnsafe());
            }

            var newQuantity = Math.Max(order.FilledQuantity, request.Quantity ?? order.Quantity);
            modified = order with
            {
                Quantity = newQuantity,
                LimitPrice = request.LimitPrice ?? order.LimitPrice,
                RemainingQuantity = Math.Max(0, newQuantity - order.FilledQuantity),
                UpdatedAt = DateTime.UtcNow
            };

            var evt = NewLifecycle(modified, "Modified", $"Modified to {modified.Side} {modified.RemainingQuantity:N0} {modified.Symbol} @ {FormatOrderPrice(modified)}");
            var report = NewReport(modified, "Modified", evt.Message, null, modified.FilledQuantity, modified.RemainingQuantity, modified.AverageFillPrice);
            SaveUnsafe(modified, report, evt);
        }

        if (CanRest(modified, GetDepth(modified.Symbol)))
        {
            return new OrderResult(modified, [], null, null, GetLifecycle(modified.OrderId), [], [], GetDepth(modified.Symbol), GetExecutionStats());
        }

        return await SubmitOrderAsync(modified with { OrderId = modified.OrderId, Quantity = modified.RemainingQuantity, Status = "New" }, ct);
    }

    public DepthSnapshot GetDepth(string symbol)
    {
        lock (_sync)
        {
            return GetDepthUnsafe(symbol);
        }
    }

    public ExecutionStats GetExecutionStats()
    {
        lock (_sync)
        {
            return GetExecutionStatsUnsafe();
        }
    }

    public MarketMakerState GetMarketMakerState()
    {
        var inventory = GetMarketMakerInventory("ES");
        var status = inventory switch
        {
            >= MarketMakerInventoryLimit => "BID DISABLED",
            <= -MarketMakerInventoryLimit => "ASK DISABLED",
            > MarketMakerWarningThreshold => "INVENTORY LONG",
            < -MarketMakerWarningThreshold => "INVENTORY SHORT",
            _ => "NORMAL"
        };

        return new MarketMakerState(
            Inventory: inventory,
            InventoryLimit: MarketMakerInventoryLimit,
            Status: status,
            BidEnabled: inventory < MarketMakerWarningThreshold,
            AskEnabled: inventory > -MarketMakerWarningThreshold,
            UpdatedAt: DateTime.UtcNow);
    }

    public IReadOnlyList<OrderLifecycleEvent> GetLifecycle(Guid orderId)
    {
        lock (_sync)
        {
            return GetLifecycleUnsafe(orderId);
        }
    }

    private Order NormalizeOrder(Order request)
    {
        var now = DateTime.UtcNow;
        return request with
        {
            OrderId = request.OrderId == Guid.Empty ? Guid.NewGuid() : request.OrderId,
            Symbol = string.IsNullOrWhiteSpace(request.Symbol) ? "ES" : request.Symbol.ToUpperInvariant(),
            Side = string.IsNullOrWhiteSpace(request.Side) ? "BUY" : request.Side.ToUpperInvariant(),
            OrderType = string.IsNullOrWhiteSpace(request.OrderType) ? "Market" : request.OrderType,
            Status = "New",
            Owner = string.IsNullOrWhiteSpace(request.Owner) ? "Manual" : request.Owner,
            UseMarketMakerLiquidity = request.UseMarketMakerLiquidity,
            FilledQuantity = request.FilledQuantity,
            RemainingQuantity = request.Quantity,
            CreatedAt = request.CreatedAt == default ? now : request.CreatedAt,
            UpdatedAt = now,
            RejectReason = null
        };
    }

    private static bool CanRest(Order order, DepthSnapshot preTradeDepth)
    {
        if (order.OrderType.Equals("Market", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (order.LimitPrice is null)
        {
            return true;
        }

        return order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? order.LimitPrice < preTradeDepth.BestAsk
            : order.LimitPrice > preTradeDepth.BestBid;
    }

    private FillComputation FillAgainstDepth(Order order, List<OrderLifecycleEvent> lifecycle, List<ExecutionReport> reports, List<Fill> fills)
    {
        lock (_sync)
        {
            var book = GetDepthBookUnsafe(order.Symbol);
            var levels = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? book.Asks : book.Bids;
            var remaining = order.RemainingQuantity > 0 ? order.RemainingQuantity : order.Quantity;
            var filled = order.FilledQuantity;
            var notional = (order.AverageFillPrice ?? 0m) * filled;

            for (var i = 0; i < levels.Count && remaining > 0;)
            {
                var level = levels[i];
                if (!CanTradeAtLevel(order, level.Price))
                {
                    break;
                }

                var quantity = Math.Min(remaining, level.Quantity);
                var fillOwner = order.UseMarketMakerLiquidity ? "MarketMaker" : order.Owner;
                var fill = new Fill(Guid.NewGuid(), order.OrderId, order.Symbol, order.Side, quantity, level.Price, DateTime.UtcNow, fillOwner);
                fills.Add(fill);
                remaining -= quantity;
                filled += quantity;
                notional += quantity * level.Price;
                var avgPrice = filled == 0 ? 0 : notional / filled;

                AddLifecycle(lifecycle, order, "Fill", $"Filled {quantity:N0} @ {level.Price:N2}", quantity, level.Price);
                reports.Add(NewReport(order, "PartiallyFilled", $"Filled {quantity:N0} @ {level.Price:N2}", fill, filled, remaining, avgPrice));

                var remainingAtLevel = level.Quantity - quantity;
                if (remainingAtLevel <= 0)
                {
                    levels.RemoveAt(i);
                }
                else
                {
                    levels[i] = level with { Quantity = remainingAtLevel };
                    i++;
                }
            }

            ReplenishDepthUnsafe(book);
            return new FillComputation(filled, remaining, filled == 0 ? null : notional / filled);
        }
    }

    private void ApplyMarketMakerInventory(Fill fill)
    {
        if (!fill.Owner.Equals("MarketMaker", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var signedQuantity = fill.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? -fill.Quantity : fill.Quantity;
        lock (_sync)
        {
            _marketMakerInventoryBySymbol[fill.Symbol] = GetMarketMakerInventoryUnsafe(fill.Symbol) + signedQuantity;
        }
    }

    private int GetMarketMakerInventory(string symbol)
    {
        lock (_sync)
        {
            return GetMarketMakerInventoryUnsafe(symbol);
        }
    }

    private int GetMarketMakerInventoryUnsafe(string symbol) =>
        _marketMakerInventoryBySymbol.TryGetValue(symbol, out var inventory) ? inventory : 0;

    private static bool CanTradeAtLevel(Order order, decimal price)
    {
        if (order.OrderType.Equals("Market", StringComparison.OrdinalIgnoreCase) || order.LimitPrice is null)
        {
            return true;
        }

        return order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? price <= order.LimitPrice
            : price >= order.LimitPrice;
    }

    private OrderResult BuildResult(Order order, List<ExecutionReport> reports, List<Fill> fills, Position? position, List<OrderLifecycleEvent> lifecycle, DepthSnapshot depth, LatencyBreakdown? latency = null) =>
        new(order, reports, fills.LastOrDefault(), position, lifecycle, fills, fills, depth, GetExecutionStats(), latency);

    private DepthSnapshot GetDepthUnsafe(string symbol)
    {
        var book = GetDepthBookUnsafe(symbol);
        TickDepthUnsafe(book);
        var snapshot = ToDepthSnapshot(book);
        _positionManager.UpdateMark(snapshot.Symbol, snapshot.MidPrice);
        return snapshot;
    }

    private DepthBookState GetDepthBookUnsafe(string symbol)
    {
        symbol = string.IsNullOrWhiteSpace(symbol) ? "ES" : symbol.ToUpperInvariant();
        if (_depthBySymbol.TryGetValue(symbol, out var book))
        {
            return book;
        }

        book = CreateDepthBook(symbol);
        _depthBySymbol[symbol] = book;
        return book;
    }

    private static DepthBookState CreateDepthBook(string symbol)
    {
        var mid = GetReferencePrice(symbol);
        var tick = symbol == "ES" ? EsTickSize : Math.Max(0.01m, Math.Round(mid * 0.0005m, 2));
        return new DepthBookState(
            Symbol: symbol,
            TickSize: tick,
            Bids:
            [
                new DepthLevel(mid - tick, 60),
                new DepthLevel(mid - tick * 2, 40),
                new DepthLevel(mid - tick * 3, 75),
                new DepthLevel(mid - tick * 4, 90),
                new DepthLevel(mid - tick * 5, 120)
            ],
            Asks:
            [
                new DepthLevel(mid + tick, 30),
                new DepthLevel(mid + tick * 2, 45),
                new DepthLevel(mid + tick * 3, 25),
                new DepthLevel(mid + tick * 4, 60),
                new DepthLevel(mid + tick * 5, 100)
            ]);
    }

    private static void TickDepthUnsafe(DepthBookState book)
    {
        book.TickCount++;

        for (var i = 0; i < book.Asks.Count; i++)
        {
            var delta = (int)((book.TickCount + i) % 3) - 1;
            book.Asks[i] = book.Asks[i] with { Quantity = Math.Max(10, book.Asks[i].Quantity + delta * 2) };
        }

        for (var i = 0; i < book.Bids.Count; i++)
        {
            var delta = (int)((book.TickCount + i + 1) % 3) - 1;
            book.Bids[i] = book.Bids[i] with { Quantity = Math.Max(10, book.Bids[i].Quantity + delta * 3) };
        }

        if (book.TickCount % 8 == 0)
        {
            ShiftMidUnsafe(book, book.TickCount % 16 == 0 ? -book.TickSize : book.TickSize);
        }
    }

    private static void ShiftMidUnsafe(DepthBookState book, decimal delta)
    {
        book.MidPrice += delta;
        for (var i = 0; i < book.Asks.Count; i++)
        {
            book.Asks[i] = book.Asks[i] with { Price = book.Asks[i].Price + delta };
        }

        for (var i = 0; i < book.Bids.Count; i++)
        {
            book.Bids[i] = book.Bids[i] with { Price = book.Bids[i].Price + delta };
        }
    }

    private static void ReplenishDepthUnsafe(DepthBookState book)
    {
        while (book.Asks.Count < 5)
        {
            var nextPrice = book.Asks.Count == 0 ? book.MidPrice + book.TickSize : book.Asks[^1].Price + book.TickSize;
            book.Asks.Add(new DepthLevel(nextPrice, NextAskSize(book.Asks.Count)));
        }

        while (book.Bids.Count < 5)
        {
            var nextPrice = book.Bids.Count == 0 ? book.MidPrice - book.TickSize : book.Bids[^1].Price - book.TickSize;
            book.Bids.Add(new DepthLevel(nextPrice, NextBidSize(book.Bids.Count)));
        }
    }

    private static int NextAskSize(int index) => new[] { 60, 100, 80, 120, 150 }[Math.Min(index, 4)];

    private static int NextBidSize(int index) => new[] { 75, 90, 120, 140, 160 }[Math.Min(index, 4)];

    private static DepthSnapshot ToDepthSnapshot(DepthBookState book) => new(
        Symbol: book.Symbol,
        MidPrice: book.MidPrice,
        BestBid: book.Bids.Count > 0 ? book.Bids[0].Price : book.MidPrice - book.TickSize,
        BestAsk: book.Asks.Count > 0 ? book.Asks[0].Price : book.MidPrice + book.TickSize,
        Bids: book.Bids.ToArray(),
        Asks: book.Asks.ToArray(),
        Timestamp: DateTime.UtcNow);

    private void Save(Order order, IReadOnlyList<ExecutionReport> reports, IReadOnlyList<OrderLifecycleEvent> lifecycle, double? latencyMs = null)
    {
        lock (_sync)
        {
            foreach (var report in reports)
            {
                _executions.Add(report);
            }
            foreach (var evt in lifecycle)
            {
                AddLifecycleUnsafe(evt);
            }
            SaveOrderUnsafe(order);
            if (latencyMs is not null)
            {
                _latenciesMs.Add(latencyMs.Value);
            }
        }
    }

    private void Save(Order order, ExecutionReport report, OrderLifecycleEvent evt, double? latencyMs = null)
    {
        lock (_sync)
        {
            SaveUnsafe(order, report, evt);
            if (latencyMs is not null)
            {
                _latenciesMs.Add(latencyMs.Value);
            }
        }
    }

    private void SaveUnsafe(Order order, ExecutionReport report, OrderLifecycleEvent evt)
    {
        _executions.Add(report);
        AddLifecycleUnsafe(evt);
        SaveOrderUnsafe(order);
    }

    private void SaveOrderUnsafe(Order order)
    {
        var index = _orders.FindIndex(o => o.OrderId == order.OrderId);
        if (index >= 0)
        {
            _orders[index] = order;
        }
        else
        {
            _orders.Add(order);
        }
    }

    private void AddLifecycleUnsafe(OrderLifecycleEvent evt)
    {
        if (!_lifecycleByOrderId.TryGetValue(evt.OrderId, out var events))
        {
            events = [];
            _lifecycleByOrderId[evt.OrderId] = events;
        }
        events.Add(evt);
    }

    private void AddLifecycle(List<OrderLifecycleEvent> lifecycle, Order order, string stage, string message, int? quantity = null, decimal? price = null) =>
        lifecycle.Add(NewLifecycle(order, stage, message, quantity, price));

    private static OrderLifecycleEvent NewLifecycle(Order order, string stage, string message, int? quantity = null, decimal? price = null) =>
        new(Guid.NewGuid(), order.OrderId, stage, message, DateTime.UtcNow, quantity, price);

    private static LatencyBreakdown BuildLatency(double riskMs, double routeMs, double exchangeMs, double fillMs, double totalMs)
    {
        var otherMs = Math.Max(0, totalMs - riskMs - routeMs - exchangeMs - fillMs);
        return new LatencyBreakdown(riskMs, routeMs, exchangeMs, fillMs, otherMs, totalMs);
    }

    private static ExecutionReport NewReport(Order order, string status, string message, Fill? fill = null, int cumQuantity = 0, int leavesQuantity = 0, decimal? averageFillPrice = null) => new(
        ExecutionReportId: Guid.NewGuid(),
        OrderId: order.OrderId,
        Symbol: order.Symbol,
        Status: status,
        Message: message,
        Timestamp: DateTime.UtcNow,
        Fill: fill,
        CumQuantity: cumQuantity,
        LeavesQuantity: leavesQuantity,
        AverageFillPrice: averageFillPrice);

    private IReadOnlyList<OrderLifecycleEvent> GetLifecycleUnsafe(Guid orderId) =>
        _lifecycleByOrderId.TryGetValue(orderId, out var events) ? events.ToArray() : [];

    private ExecutionStats GetExecutionStatsUnsafe()
    {
        var filledOrders = _orders.Count(o => o.Status == "Filled");
        var cancels = _orders.Count(o => o.Status == "Canceled");
        var rejections = _orders.Count(o => o.Status == "Rejected");
        var fills = _executions.Select(e => e.Fill).OfType<Fill>().ToArray();
        var totalQty = fills.Sum(f => f.Quantity);
        var grossNotional = fills.Sum(f => f.Quantity * f.Price);
        var avgFill = totalQty == 0 ? 0 : grossNotional / totalQty;
        var avgLatency = _latenciesMs.Count == 0 ? 0 : _latenciesMs.Average();
        var ordersSent = _orders.Count;

        return new ExecutionStats(
            OrdersSent: ordersSent,
            OrdersFilled: filledOrders,
            OpenOrders: _orders.Count(IsOpen),
            Cancels: cancels,
            Rejections: rejections,
            TotalFilledQuantity: totalQty,
            GrossNotional: grossNotional,
            AverageFillPrice: avgFill,
            FillRatio: ordersSent == 0 ? 0 : (double)filledOrders / ordersSent * 100.0,
            AverageLatencyMs: avgLatency,
            PnL: _positionManager.GetPositions().Sum(p => p.UnrealizedPnl),
            UpdatedAt: DateTime.UtcNow);
    }

    private static bool IsOpen(Order order) =>
        order.RemainingQuantity > 0 && (order.Status is "New" or "Accepted" or "Working" or "PartiallyFilled");

    private static string FormatOrderPrice(Order order) =>
        order.OrderType.Equals("Market", StringComparison.OrdinalIgnoreCase)
            ? "Market"
            : order.LimitPrice?.ToString("N2") ?? "Limit";

    private static string RouteFor(string symbol) => symbol.Equals("ES", StringComparison.OrdinalIgnoreCase) ? "CME" : "SIMEX";

    private static decimal GetReferencePrice(string symbol) => symbol.ToUpperInvariant() switch
    {
        "ES" => 5982.00m,
        "NQ" => 21850.50m,
        "AAPL" => 195.50m,
        "MSFT" => 430.20m,
        "NVDA" => 950.00m,
        "BTC-USD" => 68500.00m,
        _ => 100.00m
    };

    private sealed record FillComputation(int FilledQuantity, int RemainingQuantity, decimal? AverageFillPrice);

    private sealed record DepthBookState(
        string Symbol,
        decimal TickSize,
        List<DepthLevel> Bids,
        List<DepthLevel> Asks
    )
    {
        public decimal MidPrice { get; set; } = GetReferencePrice(Symbol);
        public long TickCount { get; set; }
    }
}
