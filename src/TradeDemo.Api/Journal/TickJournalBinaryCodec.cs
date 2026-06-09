using System.Buffers.Binary;
using System.Text;
using TradeDemo.Api.Models;

namespace TradeDemo.Api.Journal;

public static class TickJournalBinaryCodec
{
    public const uint SegmentMagic = 0x314A4454; // "TDJ1" little-endian
    public const byte SegmentVersion = 1;
    public const int SegmentHeaderLength = 32;

    public static void WriteSegmentHeader(Stream stream, long createdUtcTicks, long firstSequenceId)
    {
        Span<byte> header = stackalloc byte[SegmentHeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], SegmentMagic);
        header[4] = SegmentVersion;
        header[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], SegmentHeaderLength);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..16], createdUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..24], firstSequenceId);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..32], 0);
        stream.Write(header);
    }

    public static bool TryReadSegmentHeader(Stream stream, out TickJournalSegmentHeader header)
    {
        Span<byte> buffer = stackalloc byte[SegmentHeaderLength];
        if (!ReadExactlyOrFalse(stream, buffer))
        {
            header = default;
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        var version = buffer[4];
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..8]);
        if (magic != SegmentMagic || version != SegmentVersion || headerLength != SegmentHeaderLength)
        {
            header = default;
            return false;
        }

        header = new TickJournalSegmentHeader(
            CreatedUtcTicks: BinaryPrimitives.ReadInt64LittleEndian(buffer[8..16]),
            FirstSequenceId: BinaryPrimitives.ReadInt64LittleEndian(buffer[16..24]));
        return true;
    }

    public static byte[] EncodeRecord(TradeSignal signal)
    {
        using var payloadStream = new MemoryStream(256);
        using var writer = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true);

        writer.Write(signal.SequenceId);
        writer.Write(signal.Timestamp.Kind == DateTimeKind.Utc ? signal.Timestamp.Ticks : signal.Timestamp.ToUniversalTime().Ticks);
        WriteDecimal(writer, signal.BidPrice);
        WriteDecimal(writer, signal.AskPrice);
        WriteDecimal(writer, signal.MidPrice);
        WriteDecimal(writer, signal.Change);
        writer.Write(signal.ChangePercent);
        writer.Write(signal.Volume);
        WriteString(writer, signal.Symbol);
        WriteString(writer, signal.Exchange);
        WriteString(writer, signal.Direction);
        writer.Flush();

        var payload = payloadStream.ToArray();
        var record = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4, 4), Crc32.Compute(payload));
        payload.CopyTo(record.AsSpan(8));
        return record;
    }

    public static bool TryReadRecord(Stream stream, out TradeSignal signal)
    {
        Span<byte> frameHeader = stackalloc byte[8];
        if (!ReadExactlyOrFalse(stream, frameHeader))
        {
            signal = default;
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frameHeader[0..4]);
        if (payloadLength <= 0 || payloadLength > 1024 * 1024)
        {
            signal = default;
            return false;
        }

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader[4..8]);
        var payload = new byte[payloadLength];
        if (!ReadExactlyOrFalse(stream, payload))
        {
            signal = default;
            return false;
        }

        if (Crc32.Compute(payload) != expectedChecksum)
        {
            signal = default;
            return false;
        }

        signal = DecodePayload(payload);
        return true;
    }

    public static byte[] EncodePayload(TradeSignal signal)
    {
        var record = EncodeRecord(signal);
        return record[8..];
    }

    public static TradeSignal DecodePayload(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var sequenceId = reader.ReadInt64();
        var timestampTicks = reader.ReadInt64();
        var bidPrice = ReadDecimal(reader);
        var askPrice = ReadDecimal(reader);
        var midPrice = ReadDecimal(reader);
        var change = ReadDecimal(reader);
        var changePercent = reader.ReadDouble();
        var volume = reader.ReadInt64();
        var symbol = ReadString(reader);
        var exchange = ReadString(reader);
        var direction = ReadString(reader);

        return new TradeSignal(
            Symbol: symbol,
            BidPrice: bidPrice,
            AskPrice: askPrice,
            MidPrice: midPrice,
            Change: change,
            ChangePercent: changePercent,
            Volume: volume,
            Exchange: exchange,
            Timestamp: new DateTime(timestampTicks, DateTimeKind.Utc),
            Direction: direction,
            SequenceId: sequenceId);
    }

    private static void WriteDecimal(BinaryWriter writer, decimal value)
    {
        var bits = decimal.GetBits(value);
        writer.Write(bits[0]);
        writer.Write(bits[1]);
        writer.Write(bits[2]);
        writer.Write(bits[3]);
    }

    private static decimal ReadDecimal(BinaryReader reader)
    {
        Span<int> bits = stackalloc int[4];
        bits[0] = reader.ReadInt32();
        bits[1] = reader.ReadInt32();
        bits[2] = reader.ReadInt32();
        bits[3] = reader.ReadInt32();
        return new decimal(bits);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException($"String field is too large for tick journal record: {bytes.Length} bytes");
        }

        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("Unexpected end of journal string field.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool ReadExactlyOrFalse(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static bool ReadExactlyOrFalse(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }
}

public readonly record struct TickJournalSegmentHeader(long CreatedUtcTicks, long FirstSequenceId);

internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var j = 0; j < 8; j++)
            {
                value = (value & 1) == 1 ? Polynomial ^ (value >> 1) : value >> 1;
            }
            table[i] = value;
        }
        return table;
    }
}
