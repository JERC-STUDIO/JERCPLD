namespace JERCPLD.Protocol.RakNet;

public sealed record UnconnectedPing(ulong Time, ulong ClientGuid, bool OpenConnections);

public sealed record UnconnectedPong(ulong Time, ulong ServerGuid, string Motd);

public sealed record OpenConnectionRequest1(byte RakNetProtocolVersion, int DatagramLength);

public sealed record OpenConnectionReply1(ulong ServerGuid, int Mtu);

public sealed record OpenConnectionRequest2(RakNetAddress ServerAddress, int Mtu, ulong ClientGuid);

public sealed record OpenConnectionReply2(ulong ServerGuid, RakNetAddress ClientAddress, int Mtu, bool EncryptionEnabled);

/// <summary>
/// 服务器列表 MOTD。wire 格式为分号分隔的 12 段：
/// MCPE;motd;protocol;version;players;maxPlayers;serverGuid;levelName;gameMode;gameModeId;portV4;portV6;
/// TODO(bedrock): 新版本可能追加字段（如 IPv6 端口、Nintendo 限制等），解析需要按官方实现补齐。
/// </summary>
public sealed record ServerMotd(
    string Motd,
    int Protocol,
    string Version,
    int Players,
    int MaxPlayers,
    ulong ServerGuid,
    string LevelName,
    string GameMode,
    int GameModeId,
    int PortV4,
    int PortV6)
{
    public string ToWireString()
    {
        return string.Join(
            ';',
            "MCPE",
            Motd,
            Protocol,
            Version,
            Players,
            MaxPlayers,
            ServerGuid,
            LevelName,
            GameMode,
            GameModeId,
            PortV4,
            PortV6,
            string.Empty);
    }

    public static ServerMotd? TryParse(string text)
    {
        var parts = text.Split(';');
        if (parts.Length < 12 || !string.Equals(parts[0], "MCPE", StringComparison.Ordinal))
        {
            return null;
        }

        if (!int.TryParse(parts[2], out var protocol) ||
            !int.TryParse(parts[4], out var players) ||
            !int.TryParse(parts[5], out var maxPlayers) ||
            !ulong.TryParse(parts[6], out var guid) ||
            !int.TryParse(parts[9], out var gameModeId) ||
            !int.TryParse(parts[10], out var portV4) ||
            !int.TryParse(parts[11], out var portV6))
        {
            return null;
        }

        return new ServerMotd(parts[1], protocol, parts[3], players, maxPlayers, guid, parts[7], parts[8], gameModeId, portV4, portV6);
    }
}
