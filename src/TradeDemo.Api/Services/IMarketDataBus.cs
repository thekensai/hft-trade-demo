using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

/// <summary>
/// Publishes market data signals to subscribed in-process consumers.
/// </summary>
public interface IMarketDataBus
{
    void Publish(TradeSignal signal);
}

/// <summary>
/// Receives market data signals from the in-memory bus.
/// </summary>
public interface IMarketDataSubscriber
{
    void OnMarketData(TradeSignal signal);
}
