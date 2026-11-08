namespace JERCPLD.Protocol.RakNet;

public sealed record ConnectionRequest(ulong ClientGuid, ulong Time, bool UseSecurity);

public sealed record ConnectionRequestAccepted(
    RakNetAddress ClientAddress,
    ushort SystemIndex,
    IReadOnlyList<RakNetAddress> SystemAddresses,
    ulong RequestTime,
    ulong Time);

public sealed record NewIncomingConnection(
    RakNetAddress ServerAddress,
    IReadOnlyList<RakNetAddress> InternalAddresses,
    ulong RequestTime,
    ulong Time);

public sealed record ConnectedPing(ulong Time);

public sealed record ConnectedPong(ulong PingTime, ulong PongTime);
