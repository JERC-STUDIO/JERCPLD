using JERCPLD.Codec.Bedrock;
using JERCPLD.Codec.Binary;
using JERCPLD.Core;
using JERCPLD.Core.Logging;
using JERCPLD.Core.Transport;
using JERCPLD.Protocol.Bedrock;
using JERCPLD.Proxy;
using JERCPLD.VersionMap;
using JERCPLD.VersionMap.Adapters;

internal static class ProxyCompressionTest
{
    public static async Task RunAsync(ProtocolVersion v2193, ProtocolVersion v2225)
    {
        var registry = new VersionMapRegistry();
        registry.Register(new LoginStartGameAdapter(v2193));
        registry.Register(new LoginStartGameAdapter(v2225));

        var listener = new FakeClientServer();
        var upstream = new FakeUpstream();
        await using var proxy = new TranslationProxy(
            listener,
            upstream,
            new VersionMapRouter(registry),
            new TranslationProxyOptions
            {
                ClientVersion = v2225,
                ServerVersion = v2193,
                PassUnknownPackets = true,
            },
            NullLog.Instance);

        await proxy.StartAsync();
        var client = new FakeClientSession();
        await listener.AcceptAsync(client);

        var server = upstream.Session ?? throw new InvalidOperationException("上行会话未建立");

        // 1) 服务端下发 NetworkSettings（压缩算法 ZLib，阈值 1），代理应解压并透传给客户端。
        var networkSettings = BuildNetworkSettings(threshold: 1, algorithm: 0);
        var networkSettingsBatch = BedrockBatchCodec.EncodeBatch(new[]
        {
            new BedrockPacketFrame(BedrockPacketIds.NetworkSettings, networkSettings),
        });
        var compressedSettings = BedrockCompressionCodec.Compress(networkSettingsBatch, BedrockCompression.Zlib);
        await server.EmitAsync(compressedSettings);
        Assert(client.Sent.Count == 1, "客户端未收到 NetworkSettings");
        Assert(Contains(client.Sent[0], BedrockPacketIds.NetworkSettings), "NetworkSettings 包丢失");
        Assert(!IsZlib(client.Sent[0]), "NetworkSettings 不应被压缩");

        // 2) 客户端发送压缩的 Login(2225)，代理应解压、改写为 2193、再压缩发给上游。
        var login = BuildLogin(2225);
        var loginBatch = BedrockBatchCodec.EncodeBatch(new[]
        {
            new BedrockPacketFrame(BedrockPacketIds.Login, login),
        });
        var compressedLogin = BedrockCompressionCodec.Compress(loginBatch, BedrockCompression.Zlib);
        await client.EmitAsync(compressedLogin);
        Assert(server.Sent.Count == 1, "上游未收到 Login");

        var upstreamPlain = IsZlib(server.Sent[0])
            ? BedrockCompressionCodec.Decompress(server.Sent[0], BedrockCompression.Zlib)
            : server.Sent[0];
        var translated = BedrockBatchCodec.DecodeBatch(upstreamPlain);
        Assert(translated.Count == 1 && translated[0].PacketId == BedrockPacketIds.Login, "上游收到的不是 Login");
        var protocol = (translated[0].Body.Span[0] << 24) | (translated[0].Body.Span[1] << 16)
                       | (translated[0].Body.Span[2] << 8) | translated[0].Body.Span[3];
        Assert(protocol == 2193, $"Login 协议号应为 2193，实际 {protocol}");
        Assert(IsZlib(server.Sent[0]), "阈值=1 时上行批次应被压缩");

        Console.WriteLine("PASS Proxy 压缩协商 + Login 翻译 + 重压缩");
    }

    private static byte[] BuildNetworkSettings(ushort threshold, ushort algorithm)
    {
        var w = new BinaryWriterEx();
        w.WriteByte((byte)threshold);
        w.WriteByte((byte)(threshold >> 8));
        w.WriteByte((byte)algorithm);
        w.WriteByte((byte)(algorithm >> 8));
        w.WriteByte(0); // throttle disabled
        w.WriteByte(0);
        w.WriteSingleLE(0f);
        return w.ToArray();
    }

    private static byte[] BuildLogin(int protocol)
    {
        var w = new BinaryWriterEx();
        w.WriteByte((byte)(protocol >> 24));
        w.WriteByte((byte)(protocol >> 16));
        w.WriteByte((byte)(protocol >> 8));
        w.WriteByte((byte)protocol);
        w.WriteString("{}");
        return w.ToArray();
    }

    private static bool Contains(byte[] batch, int packetId)
        => BedrockBatchCodec.DecodeBatch(batch).Any(frame => frame.PacketId == packetId);

    private static bool IsZlib(byte[] data) => data.Length > 2 && data[0] == 0x78;

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("FAIL " + message);
            Environment.Exit(1);
        }
    }
}

internal sealed class FakeClientServer : IClientTransportServer
{
    private Func<IClientSession, ValueTask>? _callback;

    public string EndPoint => "fake";

    public int ActiveSessionCount => 1;

    public Task StartAsync(Func<IClientSession, ValueTask> onSessionAccepted, CancellationToken cancellationToken)
    {
        _callback = onSessionAccepted;
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask AcceptAsync(IClientSession session) => _callback!(session);
}

internal sealed class FakeClientSession : IClientSession
{
    public event Func<ReadOnlyMemory<byte>, ValueTask>? GamePacketReceived;

    public List<byte[]> Sent { get; } = new();

    public string SessionId => "fake-client";

    public string RemoteEndPoint => "127.0.0.1:0";

    public bool IsEstablished => true;

    public ValueTask SendGamePacketAsync(ReadOnlyMemory<byte> batchPayload, CancellationToken cancellationToken = default)
    {
        Sent.Add(batchPayload.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask EmitAsync(byte[] payload) => GamePacketReceived!(payload);
}

internal sealed class FakeUpstream : IUpstreamTransport
{
    public FakeServerSession? Session { get; private set; }

    public Task<IServerSession> ConnectAsync(CancellationToken cancellationToken)
    {
        Session = new FakeServerSession();
        return Task.FromResult<IServerSession>(Session);
    }
}

internal sealed class FakeServerSession : IServerSession
{
    public event Func<ReadOnlyMemory<byte>, ValueTask>? GamePacketReceived;

    public List<byte[]> Sent { get; } = new();

    public bool IsConnected => true;

    public string RemoteEndPoint => "fake-server";

    public ValueTask SendGamePacketAsync(ReadOnlyMemory<byte> batchPayload, CancellationToken cancellationToken = default)
    {
        Sent.Add(batchPayload.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask EmitAsync(byte[] payload) => GamePacketReceived!(payload);
}
