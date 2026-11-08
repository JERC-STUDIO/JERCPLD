using System.Text;
using JERCPLD.Codec.Bedrock.Metadata;
using JERCPLD.Codec.Binary;
using JERCPLD.Codec.Nbt;
using JERCPLD.VersionMap.Adapters;
using JERCPLD.VersionMap.Content;

internal static class OfficialSampleSelfCheckTests
{
    public static void Run()
    {
        TestSetActorData();
        TestOfficialPaletteRoundTrip();
        Console.WriteLine("PASS 官方样例自检（SetActorData + 方块表往返）");
    }

    private static void TestSetActorData()
    {
        var writer = new BinaryWriterEx();
        writer.WriteVarULong(12345); // runtime id
        writer.WriteVarUInt(2); // metadata count
        writer.WriteVarUInt(2);
        writer.WriteByte((byte)MetadataValueKind.String);
        writer.WriteString("Steve");
        writer.WriteVarUInt(100);
        writer.WriteByte((byte)MetadataValueKind.Int);
        writer.WriteVarInt(7);
        writer.WriteBytes(new byte[] { 0xAA, 0xBB }); // Synched Properties / Tick 占位

        var rewritten = SetActorDataWireRewriter.Rewrite(
            writer.ToArray(), 2225, 2193, MetadataSlotMap.V2225ToV2193);
        var reader = new BinaryReaderEx(rewritten);
        Assert(reader.ReadVarULong() == 12345, "SetActorData runtime id 变化");

        var remaining = reader.PeekRemaining();
        Assert(EntityMetadataCodec.TryRead(remaining.Span, out var entries, out var consumed), "重写后的元数据无法解析");
        reader.ReadBytes(consumed);

        Assert(entries.Count >= 7, $"默认槽未补齐，实际 {entries.Count} 条");
        Assert(entries.Any(entry => entry.Id == 2 && (string)entry.Value == "Steve"), "slot 2 字符串丢失");
        Assert(entries.Any(entry => entry.Id == 100 && (int)entry.Value == 7), "未知 slot 100 丢失");
        for (var slot = 0; slot <= 6; slot++)
        {
            Assert(entries.Any(entry => entry.Id == slot), $"默认 slot {slot} 缺失");
        }

        Assert(reader.ReadRemaining().Span.SequenceEqual(new byte[] { 0xAA, 0xBB }), "SetActorData 尾部数据被改动");
    }

    private static void TestOfficialPaletteRoundTrip()
    {
        var root = FindRepoRoot();
        var source = BlockStateTable.Load(Path.Combine(root, "data", "generated", "block_states.2225.json"));
        var target = BlockStateTable.Load(Path.Combine(root, "data", "generated", "block_states.2193.json"));
        var mapper = new BlockStateMapper(source, target, ContentFallbackMap.V2225ToV2193);

        var stone = source.GetByName("minecraft:stone").Single();
        var redstone = source.GetByName("minecraft:redstone_wire")
            .First(entry => entry.States.TryGetValue("redstone_signal", out var value) && (int)value == 7);
        var discPress = source.GetByName("minecraft:disc_press").First();

        // 自检：官方状态重新计算哈希应与生成表一致。
        foreach (var entry in new[] { stone, redstone, discPress })
        {
            var computed = BlockStateHash.Compute(entry.Name, ToNbtStateDictionary(entry.States));
            Assert(computed == entry.Hash, $"{entry.Name} 哈希与表不一致: {computed} != {entry.Hash}");
        }

        var palette = new[] { stone, redstone, discPress };
        var terrain = BuildTerrain(palette);
        var rewritten = SubChunkTerrainRewriter.Rewrite(terrain, ContentFallbackMap.V2225ToV2193);
        Assert(!rewritten.SequenceEqual(terrain), "官方样例调色板未被改写");

        var reader = new BinaryReaderEx(rewritten);
        reader.ReadByte(); // version
        reader.ReadByte(); // layers
        reader.ReadByte(); // Y
        var header = reader.ReadByte();
        var bits = header >> 1;
        var indicesPerBlock = 32 / bits;
        var wordCount = (4096 + indicesPerBlock - 1) / indicesPerBlock;
        reader.ReadBytes(wordCount * 4);
        Assert(reader.ReadUInt32LE() == 3, "调色板数量变化");

        var names = new List<string>();
        var states = new List<Dictionary<string, object>>();
        for (var i = 0; i < 3; i++)
        {
            var tag = ReadPalette(reader);
            names.Add(GetName(tag));
            states.Add(GetStates(tag));
        }

        Assert(names[0] == "minecraft:stone", "stone 名称变化");
        Assert(names[1] == "minecraft:redstone_wire", "redstone_wire 名称变化");
        Assert(names[2] == "minecraft:jukebox", "disc_press 未降级为 jukebox");
        Assert((int)states[1]["redstone_signal"] == 7, "redstone_signal 状态丢失");
        Assert(states[2].Count == 0, "jukebox 不应保留 disc_press 状态");

        // 再解析一次映射结果，确认目标哈希存在。
        var mapped = mapper.Map(discPress.Hash);
        Assert(target.TryGet(mapped.TargetHash, out var targetEntry), "映射后的目标哈希不在 2193 表中");
        Assert(targetEntry.Name == "minecraft:jukebox", "目标哈希对应方块错误");
    }

