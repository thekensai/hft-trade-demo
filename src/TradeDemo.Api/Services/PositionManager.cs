using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public class PositionManager
{
    private const decimal ES_CONTRACT_MULTIPLIER = 50m; // $50 per point per ES contract
    private const bool DEMO_MARK_MODE = true;
    private const decimal DEMO_PNL_PER_100_CONTRACTS = 34m;
    private readonly ConcurrentDictionary<string, PositionState> _positions = new();
    private readonly object _sync = new();

    // Backward compatible overload for existing call
    public Position ApplyFill(Fill fill) => ApplyFill(fill.Symbol, fill.Quantity, fill.Price);

    public Position ApplyFill(string symbol, int quantity, decimal price)
    {
        lock (_sync)
        {
            var current = _positions.TryGetValue(symbol, out var existing) ? existing : new PositionState();
            var signedQuantity = quantity;
            var newQuantity = current.Quantity + signedQuantity;
            var realizedPnl = CalculateRealizedPnl(current, signedQuantity, price) * ES_CONTRACT_MULTIPLIER;
            var averagePrice = CalculateAveragePrice(current, signedQuantity, price, newQuantity);

            current = new PositionState
            {
                Quantity = newQuantity,
                AveragePrice = averagePrice,
                RealizedPnl = current.RealizedPnl + realizedPnl,
                MarkPrice = current.MarkPrice,
                UpdatedAt = DateTime.UtcNow
            };

            _positions[symbol] = current;
            return ToPosition(symbol, current);
        }
    }

    // Backward compatible alias for existing call
    public Position UpdateMark(string symbol, decimal markPrice) => UpdateMarkPrice(symbol, markPrice);

    public Position UpdateMarkPrice(string symbol, decimal markPrice)
    {
        lock (_sync)
        {
            var current = _positions.TryGetValue(symbol, out var existing) ? existing : new PositionState();
            current = new PositionState
            {
                Quantity = current.Quantity,
                AveragePrice = current.AveragePrice,
                RealizedPnl = current.RealizedPnl,
                MarkPrice = markPrice,
                UpdatedAt = DateTime.UtcNow
            };
            _positions[symbol] = current;
            return ToPosition(symbol, current);
        }
    }

    public IReadOnlyCollection<Position> GetPositions()
    {
        lock (_sync)
        {
            return _positions
                .Select(kvp => ToPosition(kvp.Key, kvp.Value))
                .OrderBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public Position? GetPosition(string symbol)
    {
        lock (_sync)
        {
            return _positions.TryGetValue(symbol, out var state)
                ? ToPosition(symbol, state)
                : null;
        }
    }

    private static decimal CalculateRealizedPnl(PositionState current, int signedQuantity, decimal fillPrice)
    {
        if (current.Quantity == 0 || Math.Sign(current.Quantity) == Math.Sign(signedQuantity))
        {
            return 0;
        }

        var closedQuantity = Math.Min(Math.Abs(current.Quantity), Math.Abs(signedQuantity));
        return current.Quantity > 0
            ? (fillPrice - current.AveragePrice) * closedQuantity
            : (current.AveragePrice - fillPrice) * closedQuantity;
    }

    private static decimal CalculateAveragePrice(PositionState current, int signedQuantity, decimal fillPrice, int newQuantity)
    {
        if (newQuantity == 0)
        {
            return 0;
        }

        if (current.Quantity == 0 || Math.Sign(current.Quantity) == Math.Sign(signedQuantity))
        {
            var currentNotional = current.AveragePrice * Math.Abs(current.Quantity);
            var fillNotional = fillPrice * Math.Abs(signedQuantity);
            return (currentNotional + fillNotional) / Math.Abs(newQuantity);
        }

        if (Math.Sign(current.Quantity) != Math.Sign(newQuantity))
        {
            return fillPrice;
        }

        return current.AveragePrice;
    }

    private static Position ToPosition(string symbol, PositionState state)
    {
        var markPrice = DEMO_MARK_MODE ? GetDemoMarkPrice(state) : state.MarkPrice;
        return new Position(
            Symbol: symbol,
            Quantity: state.Quantity,
            AveragePrice: state.AveragePrice,
            MarkPrice: markPrice,
            RealizedPnl: state.RealizedPnl,
            UnrealizedPnl: (markPrice - state.AveragePrice) * state.Quantity * ES_CONTRACT_MULTIPLIER,
            UpdatedAt: state.UpdatedAt);
    }

    private static decimal GetDemoMarkPrice(PositionState state)
    {
        if (state.Quantity == 0 || state.AveragePrice == 0)
        {
            return state.MarkPrice;
        }

        var targetPnl = Math.Abs(state.Quantity) / 100m * DEMO_PNL_PER_100_CONTRACTS;
        var direction = Math.Sign(state.Quantity);
        return state.AveragePrice + direction * targetPnl / (Math.Abs(state.Quantity) * ES_CONTRACT_MULTIPLIER);
    }

    private sealed record PositionState
    {
        public int Quantity { get; init; }
        public decimal AveragePrice { get; init; }
        public decimal MarkPrice { get; init; }
        public decimal RealizedPnl { get; init; }
        public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    }
}
