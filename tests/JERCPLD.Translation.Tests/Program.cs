using JERCPLD.Codec.Bedrock;
using JERCPLD.Codec.Binary;
using JERCPLD.Core;
using JERCPLD.Protocol.Bedrock;
using JERCPLD.VersionMap;
using JERCPLD.VersionMap.Adapters;

var v2193 = KnownProtocolVersions.V1_26_50;
var v2225 = KnownProtocolVersions.V1_26_60;

// ---- Login ----
var loginBody2225 = new byte[] { 0x00, 0x00, 0x08, 0xB1, 0xDE, 0xAD, 0xBE, 0xEF }; // 2225 + payload
var loginFrame2225 = new BedrockPacketFrame(BedrockPacketIds.Login, loginBody2225);
var loginDown = new LoginStartGameAdapter(v2193).Encode(new LoginStartGameAdapter(v2225).Decode(loginFrame2225));
Assert(ReadInt32Be(loginDown.Body.Span) == 2193, $"Login 下行协议号应为 2193，实际 {ReadInt32Be(loginDown.Body.Span)}");
Assert(loginDown.Body.Span[4..].SequenceEqual(loginBody2225[4..]), "Login 下行其余字节被改动");
var loginUp = new LoginStartGameAdapter(v2225).Encode(new LoginStartGameAdapter(v2193).Decode(loginDown));
Assert(loginUp.Body.Span.SequenceEqual(loginBody2225), "Login 上行往返不一致");
Console.WriteLine("PASS Login 2225 <-> 2193");

// ---- StartGame ----
var start2225 = BuildStartGame2225();
var startFrame2225 = new BedrockPacketFrame(BedrockPacketIds.StartGame, start2225);
var startDownFrame = new LoginStartGameAdapter(v2193).Encode(new LoginStartGameAdapter(v2225).Decode(startFrame2225));
var startDown = startDownFrame.Body.ToArray();
Assert(startDown.Length == start2225.Length - 1, $"StartGame 下行长度应为 {start2225.Length - 1}，实际 {startDown.Length}");
Assert(startDown[^3..].SequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC }), "StartGame 尾部未保留");
var startUpFrame = new LoginStartGameAdapter(v2225).Encode(new LoginStartGameAdapter(v2193).Decode(startDownFrame));
Assert(startUpFrame.Body.Span.SequenceEqual(start2225), "StartGame 往返不一致");
Console.WriteLine("PASS StartGame 2225 -> 2193 -> 2225");

// ---- Router ----
var registry = new VersionMapRegistry();
registry.Register(new LoginStartGameAdapter(v2193));
registry.Register(new LoginStartGameAdapter(v2225));
var router = new VersionMapRouter(registry);
var route = router.TryCreateRoute(v2225, v2193, out var reason);
Assert(route is not null, $"路由创建失败: {reason}");

var encodedLogin = BedrockBatchCodec.EncodePacket(loginFrame2225);
var outcome = router.Translate(route!, encodedLogin, passUnknownPackets: true);
Assert(outcome.Status == TranslationStatus.Translated, $"Login 路由结果: {outcome.Status} {outcome.Detail}");
var outFrame = BedrockBatchCodec.DecodePacket(outcome.Output);
Assert(ReadInt32Be(outFrame.Body.Span) == 2193, "路由后 Login 协议号错误");

var unsupported = BedrockBatchCodec.EncodePacket(new BedrockPacketFrame(BedrockPacketIds.Text, new byte[] { 1, 2, 3 }));
var passthrough = router.Translate(route!, unsupported, passUnknownPackets: true);
Assert(passthrough.Status == TranslationStatus.PassedThrough, $"未支持包应为透传，实际 {passthrough.Status}");
Console.WriteLine("PASS Router 2225 -> 2193（Login 翻译，Text 透传）");

await ProxyCompressionTest.RunAsync(v2193, v2225);
EncryptionTests.Run();
EncryptionBridgeTests.Run();
ContentFallbackTests.Run();
NbtTests.Run();
BlockStateTableTests.Run();
ChunkRewriteTests.Run();
MetadataItemTests.Run();
OfficialSampleSelfCheckTests.Run();

Console.WriteLine("ALL PASS");

static byte[] BuildStartGame2225()
{
    var w = new BinaryWriterEx();
    w.WriteVarUInt(0x01); // ActorUniqueID
    w.WriteVarUInt(0x02); // ActorRuntimeID
    w.WriteVarInt(0); // Game Type: Survival(2225)=0
    w.WriteSingleLE(1.5f);
    w.WriteSingleLE(64f);
    w.WriteSingleLE(-2.25f);
    w.WriteSingleLE(90f);
    w.WriteSingleLE(-45f);
    w.WriteUInt64LE(0x1122334455667788UL); // Seed
    w.WriteBytes(new byte[] { 0x00, 0x00 }); // Spawn Biome Type
    w.WriteString(string.Empty); // User Defined Biome Name
    w.WriteVarUInt(0); // Dimension
    w.WriteVarUInt(1); // Generator Type: Overworld
    w.WriteVarInt(0); // LevelSettings Game Type: Survival(2225)=0
    w.WriteByte(0); // Is Hardcore
    w.WriteVarUInt(2); // Difficulty: Normal
    w.WriteVarUInt(0); // BlockPos.X
    w.WriteVarUInt(0x80); w.WriteByte(0x01); // BlockPos.Y = 64 (zigzag 128)
    w.WriteVarUInt(0); // BlockPos.Z
    w.WriteByte(1); // Achievements Disabled
    w.WriteVarUInt(0); // Editor World Type
    w.WriteByte(0); // Is Created In Editor
    w.WriteByte(0); // Is Exported From Editor
    w.WriteByte(0); // Editor Level Migration Version (2225 only)
    w.WriteBytes(new byte[] { 0xAA, 0xBB, 0xCC }); // 后续字段占位
    return w.ToArray();
}

static int ReadInt32Be(ReadOnlySpan<byte> span)
    => (span[0] << 24) | (span[1] << 16) | (span[2] << 8) | span[3];

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine("FAIL " + message);
        Environment.Exit(1);
    }
}
