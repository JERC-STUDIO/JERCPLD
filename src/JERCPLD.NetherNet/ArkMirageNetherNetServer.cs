using JERCPLD.Core.Logging;
using JERCPLD.Core.Transport;
using Ark = global::NetherNet;
using ArkDiscovery = global::NetherNet.Discovery;
using ArkEndpoint = global::NetherNet.Endpoint;

namespace JERCPLD.NetherNet;

/// <summary>
/// 基于 ArkMirage.NetherNet 的客户端接入监听端。
///
/// 支持两种信令：
///  - LAN 发现（UDP 7551）：Minecraft 客户端可在「局域网」列表中发现本代理；
///  - HTTP 信令：供自测客户端通过 http://host:port 加入。
///
/// 注意：这里只负责 WebRTC/NetherNet 传输，不解析 Bedrock 包。
/// </summary>
public sealed class ArkMirageNetherNetServer : IClientTransportServer
{
    private readonly NetherNetOptions _options;
    private readonly ILog _log;
    private readonly List<ArkMirageSession> _sessions = new();

    private Ark.Listener? _listener;
    private object? _signaling;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Func<IClientSession, ValueTask>? _onSessionAccepted;
    private ulong _networkId;

    public ArkMirageNetherNetServer(NetherNetOptions options, ILog log)
    {
        _options = options;
        _log = log;
    }

    public string EndPoint => _options.ListenEndPoint;

    public int ActiveSessionCount
    {
        get
        {
            lock (_sessions)
            {
                return _sessions.Count;
            }
        }
    }

    public async Task StartAsync(Func<IClientSession, ValueTask> onSessionAccepted, CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("NetherNet 监听已经启动");
        }

        _onSessionAccepted = onSessionAccepted;
        _networkId = _options.ServerNetworkId != 0 ? _options.ServerNetworkId : RandomNetworkId();

        var listenConfig = new Ark.ListenConfig
        {
            AllowAnonymous = true,
            DisableTrickleICE = true,
            Log = message => _log.Debug($"[nethernet] {message}"),
        };

        _signaling = CreateSignaling();
        _listener = await global::NetherNet.ListenConfigExtensions
            .ListenAsync(listenConfig, (Ark.ISignaling)_signaling)
            .ConfigureAwait(false);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);

        _log.Info(
            $"NetherNet 监听已启动: {_options.ListenEndPoint} "
            + $"(mode={_options.ListenMode}, networkId={_networkId:X16}, protocol={_options.ProtocolVersion})");
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Close();

        if (_signaling is ArkEndpoint.Handler handler)
        {
            handler.Close();
        }
        else if (_signaling is LanDiscoverySignaling discovery)
        {
            discovery.Close();
        }

        _signaling = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        List<ArkMirageSession> sessions;
        lock (_sessions)
        {
            sessions = _sessions.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.CloseAsync("服务器停止").ConfigureAwait(false);
        }

        _listener = null;
        _acceptLoop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private object CreateSignaling()
    {
        if (_options.ListenMode == NetherNetListenMode.LanDiscovery)
        {
            var discovery = new LanDiscoverySignaling(
                _options.ListenEndPoint,
                _networkId,
                message => _log.Debug($"[nethernet-discovery] {message}"));

            var serverData = new ArkDiscovery.ServerData
            {
                ServerName = _options.ServerName,
                Protocol = _options.ProtocolVersion,
                VersionString = _options.MinecraftVersion,
                LevelName = _options.LevelName,
                GameType = ArkDiscovery.GameType.Survival,
                PlayerCount = 0,
                MaxPlayerCount = _options.MaxPlayers,
                AcceptsOnlineAuth = false,
                AcceptsSelfSignedAuth = true,
                ConnectionType = 4,
            };

            discovery.SetServerData(serverData);
            _log.Info($"NetherNet 已启用局域网发现信令: {_options.ListenEndPoint}");
            return discovery;
        }

        var httpHandler = ArkEndpoint.Handler.New(new ArkEndpoint.HandlerConfig
        {
            NetworkID = _networkId.ToString(),
            Logger = message => _log.Debug($"[nethernet-http] {message}"),
        });

        httpHandler.Status(new ArkEndpoint.Status
        {
            ServerName = _options.ServerName,
            Protocol = _options.ProtocolVersion,
            Version = _options.MinecraftVersion,
            LevelName = _options.LevelName,
            PlayerCount = 0,
            MaxPlayerCount = _options.MaxPlayers,
            GameType = ArkEndpoint.GameTypes.Survival,
        });

        httpHandler.Start(_options.ListenEndPoint);
        _log.Info($"NetherNet 已启用 HTTP 信令: {_options.ListenEndPoint}");
        return httpHandler;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Ark.Conn conn;
            try
            {
                conn = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _log.Warn($"NetherNet 接受连接失败: {exception.Message}");
                continue;
            }

            var session = new ArkMirageSession(Guid.NewGuid().ToString("N")[..8], conn, _log);
            lock (_sessions)
            {
                _sessions.Add(session);
            }

            session.Closed += closed =>
            {
                lock (_sessions)
                {
                    _sessions.Remove(closed);
                }
            };

            session.Start();
            _log.Info($"NetherNet 客户端已连接: session={session.SessionId} remote={session.RemoteEndPoint}");

            var callback = _onSessionAccepted;
            if (callback is not null)
            {
                await callback(session).ConfigureAwait(false);
            }
        }
    }

    private static ulong RandomNetworkId()
    {
        Span<byte> buffer = stackalloc byte[8];
        Random.Shared.NextBytes(buffer);
        var value = BitConverter.ToUInt64(buffer);
        return value == 0 ? 1UL : value;
    }
}

/// <summary>NetherNet 客户端会话：把 <see cref="Ark.Conn"/> 适配成 <see cref="IClientSession"/>。</summary>
internal sealed class ArkMirageSession : IClientSession
{
    private readonly Ark.Conn _conn;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _closed;

    public ArkMirageSession(string sessionId, Ark.Conn conn, ILog log)
    {
        SessionId = sessionId;
        _conn = conn;
        _log = log;
    }

    public event Func<ReadOnlyMemory<byte>, ValueTask>? GamePacketReceived;

    public event Action<ArkMirageSession>? Closed;

    public string SessionId { get; }

    public string RemoteEndPoint => _conn.RemoteAddr()?.ToString() ?? "nethernet";

    public bool IsEstablished => !_conn.IsClosed;

    public void Start() => _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);

    public ValueTask SendGamePacketAsync(ReadOnlyMemory<byte> batchPayload, CancellationToken cancellationToken = default)
    {
        _conn.Send(batchPayload.ToArray(), Ark.MessageReliability.Reliable);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _log.Debug($"NetherNet 会话 {SessionId} 关闭: {reason}");
        _cts.Cancel();
        try
        {
            _conn.Close();
        }
        catch (Exception)
        {
            // 关闭阶段的异常不影响上层状态。
        }

        Closed?.Invoke(this);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("dispose").ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 接收循环结束时已经记录过原因。
            }
        }

        _cts.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var payload = await _conn.ReceiveAsync(Ark.MessageReliability.Reliable, _cts.Token).ConfigureAwait(false);
                var handler = GamePacketReceived;
                if (handler is not null)
                {
                    await handler(payload).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            if (!_cts.IsCancellationRequested)
            {
                _log.Warn($"NetherNet 会话 {SessionId} 接收结束: {exception.Message}");
            }
        }
        finally
        {
            await CloseAsync("接收循环结束").ConfigureAwait(false);
        }
    }
}
