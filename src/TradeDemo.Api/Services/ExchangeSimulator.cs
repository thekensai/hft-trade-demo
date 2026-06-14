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
    private readonly Dictionary<Guid, OrderTelemetry> _telemetryByOrderId = [];
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
        var arrivalPrice = preTradeDepth.MidPrice;
        var lifecycle = new List<OrderLifecycleEvent>();
        var reports = new List<ExecutionReport>();
        var fills = new List<Fill>();
        Position? position = null;
        double riskMs = 0;
        double routeMs = 0;
        double exchangeMs = 0;
        double fillMs = 0;

        AddLifecycle(lifecycle, order, "Submitted", $"Order Submitted {order.Side} {order.Quantity:N0} {order.Symbol} @ {FormatOrderPrice(order)}");

        await Task.Delay(SimulatedHop, ct);
        var referencePrice = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? preTradeDepth.BestAsk : preTradeDepth.BestBid;
        var currentPosition = _positionManager.GetPosition(order.Symbol)?.Quantity ?? 0;
        var risk = _riskEngine.Check(order, referencePrice, currentPosition);
        riskMs = SimulatedLatency(order.OrderId, 1, 0.4, 1.2);
        if (!risk.IsAccepted)
        {
            var rejected = order with
            {
                Status = "Rejected",
                RemainingQuantity = order.Quantity,
                UpdatedAt = DateTime.UtcNow,
                RejectReason = risk.RejectReason
            };
            AddLifecycle(lifecycle, rejected, "Risk Reject", risk.RejectReason ?? "Risk check rejected order");
            reports.Add(NewReport(rejected, "Rejected", risk.RejectReason ?? "Risk check rejected order"));
            var rejectedLatency = BuildLatency(order.OrderId, riskMs, routeMs, exchangeMs, fillMs);
            Save(rejected, reports, lifecycle, sw.Elapsed.TotalMilliseconds, "RISK", rejectedLatency);
            return BuildResult(rejected, reports, fills, position, lifecycle, preTradeDepth, rejectedLatency);
        }

        AddLifecycle(lifecycle, order, "Risk Check Passed", $"Risk Check Passed ({riskMs:F1}ms)");

        await Task.Delay(SimulatedHop, ct);
        routeMs = SimulatedLatency(order.OrderId, 2, 1.4, 3.2);
        var venue = RouteFor(order.Symbol);

        AddLifecycle(lifecycle, order, "Routed", $"Routed to {venue} ({routeMs:F1}ms)");

        await Task.Delay(SimulatedHop, ct);
        exchangeMs = SimulatedLatency(order.OrderId, 3, 3.5, 9.5);

        var accepted = order with
        {
            Status = "Accepted",
            RemainingQuantity = order.Quantity,
            UpdatedAt = DateTime.UtcNow
        };
        AddLifecycle(lifecycle, accepted, "Accepted", $"Accepted ({exchangeMs:F1}ms)");
        reports.Add(NewReport(accepted, "Accepted", "Order accepted by exchange simulator"));

        await Task.Delay(SimulatedHop, ct);

        if (CanRest(accepted, preTradeDepth))
        {
            var working = accepted with { Status = "Working", UpdatedAt = DateTime.UtcNow };
            AddLifecycle(lifecycle, working, "Working", $"Resting {working.Side} {working.RemainingQuantity:N0} {working.Symbol} @ {FormatOrderPrice(working)}");
            reports.Add(NewReport(working, "Working", "Order resting in open-orders book"));
            var workingLatency = BuildLatency(order.OrderId, riskMs, routeMs, exchangeMs, fillMs);
            Save(working, reports, lifecycle, sw.Elapsed.TotalMilliseconds, venue, workingLatency);
            return BuildResult(working, reports, fills, position, lifecycle, preTradeDepth, workingLatency);
        }

        var fillResult = FillAgainstDepth(accepted, lifecycle, reports, fills);
        if (fills.Count > 0)
        {
            position = _positionManager.ApplyFills(fills);
        }
        foreach (var fill in fills)
        {
            ApplyMarketMakerInventory(fill);
            await Task.Delay(SimulatedHop, ct);
        }
        fillMs = SimulatedLatency(order.OrderId, 4, 18.0, 55.0);

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
            filledOrder.Status == "Filled" ? $"Order Fully Filled ({fillMs:F1}ms)" : $"Working {filledOrder.RemainingQuantity:N0} remaining");
        reports.Add(NewReport(filledOrder, filledOrder.Status, lifecycle[^1].Message, null, filledOrder.FilledQuantity, filledOrder.RemainingQuantity, filledOrder.AverageFillPrice));
        var latency = BuildLatency(order.OrderId, riskMs, routeMs, exchangeMs, fillMs);
        Save(filledOrder, reports, lifecycle, sw.Elapsed.TotalMilliseconds, venue, latency);

        var depth = GetDepth(filledOrder.Symbol);
        if (position is not null)
        {
            position = _positionManager.GetPosition(filledOrder.Symbol);
        }

        return BuildResult(filledOrder, reports, fills, position, lifecycle, depth, latency, BuildSlippage(filledOrder, fillResult, arrivalPrice));
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

    public IReadOnlyList<TradeMonitorRow> GetTradeMonitor()
    {
        lock (_sync)
        {
            return _orders
                .OrderByDescending(o => o.UpdatedAt ?? o.CreatedAt)
                .Select(ToTradeMonitorRowUnsafe)
                .ToArray();
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
            BidEnabled: inventory < MarketMakerInventoryLimit,
            AskEnabled: inventory > -MarketMakerInventoryLimit,
            UpdatedAt: DateTime.UtcNow);
    }

    public IReadOnlyList<OrderLifecycleEvent> GetLifecycle(Guid orderId)
    {
        lock (_sync)
        {
            return GetLifecycleUnsafe(orderId);
        }
    }

    public void ResetDemo()
    {
        lock (_sync)
        {
            _orders.Clear();
            _executions.Clear();
            _lifecycleByOrderId.Clear();
            _depthBySymbol.Clear();
            _marketMakerInventoryBySymbol.Clear();
            _telemetryByOrderId.Clear();
            _latenciesMs.Clear();
            _positionManager.Reset();
        }
    }

    public IReadOnlyList<OrderLifecycleEvent> GetRecentLifecycleEvents(int count = 80)
    {
        lock (_sync)
        {
            return _lifecycleByOrderId.Values
                .SelectMany(events => events)
                .OrderByDescending(evt => evt.Timestamp)
                .Take(Math.Clamp(count, 1, 500))
                .OrderBy(evt => evt.Timestamp)
                .ToArray();
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
            var notedMarketMakerUnavailable = false;

            for (var i = 0; i < levels.Count && remaining > 0;)
            {
                var level = levels[i];
                if (!CanTradeAtLevel(order, level.Price))
                {
                    break;
                }

                var quantity = Math.Min(remaining, level.Quantity);
                var useMarketMaker = order.UseMarketMakerLiquidity && CanUseMarketMakerLiquidityUnsafe(order, quantity);
                if (order.UseMarketMakerLiquidity && !useMarketMaker && !notedMarketMakerUnavailable)
                {
                    AddLifecycle(lifecycle, order, "MM Quote Disabled", $"MM {MarketMakerQuoteSide(order)} disabled — routed to regular book");
                    notedMarketMakerUnavailable = true;
                }

                var fillOwner = useMarketMaker ? "MarketMaker" : order.Owner;
                book.Sequence++;
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

    private bool CanUseMarketMakerLiquidityUnsafe(Order order, int quantity)
    {
        var inventory = GetMarketMakerInventoryUnsafe(order.Symbol);
        return order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? inventory - quantity >= -MarketMakerInventoryLimit
            : inventory + quantity <= MarketMakerInventoryLimit;
    }

    private static string MarketMakerQuoteSide(Order order) =>
        order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "ask" : "bid";

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

    private OrderResult BuildResult(Order order, List<ExecutionReport> reports, List<Fill> fills, Position? position, List<OrderLifecycleEvent> lifecycle, DepthSnapshot depth, LatencyBreakdown? latency = null, SlippageMetrics? slippage = null) =>
        new(order, reports, fills.LastOrDefault(), position, lifecycle, fills, fills, depth, GetExecutionStats(), latency, slippage);

    private TradeMonitorRow ToTradeMonitorRowUnsafe(Order order)
    {
        _telemetryByOrderId.TryGetValue(order.OrderId, out var telemetry);
        var latencyMs = telemetry?.Latency?.TotalMs;
        var fillPercent = order.Quantity <= 0 ? 0 : (double)order.FilledQuantity / order.Quantity * 100.0;
        var pnl = CalculateOrderPnlUnsafe(order);
        var updatedAt = order.UpdatedAt ?? order.CreatedAt;

        return new TradeMonitorRow(
            OrderId: order.OrderId,
            Symbol: order.Symbol,
            Status: order.Status,
            Venue: telemetry?.Venue ?? RouteFor(order.Symbol),
            LatencyMs: latencyMs,
            FillPercent: fillPercent,
            PnL: pnl,
            Health: DetermineHealth(order, latencyMs, updatedAt),
            UpdatedAt: updatedAt);
    }

    private decimal CalculateOrderPnlUnsafe(Order order)
    {
        if (order.FilledQuantity <= 0 || order.AverageFillPrice is null)
        {
            return 0m;
        }

        var mark = GetDepthUnsafe(order.Symbol).MidPrice;
        var direction = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
        return (mark - order.AverageFillPrice.Value) * order.FilledQuantity * ContractMultiplier(order.Symbol) * direction;
    }

    private static string DetermineHealth(Order order, double? latencyMs, DateTime updatedAt)
    {
        if (order.Status is "Rejected" or "CancelRejected" or "ModifyRejected")
        {
            return "Abnormal";
        }

        if (latencyMs >= 75)
        {
            return "Abnormal";
        }

        if (latencyMs >= 50 || order.Status == "PartiallyFilled" || (IsOpen(order) && DateTime.UtcNow - updatedAt > TimeSpan.FromSeconds(30)))
        {
            return "Slow";
        }

        return "Healthy";
    }

    private static SlippageMetrics? BuildSlippage(Order order, FillComputation fillResult, decimal arrivalPrice)
    {
        if (fillResult.AverageFillPrice is null || fillResult.FilledQuantity == 0)
        {
            return null;
        }

        var averageFillPrice = fillResult.AverageFillPrice.Value;
        var slippagePoints = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? averageFillPrice - arrivalPrice
            : arrivalPrice - averageFillPrice;
        var slippageDollars = slippagePoints * fillResult.FilledQuantity * ContractMultiplier(order.Symbol);

        return new SlippageMetrics(arrivalPrice, averageFillPrice, slippagePoints, slippageDollars, order.Symbol);
    }

    private static decimal ContractMultiplier(string symbol) =>
        symbol.Equals("ES", StringComparison.OrdinalIgnoreCase) || symbol.Equals("NQ", StringComparison.OrdinalIgnoreCase) ? 50m : 1m;

    private DepthSnapshot GetDepthUnsafe(string symbol)
    {
        var snapshot = ToDepthSnapshot(GetDepthBookUnsafe(symbol));
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
        Timestamp: DateTime.UtcNow,
        Sequence: book.Sequence);

    private void Save(Order order, IReadOnlyList<ExecutionReport> reports, IReadOnlyList<OrderLifecycleEvent> lifecycle, double? latencyMs = null, string? venue = null, LatencyBreakdown? latency = null)
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
            SaveTelemetryUnsafe(order, venue, latency);
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
        SaveTelemetryUnsafe(order, null, null);
    }

    private void SaveTelemetryUnsafe(Order order, string? venue, LatencyBreakdown? latency)
    {
        if (_telemetryByOrderId.TryGetValue(order.OrderId, out var existing))
        {
            _telemetryByOrderId[order.OrderId] = existing with
            {
                Venue = venue ?? existing.Venue,
                Latency = latency ?? existing.Latency,
                UpdatedAt = DateTime.UtcNow
            };
            return;
        }

        _telemetryByOrderId[order.OrderId] = new OrderTelemetry(venue ?? RouteFor(order.Symbol), latency, DateTime.UtcNow);
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
        new(Guid.NewGuid(), order.OrderId, stage, message, DateTime.UtcNow, quantity, price, order.Symbol);

    private static LatencyBreakdown BuildLatency(Guid orderId, double riskMs, double routeMs, double exchangeMs, double fillMs)
    {
        var otherMs = SimulatedLatency(orderId, 5, 0.3, 2.4);
        return new LatencyBreakdown(riskMs, routeMs, exchangeMs, fillMs, otherMs, riskMs + routeMs + exchangeMs + fillMs + otherMs);
    }

    private static double SimulatedLatency(Guid orderId, int stage, double minMs, double maxMs)
    {
        var hash = HashCode.Combine(orderId, stage);
        var unit = (hash & 0x7fffffff) / (double)int.MaxValue;
        return minMs + unit * (maxMs - minMs);
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

    private static string RouteFor(string symbol) => symbol.ToUpperInvariant() switch
    {
        "ES" or "NQ" => "CME",
        "AAPL" or "MSFT" or "GOOGL" or "AMZN" or "TSLA" or "NVDA" or "META" => "NASDAQ",
        "JPM" or "GS" or "BAC" or "V" or "BRK.B" => "NYSE",
        _ => "SIMEX"
    };

    private static decimal GetReferencePrice(string symbol) => symbol.ToUpperInvariant() switch
    {
        "ES" => 5982.00m,
        "NQ" => 21850.50m,
        "AAPL" => 195.50m,
        "MSFT" => 430.20m,
        "GOOGL" => 178.90m,
        "AMZN" => 185.60m,
        "TSLA" => 250.10m,
        "JPM" => 198.30m,
        "GS" => 465.80m,
        "BAC" => 38.90m,
        "V" => 278.40m,
        "BRK.B" => 415.70m,
        "NVDA" => 950.00m,
        "META" => 480.30m,
        "BTC-USD" => 68500.00m,
        _ => 100.00m
    };

    private sealed record FillComputation(int FilledQuantity, int RemainingQuantity, decimal? AverageFillPrice);

    private sealed record OrderTelemetry(string Venue, LatencyBreakdown? Latency, DateTime UpdatedAt);

    private sealed record DepthBookState(
        string Symbol,
        decimal TickSize,
        List<DepthLevel> Bids,
        List<DepthLevel> Asks
    )
    {
        public decimal MidPrice { get; set; } = GetReferencePrice(Symbol);
        public long TickCount { get; set; }
        public long Sequence { get; set; }
    }
}
