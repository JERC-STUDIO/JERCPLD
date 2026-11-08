namespace JERCPLD.Codec.Nbt;

/// <summary>
/// 1.19.80+ 方块网络 ID 的哈希算法：
/// 把 { name, states } 序列化为 NBT（states 键按序数排序），再做 FNV-1a 32。
/// </summary>
public static class BlockStateHash
{
    public static uint Compute(
        string name,
        IReadOnlyDictionary<string, NbtTag>? states = null,
        NbtEncoding encoding = NbtEncoding.LittleEndian)
    {
        var root = new NbtCompound();
        root.Add("name", new NbtString(name));

        var statesCompound = new NbtCompound();
        if (states is not null)
        {
            foreach (var entry in states.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                statesCompound.Add(entry.Key, entry.Value);
            }
        }

        root.Add("states", statesCompound);

        var bytes = NbtWriter.Write(root, rootName: string.Empty, encoding);
        return Fnv1a32(bytes);
    }

    public static uint Fnv1a32(ReadOnlySpan<byte> data)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var value in data)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }
}
