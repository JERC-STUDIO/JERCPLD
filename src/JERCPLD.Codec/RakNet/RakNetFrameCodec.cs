using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.Codec.RakNet;

/// <summary>
/// RakNet 帧（可靠性头 + 长度 + 载荷）编解码。
/// TODO(raknet): 分片帧目前只解析不重组；可靠性重传、拥塞控制也未实现。
/// </summary>
public static class RakNetFrameCodec
{
    public static IReadOnlyList<RakNetFrame> ReadFrames(BinaryReaderEx reader)
    {
        var frames = new List<RakNetFrame>();

        while (reader.Remaining > 0)
        {
            var flags = reader.ReadByte();
            var reliability = (RakNetReliability)((flags & 0xE0) >> 5);
            var isSplit = (flags & 0x10) != 0;

            uint reliableIndex = 0;
            uint sequenceIndex = 0;
            uint orderIndex = 0;
            byte orderChannel = 0;

            if (HasReliableIndex(reliability))
            {
                reliableIndex = reader.ReadUInt24LE();
            }

            if (HasSequenceIndex(reliability))
            {
                sequenceIndex = reader.ReadUInt24LE();
            }

            if (HasOrderIndex(reliability))
            {
                orderIndex = reader.ReadUInt24LE();
                orderChannel = reader.ReadByte();
            }

            var splitCount = 0;
            ushort splitId = 0;
            var splitIndex = 0;
            if (isSplit)
            {
                splitCount = checked((int)reader.ReadUInt32BE());
                splitId = reader.ReadUInt16BE();
                splitIndex = checked((int)reader.ReadUInt32BE());
            }

            var length = reader.ReadUInt16BE();
            var payload = reader.ReadBytes(length);

            frames.Add(new RakNetFrame
            {
                Reliability = reliability,
                ReliableIndex = reliableIndex,
                SequenceIndex = sequenceIndex,
                OrderIndex = orderIndex,
                OrderChannel = orderChannel,
                IsSplit = isSplit,
                SplitCount = splitCount,
                SplitId = splitId,
                SplitIndex = splitIndex,
                Payload = payload,
            });
        }

        return frames;
    }

    public static void WriteFrame(BinaryWriterEx writer, RakNetFrame frame)
    {
        var reliability = (byte)((byte)frame.Reliability << 5);
        if (frame.IsSplit)
        {
            reliability |= 0x10;
        }

        writer.WriteByte(reliability);

        if (HasReliableIndex(frame.Reliability))
        {
            writer.WriteUInt24LE(frame.ReliableIndex);
        }

        if (HasSequenceIndex(frame.Reliability))
        {
            writer.WriteUInt24LE(frame.SequenceIndex);
        }

        if (HasOrderIndex(frame.Reliability))
        {
            writer.WriteUInt24LE(frame.OrderIndex);
            writer.WriteByte(frame.OrderChannel);
        }

        if (frame.IsSplit)
        {
            writer.WriteUInt32BE((uint)frame.SplitCount);
            writer.WriteUInt16BE(frame.SplitId);
            writer.WriteUInt32BE((uint)frame.SplitIndex);
        }

        if (frame.Payload.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException($"RakNet 帧载荷过大: {frame.Payload.Length}");
        }

        writer.WriteUInt16BE((ushort)frame.Payload.Length);
        writer.WriteBytes(frame.Payload.Span);
    }

    private static bool HasReliableIndex(RakNetReliability reliability)
        => reliability is RakNetReliability.Reliable
            or RakNetReliability.ReliableOrdered
            or RakNetReliability.ReliableSequenced
            or RakNetReliability.ReliableWithAckReceipt
            or RakNetReliability.ReliableOrderedWithAckReceipt;

    private static bool HasSequenceIndex(RakNetReliability reliability)
        => reliability is RakNetReliability.UnreliableSequenced or RakNetReliability.ReliableSequenced;

    private static bool HasOrderIndex(RakNetReliability reliability)
        => reliability is RakNetReliability.ReliableOrdered
            or RakNetReliability.ReliableSequenced
            or RakNetReliability.ReliableOrderedWithAckReceipt;
}