    private static byte[] BuildTerrain(IReadOnlyList<BlockStateEntry> palette)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(9);
        writer.WriteByte(1);
        writer.WriteByte(0);
        writer.WriteByte(0x04); // bits=2, persistence palette
        var indices = new byte[1024]; // 2 bits: 16 per word, 256 words
        writer.WriteBytes(indices);
        writer.WriteUInt32LE((uint)palette.Count);

        foreach (var entry in palette)
        {
            var compound = new NbtCompound()
                .Add("name", new NbtString(entry.Name))
                .Add("states", ToNbtStates(entry.States));
            writer.WriteBytes(NbtWriter.Write(compound, string.Empty, NbtEncoding.LittleEndian));
        }

        return writer.ToArray();
    }

    private static Dictionary<string, NbtTag> ToNbtStateDictionary(IReadOnlyDictionary<string, object> states)
    {
        var result = new Dictionary<string, NbtTag>(StringComparer.Ordinal);
        foreach (var pair in ToNbtStates(states).Entries)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static NbtCompound ToNbtStates(IReadOnlyDictionary<string, object> states)
    {
        var compound = new NbtCompound();
        foreach (var pair in states.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            compound.Add(pair.Key, pair.Value switch
            {
                bool flag => new NbtByte(flag ? (sbyte)1 : (sbyte)0),
                int number => new NbtInt(number),
                string text => new NbtString(text),
                _ => new NbtString(pair.Value.ToString() ?? string.Empty),
            });
        }

        return compound;
    }

    private static Dictionary<string, object> GetStates(NbtTag tag)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (tag is not NbtCompound root || root["states"] is not NbtCompound states)
        {
            return result;
        }

        foreach (var entry in states.Entries)
        {
            result[entry.Key] = entry.Value switch
            {
                NbtByte value => value.Value != 0,
                NbtInt value => value.Value,
                NbtString value => value.Value,
                _ => entry.Value.ToString() ?? string.Empty,
            };
        }

        return result;
    }

    private static NbtTag ReadPalette(BinaryReaderEx reader)
    {
        var remaining = reader.PeekRemaining();
        Assert(NbtReader.TryReadRoot(remaining.Span, out var tag, out var consumed, NbtEncoding.LittleEndian),
            "调色板 NBT 解析失败");
        reader.ReadBytes(consumed);
        return tag;
    }

    private static string GetName(NbtTag tag)
        => tag is NbtCompound compound && compound["name"] is NbtString name ? name.Value : string.Empty;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JERCPLD.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到 JERCPLD.sln");
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
