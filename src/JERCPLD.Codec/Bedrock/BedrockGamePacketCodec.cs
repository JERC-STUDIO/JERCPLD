using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.Codec.Bedrock;

/// <summary>
/// Bedrock 游戏包外层帧：0xFE | VarUInt(长度) | 批次载荷。
/// 批次载荷可能是明文，也可能按 NetworkSettings 协商结果压缩，压缩判断由调用方负责。
/// </summary>
public static class BedrockGamePacketCodec
{
    public static byte[] Wrap(ReadOnlyMemory<byte> batchPayload)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetConstants.GamePacketId);
        writer.WriteVarUInt((uint)batchPayload.Length);
        writer.WriteBytes(batchPayload.Span);
        return writer.ToArray();
    }

    public static bool TryUnwrap(ReadOnlyMemory<byte> frame, out ReadOnlyMemory<byte> batchPayload)
    {
        batchPayload = default;
        if (frame.Length < 2 || frame.Span[0] != RakNetConstants.GamePacketId)
        {
            return false;
        }

        var reader = new BinaryReaderEx(frame);
        reader.ReadByte();
        var length = reader.ReadVarUInt();
        if (length > (uint)reader.Remaining)
        {
            return false;
        }

        batchPayload = reader.ReadBytes((int)length);
        return true;
    }
}
