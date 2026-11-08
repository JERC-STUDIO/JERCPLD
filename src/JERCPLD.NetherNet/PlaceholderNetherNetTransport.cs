using JERCPLD.Core.Logging;
using JERCPLD.Core.Transport;

namespace JERCPLD.NetherNet;

public enum NetherNetBackend
{
    /// <summary>骨架阶段：不做真实连接，直接报错。</summary>
    Placeholder = 0,

    /// <summary>原型验证：使用 ArkMirage.NetherNet 打通 WebRTC / NetherNet 握手。</summary>
    ArkMirage = 1,

    /// <summary>生产实现：基于 libwebrtc 的 C# 绑定，替换 ArkMirage 原型。</summary>
    LibWebRtc = 2,
}

/// <summary>客户端接入 NetherNet 监听的信令方式。</summary>
public enum NetherNetListenMode
{
    /// <summary>局域网发现（UDP 7551），Minecraft 客户端在「好友/局域网」里能看到。</summary>
    LanDiscovery = 0,

    /// <summary>HTTP 信令（GET /v1/join），用于自测与自定义客户端。</summary>
    HttpSignaling = 1,
}

/// <summary>上游连接 BDS 的信令方式。</summary>
public enum NetherNetConnectMode
{
    /// <summary>HTTP 信令（http://host:port），用于自建服务端与自测。</summary>
    HttpSignaling = 0,

    /// <summary>局域网发现（UDP 7551），用于连接真实 BDS。</summary>
    LanDiscovery = 1,
}

public sealed class NetherNetOptions
{
    /// <summary>BDS 的 NetherNet 接入地址（HTTP 信令地址，形如 http://127.0.0.1:19132）。</summary>
    public string ConnectEndPoint { get; set; } = "http://127.0.0.1:19132";

    /// <summary>上游信令方式；连接真实 BDS 用局域网发现。</summary>
    public NetherNetConnectMode ConnectMode { get; set; } = NetherNetConnectMode.HttpSignaling;

    /// <summary>局域网发现超时。</summary>
    public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public NetherNetBackend Backend { get; set; } = NetherNetBackend.Placeholder;

    /// <summary>BDS 的 server id / 证书标识；具体字段名待按 NetherNet 协议核对。</summary>
    public string ServerId { get; set; } = string.Empty;

    // ---- 监听端参数 ----

    /// <summary>监听地址；LAN 发现模式下是 UDP 地址（默认 0.0.0.0:7551），HTTP 模式下是 TCP 地址。</summary>
    public string ListenEndPoint { get; set; } = "0.0.0.0:7551";

    public NetherNetListenMode ListenMode { get; set; } = NetherNetListenMode.LanDiscovery;

    /// <summary>监听端 NetworkID；0 表示随机生成。</summary>
    public ulong ServerNetworkId { get; set; }

    public int ProtocolVersion { get; set; } = 2225;

    public string MinecraftVersion { get; set; } = "1.26.60";

    public string ServerName { get; set; } = "JERCPLD 跨版本代理";

    public string LevelName { get; set; } = "JERCPLD";

    public int MaxPlayers { get; set; } = 100;
}

/// <summary>
/// NetherNet 上游传输的占位实现。
///
/// 设计路线（详见 docs/DESIGN.md）：
///  1. 先接入 ArkMirage.NetherNet 做协议原型验证：跑通 NetherNet 信令、WebRTC DataChannel 与游戏包收发。
///  2. 原型稳定后，把 <see cref="IUpstreamTransport"/> 的实现替换为 libwebrtc 的 C# 绑定，
///     上层 Proxy / VersionMap 不需要任何改动。
/// </summary>
public sealed class PlaceholderNetherNetTransport : IUpstreamTransport
{
    private readonly NetherNetOptions _options;
    private readonly ILog _log;

    public PlaceholderNetherNetTransport(NetherNetOptions options, ILog log)
    {
        _options = options;
        _log = log;
    }

    public Task<IServerSession> ConnectAsync(CancellationToken cancellationToken)
    {
        _log.Warn($"NetherNet 传输尚未实现，无法连接上游 {_options.ConnectEndPoint}（backend={_options.Backend}）");
        return Task.FromException<IServerSession>(new NotSupportedException(
            "NetherNet 传输尚未实现。TODO: 先用 ArkMirage.NetherNet 完成原型验证，生产环境再替换为 libwebrtc。"));
    }
}

public static class NetherNetTransportFactory
{
    /// <summary>按配置创建上游传输。</summary>
    public static IUpstreamTransport Create(NetherNetOptions options, ILog log)
    {
        return options.Backend switch
        {
            NetherNetBackend.ArkMirage => new ArkMirageNetherNetTransport(options, log),
            _ => new PlaceholderNetherNetTransport(options, log),
        };
    }

    /// <summary>按配置创建客户端监听端；RakNet 监听端不在这里创建。</summary>
    public static IClientTransportServer CreateListener(NetherNetOptions options, ILog log)
        => new ArkMirageNetherNetServer(options, log);
}
