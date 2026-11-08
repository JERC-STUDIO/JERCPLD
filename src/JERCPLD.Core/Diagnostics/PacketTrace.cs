namespace JERCPLD.Core.Diagnostics;

public enum PacketDirection
{
    ClientToServer = 0,
    ServerToClient = 1,
}

public enum PacketLayer
{
    RakNet = 0,
    Bedrock = 1,
    VersionMap = 2,
}

/// <summary>
/// 抓包观察点。RakNet 层与翻译层都通过它把原始字节送给抓包工具，便于离线分析。
/// </summary>
public sealed record PacketTrace(
    DateTimeOffset Timestamp,
    PacketDirection Direction,
    PacketLayer Layer,
    string Summary,
    ReadOnlyMemory<byte> Payload)
{
    public static PacketTrace Create(
        PacketDirection direction,
        PacketLayer layer,
        string summary,
        ReadOnlyMemory<byte> payload)
        => new(DateTimeOffset.Now, direction, layer, summary, payload);
}

public interface IPacketObserver
{
    void OnPacket(PacketTrace trace);
}

public sealed class NullPacketObserver : IPacketObserver
{
    public static readonly NullPacketObserver Instance = new();

    private NullPacketObserver()
    {
    }

    public void OnPacket(PacketTrace trace)
    {
    }
}

public sealed class DelegatePacketObserver : IPacketObserver
{
    private readonly Action<PacketTrace> _callback;

    public DelegatePacketObserver(Action<PacketTrace> callback) => _callback = callback;

    public void OnPacket(PacketTrace trace) => _callback(trace);
}
