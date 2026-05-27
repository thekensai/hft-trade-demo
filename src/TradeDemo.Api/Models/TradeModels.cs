namespace TradeDemo.Api.Models;

public record TradeSignal(
    string Symbol,
    decimal BidPrice,
    decimal AskPrice,
    decimal MidPrice,
    decimal Change,
    double ChangePercent,
    long Volume,
    string Exchange,
    DateTime Timestamp,
    string Direction, // "BUY" | "SELL"
    long SequenceId
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
