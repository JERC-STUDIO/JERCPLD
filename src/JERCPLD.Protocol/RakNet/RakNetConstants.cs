namespace JERCPLD.Protocol.RakNet;

/// <summary>
/// RakNet / Bedrock 的固定常量。
/// </summary>
public static class RakNetConstants
{
    /// <summary>RakNet offline magic：00ffff00fefefefefdfdfdfd12345678。</summary>
    public static readonly byte[] Magic =
    {
        0x00, 0xFF, 0xFF, 0x00, 0xFE, 0xFE, 0xFE, 0xFE,
        0xFD, 0xFD, 0xFD, 0xFD, 0x12, 0x34, 0x56, 0x78,
    };

    public const int MagicLength = 16;

    /// <summary>Bedrock 游戏包批次在 RakNet 可靠帧中的首字节。</summary>
    public const byte GamePacketId = 0xFE;

    public const int MinMtu = 576;
    public const int DefaultMtu = 1400;
    public const int MaxMtu = 1492;
}

public static class RakNetMessageId
{
    public const byte ConnectedPing = 0x00;
    public const byte UnconnectedPing = 0x01;
    public const byte UnconnectedPingOpenConnections = 0x02;
    public const byte ConnectedPong = 0x03;
    public const byte OpenConnectionRequest1 = 0x05;
    public const byte OpenConnectionReply1 = 0x06;
    public const byte OpenConnectionRequest2 = 0x07;
    public const byte OpenConnectionReply2 = 0x08;
    public const byte ConnectionRequest = 0x09;
    public const byte ConnectionRequestAccepted = 0x10;
    public const byte NewIncomingConnection = 0x13;
    public const byte DisconnectNotification = 0x15;
    public const byte UnconnectedPong = 0x1C;

    public const byte Ack = 0xC0;
    public const byte Nack = 0xA0;

    /// <summary>数据报头的 valid 位（0x80）。</summary>
    public const byte DatagramValid = 0x80;

    /// <summary>Bedrock 常见数据报头 0x84 中的附加位。</summary>
    public const byte DatagramFlag = 0x04;
}
