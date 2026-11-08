using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.Codec.Bedrock;

/// <summary>
/// Bedrock 批次（batch）编解码：VarUInt(包长) | VarUInt(包 ID) | 包体，重复直到批次结束。
/// </summary>
public static class BedrockBatchCodec
{
    public static IReadOnlyList<BedrockPacketFrame> DecodeBatch(ReadOnlyMemory<byte> batch)
    {
        var reader = new BinaryReaderEx(batch);
        var frames = new List<BedrockPacketFrame>();

        while (reader.Remaining > 0)
        {
            var length = reader.ReadVarUInt();
            if (length == 0 || length > (uint)reader.Remaining)
            {
                throw new InvalidDataException($"Bedrock 批次内包长度非法: {length}, 剩余 {reader.Remaining}");
            }

            var packet = reader.ReadBytes((int)length);
            frames.Add(DecodePacket(packet));
        }

        return frames;
    }

    public static BedrockPacketFrame DecodePacket(ReadOnlyMemory<byte> packet)
    {
        var reader = new BinaryReaderEx(packet);
        var packetId = checked((int)reader.ReadVarUInt());
        return new BedrockPacketFrame(packetId, reader.ReadRemaining());
    }

    public static byte[] EncodePacket(BedrockPacketFrame frame)
    {
        var writer = new BinaryWriterEx();
        writer.WriteVarUInt((uint)frame.PacketId);
        writer.WriteBytes(frame.Body.Span);
        return writer.ToArray();
    }

    public static byte[] EncodeBatch(IEnumerable<BedrockPacketFrame> frames)
    {
        var writer = new BinaryWriterEx();
        foreach (var frame in frames)
        {
            var raw = EncodePacket(frame);
            writer.WriteVarUInt((uint)raw.Length);
            writer.WriteBytes(raw);
        }

        return writer.ToArray();
    }
}
