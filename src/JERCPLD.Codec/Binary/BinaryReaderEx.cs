using System.Text;

namespace JERCPLD.Codec.Binary;

/// <summary>
/// 协议解码用的大端读取器，附带 VarUInt/VarInt 与边界检查。
/// </summary>
public sealed class BinaryReaderEx
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _position;

    public BinaryReaderEx(ReadOnlyMemory<byte> buffer) => _buffer = buffer;

    public int Position => _position;

    public int Remaining => _buffer.Length - _position;

    public byte ReadByte()
    {
        Ensure(1);
        return _buffer.Span[_position++];
    }

    public ReadOnlyMemory<byte> ReadBytes(int count)
    {
        Ensure(count);
        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    public ReadOnlyMemory<byte> ReadRemaining() => ReadBytes(Remaining);

    /// <summary>返回剩余字节但不移动读取位置。</summary>
    public ReadOnlyMemory<byte> PeekRemaining() => _buffer.Slice(_position);

    public ushort ReadUInt16BE()
    {
        Ensure(2);
        var span = _buffer.Span;
        var value = (ushort)((span[_position] << 8) | span[_position + 1]);
        _position += 2;
        return value;
    }

    public ushort ReadUInt16LE()
    {
        Ensure(2);
        var span = _buffer.Span;
        var value = (ushort)(span[_position] | (span[_position + 1] << 8));
        _position += 2;
        return value;
    }

    public int ReadInt32LE()
    {
        Ensure(4);
        var span = _buffer.Span;
        var value = span[_position]
                    | (span[_position + 1] << 8)
                    | (span[_position + 2] << 16)
                    | (span[_position + 3] << 24);
        _position += 4;
        return value;
    }

    public uint ReadUInt32LE() => unchecked((uint)ReadInt32LE());

    public long ReadInt64LE()
    {
        Ensure(8);
        var span = _buffer.Span;
        long value = 0;
        for (var i = 7; i >= 0; i--)
        {
            value = (value << 8) | span[_position + i];
        }

        _position += 8;
        return value;
    }

    public uint ReadUInt24LE()
    {
        Ensure(3);
        var span = _buffer.Span;
        var value = (uint)(span[_position] | (span[_position + 1] << 8) | (span[_position + 2] << 16));
        _position += 3;
        return value;
    }

    public uint ReadUInt32BE()
    {
        Ensure(4);
        var span = _buffer.Span;
        var value = ((uint)span[_position] << 24)
                    | ((uint)span[_position + 1] << 16)
                    | ((uint)span[_position + 2] << 8)
                    | span[_position + 3];
        _position += 4;
        return value;
    }

    public ulong ReadUInt64BE()
    {
        Ensure(8);
        var span = _buffer.Span;
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | span[_position + i];
        }

        _position += 8;
        return value;
    }

    public ulong ReadUInt64LE()
    {
        Ensure(8);
        var span = _buffer.Span;
        ulong value = 0;
        for (var i = 7; i >= 0; i--)
        {
            value = (value << 8) | span[_position + i];
        }

        _position += 8;
        return value;
    }

    public float ReadSingleLE()
    {
        Ensure(4);
        var span = _buffer.Span;
        var bits = (uint)(span[_position]
                          | (span[_position + 1] << 8)
                          | (span[_position + 2] << 16)
                          | (span[_position + 3] << 24));
        _position += 4;
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    public uint ReadVarUInt()
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

        throw new InvalidDataException("VarUInt 超过 5 字节");
    }

    public int ReadVarInt()
    {
        var raw = ReadVarUInt();
        return (int)(raw >> 1) ^ -(int)(raw & 1);
    }

    public ulong ReadVarULong()
    {
        ulong result = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            var current = ReadByte();
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("VarULong 超过 10 字节");
    }

    public long ReadVarLong()
    {
        var raw = ReadVarULong();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    public string ReadString()
    {
        var length = ReadVarUInt();
        if (length > (uint)Remaining)
        {
            throw new InvalidDataException($"字符串长度越界: {length} > {Remaining}");
        }

        return Encoding.UTF8.GetString(ReadBytes((int)length).Span);
    }

    public bool ReadBool() => ReadByte() != 0;

    private void Ensure(int count)
    {
        if (count < 0 || _position + count > _buffer.Length)
        {
            throw new InvalidDataException($"读取越界: 需要 {count} 字节，剩余 {Remaining}");
        }
    }
}
