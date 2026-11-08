namespace JERCPLD.RakNet;

/// <summary>
/// RakNet 监听参数。监听侧对外宣告的协议版本就是旧版本客户端使用的版本。
/// </summary>
public sealed class RakNetServerOptions
{
    public string ListenEndPoint { get; set; } = "0.0.0.0:19132";

    public int MinMtu { get; set; } = Protocol.RakNet.RakNetConstants.MinMtu;

    public int MaxMtu { get; set; } = Protocol.RakNet.RakNetConstants.DefaultMtu;

    public int ProtocolVersion { get; set; } = 671;

    public string MinecraftVersion { get; set; } = "1.20.80";

    public string Motd { get; set; } = "JERCPLD 跨版本代理";

    public string LevelName { get; set; } = "JERCPLD";

    public string GameMode { get; set; } = "Survival";

    public int GameModeId { get; set; }

    public int MaxPlayers { get; set; } = 100;

    /// <summary>0 表示每次启动随机生成。</summary>
    public ulong ServerGuid { get; set; }

    /// <summary>MOTD 中宣告的端口；0 表示使用实际监听端口。</summary>
    public int AdvertisedPort { get; set; }
}
