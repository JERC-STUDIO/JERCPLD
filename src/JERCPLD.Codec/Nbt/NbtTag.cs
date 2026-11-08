namespace JERCPLD.Codec.Nbt;

public enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>Bedrock NBT 的编码变体。</summary>
public enum NbtEncoding
{
    /// <summary>经典 little-endian：字符串长度 uint16、数组/列表长度 int32。</summary>
    LittleEndian = 0,

    /// <summary>网络 NBT：字符串长度 varuint，数组/列表长度 varint32。</summary>
    NetworkLittleEndian = 1,
}

public abstract class NbtTag
{
    public abstract NbtTagType Type { get; }
}

public sealed class NbtByte(sbyte value) : NbtTag
{
    public sbyte Value { get; } = value;
    public override NbtTagType Type => NbtTagType.Byte;
}

public sealed class NbtShort(short value) : NbtTag
{
    public short Value { get; } = value;
    public override NbtTagType Type => NbtTagType.Short;
}

public sealed class NbtInt(int value) : NbtTag
{
    public int Value { get; } = value;
    public override NbtTagType Type => NbtTagType.Int;
}

public sealed class NbtLong(long value) : NbtTag
{
    public long Value { get; } = value;
    public override NbtTagType Type => NbtTagType.Long;
}

public sealed class NbtFloat(float value) : NbtTag
{
    public float Value { get; } = value;
    public override NbtTagType Type => NbtTagType.Float;
}

public sealed class NbtDouble(double value) : NbtTag
{
    public double Value { get; } = value;
    public override NbtTagType Type => NbtTagType.Double;
}

public sealed class NbtByteArray(byte[] value) : NbtTag
{
    public byte[] Value { get; } = value;
    public override NbtTagType Type => NbtTagType.ByteArray;
}

public sealed class NbtString(string value) : NbtTag
{
    public string Value { get; } = value;
    public override NbtTagType Type => NbtTagType.String;
}

public sealed class NbtList : NbtTag
{
    public NbtList(NbtTagType elementType, List<NbtTag>? items = null)
    {
        ElementType = elementType;
        Items = items ?? new List<NbtTag>();
    }

    public NbtTagType ElementType { get; }

    public List<NbtTag> Items { get; }

    public override NbtTagType Type => NbtTagType.List;
}

public sealed class NbtCompound : NbtTag
{
    private readonly List<KeyValuePair<string, NbtTag>> _entries = new();

    public override NbtTagType Type => NbtTagType.Compound;

    public IReadOnlyList<KeyValuePair<string, NbtTag>> Entries => _entries;

    public int Count => _entries.Count;

    public NbtTag? this[string name]
        => _entries.FirstOrDefault(entry => entry.Key == name).Value;

    public NbtCompound Add(string name, NbtTag value)
    {
        _entries.Add(new KeyValuePair<string, NbtTag>(name, value));
        return this;
    }

    public bool TryGet(string name, out NbtTag value)
    {
        foreach (var entry in _entries)
        {
            if (entry.Key == name)
            {
                value = entry.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }
}

public sealed class NbtIntArray(int[] value) : NbtTag
{
    public int[] Value { get; } = value;
    public override NbtTagType Type => NbtTagType.IntArray;
}

public sealed class NbtLongArray(long[] value) : NbtTag
{
    public long[] Value { get; } = value;
    public override NbtTagType Type => NbtTagType.LongArray;
}
