using JERCPLD.Codec.Bedrock.Items;
using JERCPLD.Codec.Bedrock.Metadata;
using JERCPLD.Codec.Binary;
using JERCPLD.Codec.Nbt;

internal static class MetadataItemTests
{
    public static void Run()
    {
        TestMetadataRoundTrip();
        TestMetadataSnapshot();
        TestItemStack();
        Console.WriteLine("PASS 实体元数据与物品栈编解码");
    }

    private static void TestMetadataRoundTrip()
    {
        var nbt = new NbtCompound().Add("value", new NbtInt(7));
        var entries = new List<EntityMetadataEntry>
        {
            new(0, MetadataValueKind.Byte, (sbyte)-3),
            new(1, MetadataValueKind.Short, (short)1234),
            new(2, MetadataValueKind.Int, -56789),
            new(3, MetadataValueKind.Float, 1.25f),
            new(4, MetadataValueKind.String, "JERCPLD"),
            new(5, MetadataValueKind.CompoundTag, nbt),
            new(6, MetadataValueKind.Pos, (1, -2, 3)),
            new(7, MetadataValueKind.Int64, 1234567890123L),
            new(8, MetadataValueKind.Vec3, (1.5f, -2.5f, 3.5f)),
        };

        var bytes = EntityMetadataCodec.Write(entries);
        var parsed = EntityMetadataCodec.Read(bytes);
        Assert(parsed.Count == entries.Count, "元数据条目数量不一致");

        for (var i = 0; i < entries.Count; i++)
        {
            Assert(parsed[i].Id == entries[i].Id, $"元数据 ID 不一致: {i}");
            Assert(parsed[i].Kind == entries[i].Kind, $"元数据类型不一致: {i}");
            if (entries[i].Kind == MetadataValueKind.CompoundTag)
            {
                var expected = NbtWriter.Write((NbtTag)entries[i].Value, string.Empty, NbtEncoding.NetworkLittleEndian);
                var actual = NbtWriter.Write((NbtTag)parsed[i].Value, string.Empty, NbtEncoding.NetworkLittleEndian);
                Assert(actual.SequenceEqual(expected), $"元数据 NBT 不一致: {i}");
            }
            else
            {
                Assert(parsed[i].Value.Equals(entries[i].Value), $"元数据值不一致: {i} {parsed[i].Value} != {entries[i].Value}");
            }
        }
    }

    private static void TestMetadataSnapshot()
    {
        var snapshot = MetadataSnapshot.FromEntries(new[]
        {
            new EntityMetadataEntry(0, MetadataValueKind.Byte, (sbyte)1),
            new EntityMetadataEntry(2, MetadataValueKind.Int, 5),
        });

        snapshot.FillDefaults(new[]
        {
            new EntityMetadataEntry(0, MetadataValueKind.Byte, (sbyte)9), // 已存在，不覆盖
            new EntityMetadataEntry(1, MetadataValueKind.Float, 0.5f),
            new EntityMetadataEntry(3, MetadataValueKind.String, "default"),
        });

        Assert(snapshot.Entries.Count == 4, "默认槽补齐数量错误");
        Assert((sbyte)snapshot.Entries[0].Value == 1, "已有槽被默认值覆盖");
        Assert((float)snapshot.Entries[1].Value == 0.5f, "默认槽 1 未补齐");
        Assert((string)snapshot.Entries[3].Value == "default", "默认槽 3 未补齐");

        var bitmap = snapshot.ToCompactBitmap();
        var slots = MetadataSnapshot.ParseCompactBitmap(bitmap);
        Assert(bitmap[^1] == 0x7F, "位图缺少 0x7F 终止符");
        Assert(slots.SetEquals(new[] { 0, 1, 2, 3 }), "位图解析槽位错误");
    }

    private static void TestItemStack()
    {
        var item = new NetworkItemStack(1234, 64, 7, 42, new byte[] { 1, 2, 3 });
        var writer = new BinaryWriterEx();
        NetworkItemStackCodec.Write(writer, item);
        var reader = new BinaryReaderEx(writer.ToArray());
        var parsed = NetworkItemStackCodec.Read(ref reader);
        Assert(parsed.NetworkId == item.NetworkId && parsed.Count == item.Count
               && parsed.Metadata == item.Metadata && parsed.BlockRuntimeId == item.BlockRuntimeId,
            "物品栈字段不一致");
        Assert(parsed.Nbt.SequenceEqual(item.Nbt), "物品栈 NBT 不一致");
        Assert(reader.Remaining == 0, "物品栈尾部存在多余字节");

        var emptyReader = new BinaryReaderEx(NetworkItemStackCodec.Write(NetworkItemStack.Empty));
        var empty = NetworkItemStackCodec.Read(ref emptyReader);
        Assert(empty.IsEmpty, "空物品栈解析失败");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("FAIL " + message);
            Environment.Exit(1);
        }
    }
}
