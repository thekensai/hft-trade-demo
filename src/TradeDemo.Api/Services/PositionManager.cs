using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

public class PositionManager
{
    private const decimal ES_CONTRACT_MULTIPLIER = 50m; // $50 per point per ES contract
    private readonly ConcurrentDictionary<string, PositionState> _positions = new();
    private readonly object _sync = new();

    // Backward compatible overload for existing call
    public Position ApplyFill(Fill fill) => ApplyFill(fill.Symbol, SignedQuantity(fill), fill.Price);

    public Position ApplyFills(IReadOnlyList<Fill> fills)
    {
        var first = fills[0];
        var quantity = 0;
        var notional = 0m;
        for (var i = 0; i < fills.Count; i++)
        {
            quantity += fills[i].Quantity;
            notional += fills[i].Quantity * fills[i].Price;
        }

        var averagePrice = notional / quantity;
        var signedQuantity = first.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase) ? -quantity : quantity;

        return ApplyFill(first.Symbol, signedQuantity, averagePrice);
    }

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
            if (!_positions.TryGetValue(symbol, out var current))
            {
                return ToPosition(symbol, new PositionState { MarkPrice = markPrice });
            }

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

    public void Reset()
    {
        lock (_sync)
        {
            _positions.Clear();
        }
    }

    private static int SignedQuantity(Fill fill) =>
        fill.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase) ? -fill.Quantity : fill.Quantity;

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

    private static Position ToPosition(string symbol, PositionState state) =>
        new(
            Symbol: symbol,
            Quantity: state.Quantity,
            AveragePrice: state.AveragePrice,
            MarkPrice: state.MarkPrice,
            RealizedPnl: state.RealizedPnl,
            UnrealizedPnl: (state.MarkPrice - state.AveragePrice) * state.Quantity * ES_CONTRACT_MULTIPLIER,
            UpdatedAt: state.UpdatedAt);

    private sealed record PositionState
    {
        public int Quantity { get; init; }
        public decimal AveragePrice { get; init; }
        public decimal MarkPrice { get; init; }
        public decimal RealizedPnl { get; init; }
        public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    }
}
