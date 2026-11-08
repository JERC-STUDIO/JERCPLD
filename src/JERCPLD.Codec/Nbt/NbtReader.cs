using System.Buffers.Binary;
using System.Text;

namespace JERCPLD.Codec.Nbt;

/// <summary>Bedrock NBT 反序列化。</summary>
public static class NbtReader
{
    public static NbtTag Read(ReadOnlySpan<byte> data, NbtEncoding encoding = NbtEncoding.LittleEndian)
    {
        var reader = new Cursor(data, encoding);
        var tag = ReadTag(ref reader, readType: true, out _);
        return tag;
    }

    public static bool TryReadRoot(ReadOnlySpan<byte> data, out NbtTag tag, out int consumed, NbtEncoding encoding = NbtEncoding.LittleEndian)
    {
        try
        {
            var reader = new Cursor(data, encoding);
            tag = ReadTag(ref reader, readType: true, out _);
            consumed = reader.Position;
            return true;
        }
        catch (Exception)
        {
            tag = null!;
            consumed = 0;
            return false;
        }
    }

    private static NbtTag ReadTag(ref Cursor reader, bool readType, out string name)
    {
        if (!readType)
        {
            throw new InvalidDataException("NBT 列表元素必须显式提供类型");
        }

        var type = (NbtTagType)reader.ReadByte();
        name = reader.ReadString();

        return type switch
        {
            NbtTagType.End => throw new InvalidDataException("根标签不能是 TAG_End"),
            NbtTagType.Byte => new NbtByte((sbyte)reader.ReadByte()),
            NbtTagType.Short => new NbtShort(reader.ReadInt16()),
            NbtTagType.Int => new NbtInt(reader.ReadInt32()),
            NbtTagType.Long => new NbtLong(reader.ReadInt64()),
            NbtTagType.Float => new NbtFloat(reader.ReadSingle()),
            NbtTagType.Double => new NbtDouble(reader.ReadDouble()),
            NbtTagType.ByteArray => new NbtByteArray(reader.ReadBytes(reader.ReadLength())),
            NbtTagType.String => new NbtString(reader.ReadString()),
            NbtTagType.List => ReadList(ref reader),
            NbtTagType.Compound => ReadCompound(ref reader),
            NbtTagType.IntArray => ReadIntArray(ref reader),
            NbtTagType.LongArray => ReadLongArray(ref reader),
            _ => throw new InvalidDataException($"未知 NBT 类型 {(byte)type}"),
        };
    }

    private static NbtList ReadList(ref Cursor reader)
    {
        var elementType = (NbtTagType)reader.ReadByte();
        var length = reader.ReadLength();
        var list = new NbtList(elementType);
        for (var i = 0; i < length; i++)
        {
            list.Items.Add(ReadPayload(ref reader, elementType));
        }

        return list;
    }

    private static NbtCompound ReadCompound(ref Cursor reader)
    {
        var compound = new NbtCompound();
        while (true)
        {
            var type = (NbtTagType)reader.ReadByte();
            if (type == NbtTagType.End)
            {
                break;
            }

            var name = reader.ReadString();
            compound.Add(name, ReadPayload(ref reader, type));
        }

        return compound;
    }

    private static NbtIntArray ReadIntArray(ref Cursor reader)
    {
        var length = reader.ReadLength();
        var values = new int[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = reader.ReadInt32();
        }

        return new NbtIntArray(values);
    }

    private static NbtLongArray ReadLongArray(ref Cursor reader)
    {
        var length = reader.ReadLength();
        var values = new long[length];
        for (var i = 0; i < length; i++)
        {
            values[i] = reader.ReadInt64();
        }

        return new NbtLongArray(values);
    }

    private static NbtTag ReadPayload(ref Cursor reader, NbtTagType type)
    {
        return type switch
        {
            NbtTagType.Byte => new NbtByte((sbyte)reader.ReadByte()),
            NbtTagType.Short => new NbtShort(reader.ReadInt16()),
            NbtTagType.Int => new NbtInt(reader.ReadInt32()),
            NbtTagType.Long => new NbtLong(reader.ReadInt64()),
            NbtTagType.Float => new NbtFloat(reader.ReadSingle()),
            NbtTagType.Double => new NbtDouble(reader.ReadDouble()),
            NbtTagType.ByteArray => new NbtByteArray(reader.ReadBytes(reader.ReadLength())),
            NbtTagType.String => new NbtString(reader.ReadString()),
            NbtTagType.List => ReadList(ref reader),
            NbtTagType.Compound => ReadCompound(ref reader),
            NbtTagType.IntArray => ReadIntArray(ref reader),
            NbtTagType.LongArray => ReadLongArray(ref reader),
            _ => throw new InvalidDataException($"未知 NBT 类型 {(byte)type}"),
        };
    }

    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly NbtEncoding _encoding;
        private int _position;

        public Cursor(ReadOnlySpan<byte> data, NbtEncoding encoding)
        {
            _data = data;
            _encoding = encoding;
            _position = 0;
        }

        public int Position => _position;

        public byte ReadByte()
        {
            Ensure(1);
            return _data[_position++];
        }

        public short ReadInt16()
        {
            Ensure(2);
            var value = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_position, 2));
            _position += 2;
            return value;
        }

        public int ReadInt32()
        {
            if (_encoding == NbtEncoding.NetworkLittleEndian)
            {
                return ReadVarInt();
            }

            Ensure(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public long ReadInt64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_position, 8));
            _position += 8;
            return value;
        }

        public float ReadSingle()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public double ReadDouble()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_position, 8));
            _position += 8;
            return value;
        }

        public int ReadLength() => ReadInt32();

        public string ReadString()
        {
            int length;
            if (_encoding == NbtEncoding.NetworkLittleEndian)
            {
                length = checked((int)ReadVarUInt());
            }
            else
            {
                Ensure(2);
                length = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, 2));
                _position += 2;
            }

            Ensure(length);
            var value = Encoding.UTF8.GetString(_data.Slice(_position, length));
            _position += length;
            return value;
        }

        public byte[] ReadBytes(int length)
        {
            Ensure(length);
            var value = _data.Slice(_position, length).ToArray();
            _position += length;
            return value;
        }

        private int ReadVarInt()
        {
            var raw = ReadVarUInt();
            return (int)(raw >> 1) ^ -(int)(raw & 1);
        }

        private uint ReadVarUInt()
        {
            uint result = 0;
            var shift = 0;
            for (var i = 0; i < 5; i++)
            {
                var current = ReadByte();
                result |= (uint)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }

            throw new InvalidDataException("NBT varint 超过 5 字节");
        }

        private void Ensure(int count)
        {
            if (count < 0 || _position + count > _data.Length)
            {
                throw new EndOfStreamException($"NBT 读取越界: 需要 {count} 字节，剩余 {_data.Length - _position}");
            }
        }
    }
}
