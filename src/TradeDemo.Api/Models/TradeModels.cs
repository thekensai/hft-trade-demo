namespace TradeDemo.Api.Models;

public record TradeSignal(
    string Symbol,
    decimal Price,
    decimal Change,
    double ChangePercent,
    long Volume,
    string Exchange,
    DateTime Timestamp,
    string Direction // "BUY" | "SELL"
);

public record OrderBookEntry(
    string Symbol,
    decimal BidPrice,
    decimal AskPrice,
    long BidSize,
    long AskSize,
    DateTime Timestamp
);

public record MarketEvent(
    string EventType,  // "TRADE" | "QUOTE" | "SIGNAL"
    string Symbol,
    object Payload,
    DateTime Timestamp
);
