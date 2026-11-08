namespace JERCPLD.Core.Transport;

/// <summary>
/// 面向旧版本客户端的传输层会话（当前由 RakNet 实现）。
/// 事件里传递的是 Bedrock 游戏包批次（0xFE 帧内部的载荷），不含 RakNet 帧头。
/// </summary>
public interface IClientSession : IAsyncDisposable
{
    string SessionId { get; }

    string RemoteEndPoint { get; }

    bool IsEstablished { get; }

    event Func<ReadOnlyMemory<byte>, ValueTask>? GamePacketReceived;

    ValueTask SendGamePacketAsync(ReadOnlyMemory<byte> batchPayload, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(string reason);
}

public interface IClientTransportServer : IAsyncDisposable
{
    string EndPoint { get; }

    /// <summary>当前活跃的客户端会话数，供 UI 显示。</summary>
    int ActiveSessionCount { get; }

    Task StartAsync(Func<IClientSession, ValueTask> onSessionAccepted, CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>
/// 面向新版 BDS 的上游连接（当前只有 NetherNet 占位实现）。
/// </summary>
public interface IServerSession : IAsyncDisposable
{
    bool IsConnected { get; }

    string RemoteEndPoint { get; }

    event Func<ReadOnlyMemory<byte>, ValueTask>? GamePacketReceived;

    ValueTask SendGamePacketAsync(ReadOnlyMemory<byte> batchPayload, CancellationToken cancellationToken = default);
}

public interface IUpstreamTransport
{
    Task<IServerSession> ConnectAsync(CancellationToken cancellationToken);
}
