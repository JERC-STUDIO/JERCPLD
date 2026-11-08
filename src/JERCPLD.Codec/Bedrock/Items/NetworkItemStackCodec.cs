using JERCPLD.Codec.Binary;

namespace JERCPLD.Codec.Bedrock.Items;

/// <summary>网络物品栈（NetworkItemStackDescriptor 的常用字段子集）。</summary>
public sealed record NetworkItemStack(
    int NetworkId,
    ushort Count,
    uint Metadata,
    int BlockRuntimeId,
    byte[] Nbt)
{
    public bool IsEmpty => NetworkId == 0;

    public static NetworkItemStack Empty { get; } = new(0, 0, 0, 0, Array.Empty<byte>());
}

/// <summary>
/// 网络物品栈编解码：
/// varint networkId（0 = 空）| uint16 LE count | varuint metadata | varint blockRuntimeId | varuint NBT 长度 + NBT。
///
/// TODO(items): 该布局依据社区实现，尚未用真实 2193/2225 包体交叉验证；
/// 若 BDS 版本在该结构上存在差异，需要按协议对拆分。
/// </summary>
public static class NetworkItemStackCodec
{
    public static NetworkItemStack Read(ref BinaryReaderEx reader)
    {
        var networkId = reader.ReadVarInt();
        if (networkId == 0)
        {
            return NetworkItemStack.Empty;
        }

        var count = reader.ReadUInt16LE();
        var metadata = reader.ReadVarUInt();
        var blockRuntimeId = reader.ReadVarInt();
        var nbtLength = reader.ReadVarUInt();
        var nbt = nbtLength == 0 ? Array.Empty<byte>() : reader.ReadBytes((int)nbtLength).ToArray();
        return new NetworkItemStack(networkId, count, metadata, blockRuntimeId, nbt);
    }

    public static void Write(BinaryWriterEx writer, NetworkItemStack item)
    {
        if (item.IsEmpty)
        {
            writer.WriteVarInt(0);
            return;
        }

        writer.WriteVarInt(item.NetworkId);
        writer.WriteInt16LE(unchecked((short)item.Count));
        writer.WriteVarUInt(item.Metadata);
        writer.WriteVarInt(item.BlockRuntimeId);
        writer.WriteVarUInt((uint)item.Nbt.Length);
        writer.WriteBytes(item.Nbt);
    }

    public static byte[] Write(NetworkItemStack item)
    {
        var writer = new BinaryWriterEx();
        Write(writer, item);
        return writer.ToArray();
    }
}
