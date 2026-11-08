namespace JERCPLD.Codec.Bedrock.Metadata;

/// <summary>
/// 实体元数据快照：用于跨版本合并。
///  - 补默认槽：目标版本要求但源数据缺失的槽用默认值补齐；
///  - 位图压紧：把存在的槽压成位图，0x7F 作为终止符；
///  - 序列化时再展开为 SynchedActorDataList。
/// </summary>
public sealed class MetadataSnapshot
{
    private const byte BitmapTerminator = 0x7F;
    private readonly SortedDictionary<int, EntityMetadataEntry> _entries = new();

    public IReadOnlyDictionary<int, EntityMetadataEntry> Entries => _entries;

    public static MetadataSnapshot FromEntries(IEnumerable<EntityMetadataEntry> entries)
    {
        var snapshot = new MetadataSnapshot();
        foreach (var entry in entries)
        {
            snapshot._entries[entry.Id] = entry;
        }

        return snapshot;
    }

    /// <summary>补齐默认槽；已存在的槽不覆盖。</summary>
    public MetadataSnapshot FillDefaults(IEnumerable<EntityMetadataEntry> defaults)
    {
        foreach (var entry in defaults)
        {
            _entries.TryAdd(entry.Id, entry);
        }

        return this;
    }

    public IReadOnlyList<EntityMetadataEntry> ToEntries() => _entries.Values.ToList();

    /// <summary>位图压紧：bit 表示该槽存在，末尾 0x7F。仅支持 0..126 号槽。</summary>
    public byte[] ToCompactBitmap()
    {
        if (_entries.Count == 0)
        {
            return new[] { BitmapTerminator };
        }

        var maxId = _entries.Keys.Max();
        if (maxId >= BitmapTerminator)
        {
            throw new InvalidOperationException($"位图最大槽位为 {BitmapTerminator - 1}，实际 {maxId}");
        }

        var bytes = new byte[maxId / 8 + 1];
        foreach (var id in _entries.Keys)
        {
            bytes[id / 8] |= (byte)(1 << (id % 8));
        }

        return bytes.Concat(new[] { BitmapTerminator }).ToArray();
    }

    public static HashSet<int> ParseCompactBitmap(ReadOnlySpan<byte> bitmap)
    {
        var slots = new HashSet<int>();
        for (var byteIndex = 0; byteIndex < bitmap.Length; byteIndex++)
        {
            var value = bitmap[byteIndex];
            if (value == BitmapTerminator)
            {
                return slots;
            }

            for (var bit = 0; bit < 8; bit++)
            {
                if ((value & (1 << bit)) != 0)
                {
                    slots.Add(byteIndex * 8 + bit);
                }
            }
        }

        return slots;
    }
}
