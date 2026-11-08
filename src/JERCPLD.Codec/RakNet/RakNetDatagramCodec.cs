using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.Codec.RakNet;

public static class RakNetDatagramCodec
{
    public const byte DatagramHeader = 0x84;

    public static bool TryRead(ReadOnlyMemory<byte> packet, out RakNetDatagram? datagram)
    {
        datagram = null;
        if (packet.Length < 4)
        {
            return false;
        }

        var header = packet.Span[0];
        if ((header & RakNetMessageId.DatagramValid) == 0)
        {
            return false;
        }

        var reader = new BinaryReaderEx(packet);
        reader.ReadByte();
        var packetNumber = reader.ReadUInt24LE();
        var frames = RakNetFrameCodec.ReadFrames(reader);
        datagram = new RakNetDatagram(header, packetNumber, frames);
        return true;
    }

    public static byte[] Write(uint packetNumber, IEnumerable<RakNetFrame> frames)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(DatagramHeader);
        writer.WriteUInt24LE(packetNumber);
        foreach (var frame in frames)
        {
            RakNetFrameCodec.WriteFrame(writer, frame);
        }

        return writer.ToArray();
    }

    public static bool IsAck(ReadOnlySpan<byte> packet)
        => packet.Length > 0 && packet[0] == RakNetMessageId.Ack;

    public static bool IsNack(ReadOnlySpan<byte> packet)
        => packet.Length > 0 && packet[0] == RakNetMessageId.Nack;

    public static IReadOnlyList<(uint Start, uint End)> ReadAckRanges(ReadOnlyMemory<byte> packet)
    {
        var reader = new BinaryReaderEx(packet);
        var header = reader.ReadByte();
        if (header is not (RakNetMessageId.Ack or RakNetMessageId.Nack))
        {
            throw new InvalidDataException($"不是 ACK/NAK 包: 0x{header:X2}");
        }

        var count = reader.ReadUInt16BE();
        var ranges = new List<(uint Start, uint End)>(count);
        for (var i = 0; i < count; i++)
        {
            var isSingle = reader.ReadByte() == 1;
            var start = reader.ReadUInt24LE();
            if (isSingle)
            {
                ranges.Add((start, start));
            }
            else
            {
                ranges.Add((start, reader.ReadUInt24LE()));
            }
        }

        return ranges;
    }

    public static byte[] WriteAck(IReadOnlyList<(uint Start, uint End)> ranges)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetMessageId.Ack);
        writer.WriteUInt16BE((ushort)ranges.Count);
        foreach (var (start, end) in ranges)
        {
            if (start == end)
            {
                writer.WriteByte(1);
                writer.WriteUInt24LE(start);
            }
            else
            {
                writer.WriteByte(0);
                writer.WriteUInt24LE(start);
                writer.WriteUInt24LE(end);
            }
        }

        return writer.ToArray();
    }
}
