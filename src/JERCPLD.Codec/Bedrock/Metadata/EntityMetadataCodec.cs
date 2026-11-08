using JERCPLD.Codec.Binary;
using JERCPLD.Codec.Nbt;

namespace JERCPLD.Codec.Bedrock.Metadata;

public enum MetadataValueKind : byte
{
    Byte = 0,
    Short = 1,
    Int = 2,
    Float = 3,
    String = 4,
    CompoundTag = 5,
    Pos = 6,
    Int64 = 7,
    Vec3 = 8,
}

public sealed record EntityMetadataEntry(int Id, MetadataValueKind Kind, object Value);

/// <summary>
/// SynchedActorDataList 编解码：varuint count，随后每项 varuint ID、uint8 Type、值。
/// 依据 Mojang 1.26.60 协议文档（DataItemEntry / DataItemType）。
/// </summary>
public static class EntityMetadataCodec
{
    public static IReadOnlyList<EntityMetadataEntry> Read(ReadOnlySpan<byte> data)
    {
        if (!TryRead(data, out var entries, out _))
        {
            throw new InvalidDataException("实体元数据解析失败");
        }

        return entries;
    }

    /// <summary>解析元数据列表，consumed 为列表中实际使用的字节数。</summary>
    public static bool TryRead(
        ReadOnlySpan<byte> data,
        out List<EntityMetadataEntry> entries,
        out int consumed)
    {
        entries = new List<EntityMetadataEntry>();
        consumed = 0;
        try
        {
            var reader = new BinaryReaderEx(data.ToArray());
            var count = reader.ReadVarUInt();
            for (var i = 0; i < count; i++)
            {
                var id = checked((int)reader.ReadVarUInt());
                var kind = (MetadataValueKind)reader.ReadByte();
                var value = ReadValue(ref reader, kind);
                entries.Add(new EntityMetadataEntry(id, kind, value));
            }

            consumed = reader.Position;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static byte[] Write(IEnumerable<EntityMetadataEntry> entries)
    {
        var list = entries.ToList();
        var writer = new BinaryWriterEx();
        writer.WriteVarUInt((uint)list.Count);
        foreach (var entry in list)
        {
            writer.WriteVarUInt((uint)entry.Id);
            writer.WriteByte((byte)entry.Kind);
            WriteValue(writer, entry.Kind, entry.Value);
        }

        return writer.ToArray();
    }

    private static object ReadValue(ref BinaryReaderEx reader, MetadataValueKind kind)
    {
        return kind switch
        {
            MetadataValueKind.Byte => (sbyte)reader.ReadByte(),
            MetadataValueKind.Short => (short)reader.ReadUInt16LE(),
            MetadataValueKind.Int => reader.ReadVarInt(),
            MetadataValueKind.Float => reader.ReadSingleLE(),
            MetadataValueKind.String => reader.ReadString(),
            MetadataValueKind.CompoundTag => ReadNbt(ref reader),
            MetadataValueKind.Pos => (reader.ReadVarInt(), reader.ReadVarInt(), reader.ReadVarInt()),
            MetadataValueKind.Int64 => reader.ReadVarLong(),
            MetadataValueKind.Vec3 => (reader.ReadSingleLE(), reader.ReadSingleLE(), reader.ReadSingleLE()),
            _ => throw new InvalidDataException($"未知元数据类型 {(byte)kind}"),
        };
    }

    private static void WriteValue(BinaryWriterEx writer, MetadataValueKind kind, object value)
    {
        switch (kind)
        {
            case MetadataValueKind.Byte:
                writer.WriteByte(unchecked((byte)(sbyte)value));
                break;
            case MetadataValueKind.Short:
                writer.WriteInt16LE((short)value);
                break;
            case MetadataValueKind.Int:
                writer.WriteVarInt((int)value);
                break;
            case MetadataValueKind.Float:
                writer.WriteSingleLE((float)value);
                break;
            case MetadataValueKind.String:
                writer.WriteString((string)value);
                break;
            case MetadataValueKind.CompoundTag:
                writer.WriteBytes(NbtWriter.Write((NbtTag)value, string.Empty, NbtEncoding.NetworkLittleEndian));
                break;
            case MetadataValueKind.Pos:
                var pos = ((int X, int Y, int Z))value;
                writer.WriteVarInt(pos.X);
                writer.WriteVarInt(pos.Y);
                writer.WriteVarInt(pos.Z);
                break;
            case MetadataValueKind.Int64:
                writer.WriteVarLong((long)value);
                break;
            case MetadataValueKind.Vec3:
                var vec = ((float X, float Y, float Z))value;
                writer.WriteSingleLE(vec.X);
                writer.WriteSingleLE(vec.Y);
                writer.WriteSingleLE(vec.Z);
                break;
            default:
                throw new NotSupportedException($"未知元数据类型 {kind}");
        }
    }

    private static NbtTag ReadNbt(ref BinaryReaderEx reader)
    {
        var remaining = reader.PeekRemaining();
        if (!NbtReader.TryReadRoot(remaining.Span, out var tag, out var consumed, NbtEncoding.NetworkLittleEndian))
        {
            throw new InvalidDataException("元数据 NBT 解析失败");
        }

        reader.ReadBytes(consumed);
        return tag;
    }
}
