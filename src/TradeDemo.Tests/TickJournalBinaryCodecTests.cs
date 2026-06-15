using TradeDemo.Api.Journal;
using TradeDemo.Api.Models;

namespace TradeDemo.Tests;

public sealed class TickJournalBinaryCodecTests
{
    [Fact]
    public void SegmentHeader_RoundTrips()
    {
        var createdTicks = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks;
        using var stream = new MemoryStream();

        TickJournalBinaryCodec.WriteSegmentHeader(stream, createdTicks, firstSequenceId: 42);
        stream.Position = 0;

        var read = TickJournalBinaryCodec.TryReadSegmentHeader(stream, out var header);

        Assert.True(read);
        Assert.Equal(createdTicks, header.CreatedUtcTicks);
        Assert.Equal(42, header.FirstSequenceId);
    }

    [Fact]
    public void EncodeRecord_RoundTripsTradeSignal()
    {
        var signal = CreateSignal();
        var record = TickJournalBinaryCodec.EncodeRecord(signal);
        using var stream = new MemoryStream(record);

        var read = TickJournalBinaryCodec.TryReadRecord(stream, out var decoded);

        Assert.True(read);
        Assert.Equal(signal, decoded);
    }

    [Fact]
    public void EncodePayload_RoundTripsTradeSignal()
    {
        var signal = CreateSignal();
        var payload = TickJournalBinaryCodec.EncodePayload(signal);

        var decoded = TickJournalBinaryCodec.DecodePayload(payload);

        Assert.Equal(signal, decoded);
    }

    [Fact]
    public void TryReadRecord_ReturnsFalseWhenPayloadChecksumDoesNotMatch()
    {
        var record = TickJournalBinaryCodec.EncodeRecord(CreateSignal());
        record[8] ^= 0xFF;
        using var stream = new MemoryStream(record);

        var read = TickJournalBinaryCodec.TryReadRecord(stream, out var decoded);

        Assert.False(read);
        Assert.Equal(default, decoded);
    }

    [Fact]
    public void TryReadSegmentHeader_ReturnsFalseForInvalidHeader()
    {
        using var stream = new MemoryStream(new byte[TickJournalBinaryCodec.SegmentHeaderLength]);

        var read = TickJournalBinaryCodec.TryReadSegmentHeader(stream, out var header);

        Assert.False(read);
        Assert.Equal(default, header);
    }

    private static TradeSignal CreateSignal() => new(
        Symbol: "ES",
        BidPrice: 5981.75m,
        AskPrice: 5982.25m,
        MidPrice: 5982.00m,
        Change: 1.25m,
        ChangePercent: 0.021,
        Volume: 123_456,
        Exchange: "CME",
        Timestamp: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Direction: "BUY",
        SequenceId: 42);
}
