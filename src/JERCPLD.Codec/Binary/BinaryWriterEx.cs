using System.Text;

namespace JERCPLD.Codec.Binary;

/// <summary>
/// 协议编码用的大端写入器，附带 Bedrock 的 VarUInt/VarInt 变长整数。
/// </summary>
public sealed class BinaryWriterEx
{
    private readonly MemoryStream _stream = new();

    public int Length => checked((int)_stream.Length);

    public BinaryWriterEx WriteByte(byte value)
    {
        _stream.WriteByte(value);
        return this;
    }

    public BinaryWriterEx WriteBytes(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        return this;
    }

    public BinaryWriterEx WriteUInt16BE(ushort value)
    {
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
        return this;
    }

    public BinaryWriterEx WriteUInt24LE(uint value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)(value >> 16));
        return this;
    }

    public BinaryWriterEx WriteUInt32BE(uint value)
    {
        _stream.WriteByte((byte)(value >> 24));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)value);
        return this;
    }

    public BinaryWriterEx WriteUInt64BE(ulong value)
    {
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            _stream.WriteByte((byte)(value >> shift));
        }

        return this;
    }

    public BinaryWriterEx WriteUInt64LE(ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            _stream.WriteByte((byte)(value >> shift));
        }

        return this;
    }

    public BinaryWriterEx WriteInt32LE(int value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)(value >> 16));
        _stream.WriteByte((byte)(value >> 24));
        return this;
    }

    public BinaryWriterEx WriteUInt32LE(uint value) => WriteInt32LE(unchecked((int)value));

    public BinaryWriterEx WriteInt64LE(long value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            _stream.WriteByte((byte)(value >> shift));
        }

        return this;
    }

    public BinaryWriterEx WriteSingleLE(float value)
    {
        var bits = (uint)BitConverter.SingleToInt32Bits(value);
        _stream.WriteByte((byte)bits);
        _stream.WriteByte((byte)(bits >> 8));
        _stream.WriteByte((byte)(bits >> 16));
        _stream.WriteByte((byte)(bits >> 24));
        return this;
    }

    public BinaryWriterEx WriteVarUInt(uint value)
    {
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            _stream.WriteByte(current);
        }
        while (value != 0);

        return this;
    }

    public BinaryWriterEx WriteVarInt(int value)
    {
        var zigZag = (uint)((value << 1) ^ (value >> 31));
        return WriteVarUInt(zigZag);
    }

    public BinaryWriterEx WriteVarULong(ulong value)
    {
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            _stream.WriteByte(current);
        }
        while (value != 0);

        return this;
    }

    public BinaryWriterEx WriteVarLong(long value)
    {
        var zigZag = (ulong)((value << 1) ^ (value >> 63));
        return WriteVarULong(zigZag);
    }

    public BinaryWriterEx WriteInt16LE(short value)
    {
        _stream.WriteByte((byte)value);
        _stream.WriteByte((byte)(value >> 8));
        return this;
    }

    public BinaryWriterEx WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarUInt((uint)bytes.Length);
        _stream.Write(bytes);
        return this;
    }

    public byte[] ToArray() => _stream.ToArray();
}
