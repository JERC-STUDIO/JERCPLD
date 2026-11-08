using System.Text;
using JERCPLD.Codec.Binary;
using JERCPLD.Codec.Nbt;
using JERCPLD.Protocol.Bedrock;
using JERCPLD.VersionMap.Adapters;
using JERCPLD.VersionMap.Content;

internal static class ChunkRewriteTests
{
    public static void Run()
    {
        TestLevelChunk();
        TestTerrainPalette();
        TestSubChunkPacket();
        Console.WriteLine("PASS 区块信封与 SubChunk 调色板翻译（2193 ↔ 2225）");
    }

    private static void TestLevelChunk()
    {
        var body2193 = new byte[] { 1, 2, 3, 4 };
        var body2225 = LevelChunkWireRewriter.Rewrite(body2193, 2193, 2225);
        Assert(body2225.Length == body2193.Length + 1 && body2225[^1] == 0, "LevelChunk 升级未追加布尔");
        var back = LevelChunkWireRewriter.Rewrite(body2225, 2225, 2193);
        Assert(back.SequenceEqual(body2193), "LevelChunk 往返不一致");

        TestLevelChunkPayload();
    }

    private static void TestLevelChunkPayload()
    {
        var payload = BuildTerrain();
        var writer = new BinaryWriterEx();
        writer.WriteVarUInt(0); // chunk x
        writer.WriteVarUInt(0); // chunk z
        writer.WriteVarUInt(0); // dimension
        writer.WriteVarUInt(1); // subchunk count
        writer.WriteByte(0); // request limit absent
        writer.WriteByte(0); // cache disabled
        writer.WriteVarUInt((uint)payload.Length);
        writer.WriteBytes(payload);
        writer.WriteByte(0); // 2225 Is Client Biome Update

        var rewritten = LevelChunkWireRewriter.Rewrite(writer.ToArray(), 2225, 2193, ContentFallbackMap.V2225ToV2193);
        var reader = new BinaryReaderEx(rewritten);
        reader.ReadVarUInt(); // x
        reader.ReadVarUInt(); // z
        reader.ReadVarUInt(); // dimension
        Assert(reader.ReadVarUInt() == 1, "LevelChunk 子区块数量变化");
        Assert(reader.ReadByte() == 0, "request limit 变化");
        Assert(reader.ReadByte() == 0, "cache 标志变化");
        var newPayload = reader.ReadBytes((int)reader.ReadVarUInt()).ToArray();
        Assert(reader.Remaining == 0, "LevelChunk 尾部不应再有 2225 布尔");

        var payloadReader = new BinaryReaderEx(newPayload);
        payloadReader.ReadByte(); // version
        payloadReader.ReadByte(); // layers
        payloadReader.ReadByte(); // Y
        payloadReader.ReadByte(); // header
        payloadReader.ReadBytes(512);
        Assert(payloadReader.ReadUInt32LE() == 2, "LevelChunk 调色板数量变化");
        var first = ReadPalette(payloadReader);
        Assert(GetName(first) == "minecraft:jukebox", "LevelChunk 内部调色板未降级");
    }

    private static void TestTerrainPalette()
    {
        var terrain = BuildTerrain();
        var rewritten = SubChunkTerrainRewriter.Rewrite(terrain, ContentFallbackMap.V2225ToV2193);
        Assert(!rewritten.SequenceEqual(terrain), "地形调色板未被改写");

        var reader = new BinaryReaderEx(rewritten);
        Assert(reader.ReadByte() == 9, "版本号变化");
        Assert(reader.ReadByte() == 1, "层数变化");
        _ = reader.ReadByte(); // Y
        Assert(reader.ReadByte() == 0x02, "层头变化");
        reader.ReadBytes(512); // bits=1: 128 words × 4 bytes
        Assert(reader.ReadUInt32LE() == 2, "调色板数量变化");

        var first = ReadPalette(reader);
        Assert(GetName(first) == "minecraft:jukebox", $"disc_press 未降级为 jukebox: {GetName(first)}");
        var second = ReadPalette(reader);
        Assert(GetName(second) == "minecraft:stone", "stone 名称不应变化");
        Assert(reader.Remaining == 0, "地形尾部存在多余字节");
    }

    private static void TestSubChunkPacket()
    {
        var body = BuildSubChunkPacket();
        var upgraded = SubChunkWireRewriter.Rewrite(body, 2193, 2225, ContentFallbackMap.V2225ToV2193);
        Assert(upgraded[18] == 1, $"2193->2225 结果枚举应为 1，实际 {upgraded[18]}");

        var downgraded = SubChunkWireRewriter.Rewrite(upgraded, 2225, 2193, null);
        Assert(downgraded[18] == 0, $"2225->2193 结果枚举应为 0，实际 {downgraded[18]}");
    }

    private static byte[] BuildTerrain()
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(9); // version
        writer.WriteByte(1); // layer count
        writer.WriteByte(0); // Y
        writer.WriteByte(0x02); // bits=1, palette type=0
        var indices = new byte[512];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = 0x11;
        }

        writer.WriteBytes(indices);
        writer.WriteUInt32LE(2);

        var discPress = new NbtCompound()
            .Add("name", new NbtString("minecraft:disc_press"))
            .Add("states", new NbtCompound().Add("facing_direction", new NbtInt(2)));
        writer.WriteBytes(NbtWriter.Write(discPress, string.Empty, NbtEncoding.LittleEndian));

        var stone = new NbtCompound()
            .Add("name", new NbtString("minecraft:stone"))
            .Add("states", new NbtCompound());
        writer.WriteBytes(NbtWriter.Write(stone, string.Empty, NbtEncoding.LittleEndian));

        return writer.ToArray();
    }

    private static byte[] BuildSubChunkPacket()
    {
        var terrain = BuildTerrain();
        var writer = new BinaryWriterEx();
        writer.WriteByte(0); // cache enabled
        writer.WriteByte(0); // dimension
        writer.WriteBytes(new byte[12]); // center pos
        writer.WriteVarUInt(1); // array count
        writer.WriteBytes(new byte[] { 0, 0, 0 }); // offsets
        writer.WriteByte(0); // result (2193 Success)
        writer.WriteByte(1); // terrain present
        writer.WriteVarUInt((uint)terrain.Length);
        writer.WriteBytes(terrain);
        writer.WriteByte(0); // heightmap type
        writer.WriteByte(0); // render heightmap type
        writer.WriteByte(0); // blob absent
        return writer.ToArray();
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("FAIL " + message);
            Environment.Exit(1);
        }
    }
}
