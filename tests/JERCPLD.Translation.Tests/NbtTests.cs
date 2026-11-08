using JERCPLD.Codec.Nbt;

internal static class NbtTests
{
    public static void Run()
    {
        foreach (var encoding in new[] { NbtEncoding.LittleEndian, NbtEncoding.NetworkLittleEndian })
        {
            TestRoundTrip(encoding);
        }

        TestHashOrdering();

        Console.WriteLine("PASS NBT 编解码（little-endian / network）与方块状态哈希");
    }

    private static void TestRoundTrip(NbtEncoding encoding)
    {
        var root = new NbtCompound()
            .Add("byte", new NbtByte(-7))
            .Add("short", new NbtShort(-1234))
            .Add("int", new NbtInt(123456789))
            .Add("long", new NbtLong(-1234567890123))
            .Add("float", new NbtFloat(1.5f))
            .Add("double", new NbtDouble(-2.25))
            .Add("string", new NbtString("JERCPLD 测试"))
            .Add("bytes", new NbtByteArray(new byte[] { 1, 2, 3, 255 }))
            .Add("ints", new NbtIntArray(new[] { 1, -2, 3 }))
            .Add("longs", new NbtLongArray(new[] { 1L, -2L }));

        var list = new NbtList(NbtTagType.Int);
        list.Items.Add(new NbtInt(10));
        list.Items.Add(new NbtInt(20));
        root.Add("list", list);

        root.Add("nested", new NbtCompound().Add("flag", new NbtByte(1)));

        var bytes = NbtWriter.Write(root, "root", encoding);
        var parsed = (NbtCompound)NbtReader.Read(bytes, encoding);

        Assert(((NbtByte)parsed["byte"]!).Value == -7, $"{encoding}: byte 不一致");
        Assert(((NbtShort)parsed["short"]!).Value == -1234, $"{encoding}: short 不一致");
        Assert(((NbtInt)parsed["int"]!).Value == 123456789, $"{encoding}: int 不一致");
        Assert(((NbtLong)parsed["long"]!).Value == -1234567890123, $"{encoding}: long 不一致");
        Assert(Math.Abs(((NbtFloat)parsed["float"]!).Value - 1.5f) < 0.0001, $"{encoding}: float 不一致");
        Assert(Math.Abs(((NbtDouble)parsed["double"]!).Value + 2.25) < 0.0001, $"{encoding}: double 不一致");
        Assert(((NbtString)parsed["string"]!).Value == "JERCPLD 测试", $"{encoding}: string 不一致");
        Assert(((NbtByteArray)parsed["bytes"]!).Value.SequenceEqual(new byte[] { 1, 2, 3, 255 }), $"{encoding}: byte array 不一致");
        Assert(((NbtIntArray)parsed["ints"]!).Value.SequenceEqual(new[] { 1, -2, 3 }), $"{encoding}: int array 不一致");
        Assert(((NbtLongArray)parsed["longs"]!).Value.SequenceEqual(new[] { 1L, -2L }), $"{encoding}: long array 不一致");
        Assert(((NbtList)parsed["list"]!).Items.Count == 2, $"{encoding}: list 不一致");
        Assert(((NbtCompound)parsed["nested"]!)["flag"] is NbtByte, $"{encoding}: 嵌套 compound 不一致");
    }

    private static void TestHashOrdering()
    {
        var first = new Dictionary<string, NbtTag>
        {
            ["pillar_axis"] = new NbtString("x"),
            ["age"] = new NbtInt(1),
        };
        var second = new Dictionary<string, NbtTag>
        {
            ["age"] = new NbtInt(1),
            ["pillar_axis"] = new NbtString("x"),
        };

        var hash1 = BlockStateHash.Compute("minecraft:test_block", first);
        var hash2 = BlockStateHash.Compute("minecraft:test_block", second);
        Assert(hash1 == hash2, "states 顺序不应影响哈希");
        Assert(hash1 != BlockStateHash.Compute("minecraft:test_block"), "有无 states 的哈希应不同");
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
