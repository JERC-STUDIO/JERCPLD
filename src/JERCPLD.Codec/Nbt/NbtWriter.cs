using System.Buffers.Binary;
using System.Text;

namespace JERCPLD.Codec.Nbt;

/// <summary>Bedrock NBT 序列化。</summary>
public static class NbtWriter
{
    public static byte[] Write(NbtTag tag, string rootName = "", NbtEncoding encoding = NbtEncoding.LittleEndian)
    {
        using var stream = new MemoryStream();
        WriteTag(stream, tag, rootName, encoding, writeType: true);
        return stream.ToArray();
    }

    private static void WriteTag(
        MemoryStream stream,
        NbtTag tag,
        string name,
        NbtEncoding encoding,
        bool writeType)
    {
        if (writeType)
        {
            stream.WriteByte((byte)tag.Type);
            WriteString(stream, name, encoding);
        }

        switch (tag)
        {
            case NbtByte value:
                stream.WriteByte((byte)value.Value);
                break;

            case NbtShort value:
                WriteInt16(stream, value.Value);
                break;

            case NbtInt value:
                WriteInt32(stream, value.Value, encoding);
                break;

            case NbtLong value:
                WriteInt64(stream, value.Value);
                break;

            case NbtFloat value:
                Span<byte> floatBytes = stackalloc byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(floatBytes, value.Value);
                stream.Write(floatBytes);
                break;

            case NbtDouble value:
                Span<byte> doubleBytes = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(doubleBytes, value.Value);
                stream.Write(doubleBytes);
                break;

            case NbtByteArray value:
                WriteLength(stream, value.Value.Length, encoding);
                stream.Write(value.Value);
                break;

            case NbtString value:
                WriteString(stream, value.Value, encoding);
                break;

            case NbtList value:
                stream.WriteByte((byte)value.ElementType);
                WriteLength(stream, value.Items.Count, encoding);
                foreach (var item in value.Items)
                {
                    WriteTag(stream, item, string.Empty, encoding, writeType: false);
                }

                break;

            case NbtCompound value:
                foreach (var entry in value.Entries)
                {
                    WriteTag(stream, entry.Value, entry.Key, encoding, writeType: true);
                }

                stream.WriteByte((byte)NbtTagType.End);
                break;

            case NbtIntArray value:
                WriteLength(stream, value.Value.Length, encoding);
                foreach (var item in value.Value)
                {
                    WriteInt32(stream, item, encoding);
                }

                break;

            case NbtLongArray value:
                WriteLength(stream, value.Value.Length, encoding);
                foreach (var item in value.Value)
                {
                    WriteInt64(stream, item);
                }

                break;

            default:
                throw new NotSupportedException($"不支持的 NBT 类型 {tag.Type}");
        }
    }

    private static void WriteString(MemoryStream stream, string value, NbtEncoding encoding)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (encoding == NbtEncoding.NetworkLittleEndian)
        {
            WriteVarUInt(stream, (uint)bytes.Length);
        }
        else
        {
            if (bytes.Length > ushort.MaxValue)
            {
                throw new InvalidDataException("NBT 字符串超过 65535 字节");
            }

            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)bytes.Length);
            stream.Write(length);
        }

        stream.Write(bytes);
    }

    private static void WriteLength(MemoryStream stream, int length, NbtEncoding encoding)
    {
        if (encoding == NbtEncoding.NetworkLittleEndian)
        {
            WriteVarInt(stream, length);
        }
        else
        {
            WriteInt32(stream, length, encoding);
        }
    }

    private static void WriteInt16(MemoryStream stream, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(MemoryStream stream, int value, NbtEncoding encoding)
    {
        if (encoding == NbtEncoding.NetworkLittleEndian)
        {
            WriteVarInt(stream, value);
            return;
        }

        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(MemoryStream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteVarUInt(MemoryStream stream, uint value)
    {
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            stream.WriteByte(current);
        }
        while (value != 0);
    }

    private static void WriteVarInt(MemoryStream stream, int value)
    {
        var zigZag = (uint)((value << 1) ^ (value >> 31));
        WriteVarUInt(stream, zigZag);
    }
}
