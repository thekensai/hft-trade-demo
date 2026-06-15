using TradeDemo.Api.Models;

namespace TradeDemo.Api.Services;

/// <summary>
/// Lightweight in-memory pub/sub bus. Subscribers keep their own channel and
/// backpressure semantics; the hot path only fans out to the fixed subscriber set.
/// </summary>
public sealed class InMemoryMarketDataBus : IMarketDataBus
{
    private readonly IMarketDataSubscriber[] _subscribers;

    public InMemoryMarketDataBus(IEnumerable<IMarketDataSubscriber> subscribers)
    {
        _subscribers = subscribers.ToArray();
    }

    public void Publish(TradeSignal signal)
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.OnMarketData(signal);
        }
    }
}
