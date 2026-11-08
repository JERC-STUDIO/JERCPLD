namespace JERCPLD.Protocol.RakNet;

/// <summary>
/// RakNet 帧可靠性等级。数值与线格式中的 3 bit 字段一致。
/// </summary>
public enum RakNetReliability : byte
{
    Unreliable = 0,
    UnreliableSequenced = 1,
    Reliable = 2,
    ReliableOrdered = 3,
    ReliableSequenced = 4,
    UnreliableWithAckReceipt = 5,
    ReliableWithAckReceipt = 6,
    ReliableOrderedWithAckReceipt = 7,
}

/// <summary>
/// RakNet 帧：一个数据报里可以携带多个帧。
/// </summary>
public sealed record RakNetFrame
{
    public required RakNetReliability Reliability { get; init; }

    public uint ReliableIndex { get; init; }

    public uint SequenceIndex { get; init; }

    public uint OrderIndex { get; init; }

    public byte OrderChannel { get; init; }

    public bool IsSplit { get; init; }

    public int SplitCount { get; init; }

    public ushort SplitId { get; init; }

    public int SplitIndex { get; init; }

    public required ReadOnlyMemory<byte> Payload { get; init; }

    public static RakNetFrame ReliableOrdered(uint orderIndex, ReadOnlyMemory<byte> payload, uint reliableIndex = 0)
        => new()
        {
            Reliability = RakNetReliability.ReliableOrdered,
            ReliableIndex = reliableIndex,
            OrderIndex = orderIndex,
            OrderChannel = 0,
            Payload = payload,
        };
}

/// <summary>
/// RakNet 数据报：头字节 + 3 字节小端包序号 + 若干帧。
/// </summary>
public sealed record RakNetDatagram(byte Header, uint PacketNumber, IReadOnlyList<RakNetFrame> Frames);
