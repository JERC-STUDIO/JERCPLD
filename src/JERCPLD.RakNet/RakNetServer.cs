using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using JERCPLD.Codec.RakNet;
using JERCPLD.Core.Diagnostics;
using JERCPLD.Core.Logging;
using JERCPLD.Core.Networking;
using JERCPLD.Core.Transport;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.RakNet;

/// <summary>
/// 面向旧版本客户端的 RakNet UDP 监听器。
///
/// 已实现（仅限骨架范围）：
///  - Unconnected Ping / Ping Open Connections 回应；
///  - OpenConnectionRequest1/2 与 OpenConnectionReply1/2 的 MTU 协商；
///  - 在线握手 ConnectionRequest -> ConnectionRequestAccepted -> NewIncomingConnection；
///  - 可靠有序帧的基本收发与 ACK 生成；
///  - 0xFE 游戏包批次的透传扩展点。
///
/// 未实现（明确 TODO）：
///  - RakNet 加密（Xbox Live / 客户端加密开关）；
///  - 分片帧重组、NAK 与丢包重传、拥塞控制；
///  - IPv6 地址；
///  - 客户端与 BDS 之间的流量整形。
/// </summary>
public sealed class RakNetServer : IClientTransportServer
{
    private readonly RakNetServerOptions _options;
    private readonly ILog _log;
    private readonly IPacketObserver _observer;
    private readonly ConcurrentDictionary<string, RakNetSession> _sessions = new(StringComparer.Ordinal);

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Func<IClientSession, ValueTask>? _onSessionAccepted;
    private ulong _serverGuid;
    private int _boundPort;

    public RakNetServer(RakNetServerOptions options, ILog log, IPacketObserver? observer = null)
    {
        _options = options;
        _log = log;
        _observer = observer ?? NullPacketObserver.Instance;
    }

    public string EndPoint => _options.ListenEndPoint;

    public int ActiveSessionCount => _sessions.Count;

    public Task StartAsync(Func<IClientSession, ValueTask> onSessionAccepted, CancellationToken cancellationToken)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("RakNetServer 已经启动");
        }

        _onSessionAccepted = onSessionAccepted;
        var endPoint = EndpointParser.Parse(_options.ListenEndPoint, 19132);
        _socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(endPoint);
        _boundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _serverGuid = _options.ServerGuid != 0 ? _options.ServerGuid : GenerateServerGuid();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _log.Info($"RakNet 监听已启动: {endPoint} (protocol={_options.ProtocolVersion}, serverGuid={_serverGuid:X16})");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _socket?.Dispose();
        _socket = null;

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var sessions = _sessions.Values.ToList();
        _sessions.Clear();
        foreach (var session in sessions)
        {
            await session.CloseAsync("服务器停止").ConfigureAwait(false);
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var buffer = new byte[2048];
        EndPoint remoteTemplate = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _log.Warn($"UDP 接收失败: {exception.SocketErrorCode}");
                continue;
            }

            var remote = (IPEndPoint)result.RemoteEndPoint;
            // 复制一份，避免会话持有接收缓冲区的引用。
            var packet = buffer.AsMemory(0, result.ReceivedBytes).ToArray();
            try
            {
                await HandlePacketAsync(remote, packet, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Error($"处理来自 {remote} 的数据报失败", exception);
            }
        }
    }

    private async ValueTask HandlePacketAsync(IPEndPoint remote, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        var key = KeyOf(remote);

        if (_sessions.TryGetValue(key, out var session))
        {
            if (RakNetDatagramCodec.IsAck(packet.Span) || RakNetDatagramCodec.IsNack(packet.Span))
            {
                session.HandleAck(packet);
                return;
            }

            if ((packet.Span[0] & RakNetMessageId.DatagramValid) != 0)
            {
                await session.HandleDatagramAsync(packet, cancellationToken).ConfigureAwait(false);
                return;
            }

            _log.Debug($"会话 {remote} 收到未识别的在线包 0x{packet.Span[0]:X2}");
            return;
        }

        if (packet.Length == 0)
        {
            return;
        }

        switch (packet.Span[0])
        {
            case RakNetMessageId.UnconnectedPing:
            case RakNetMessageId.UnconnectedPingOpenConnections:
                await HandleUnconnectedPingAsync(remote, packet, cancellationToken).ConfigureAwait(false);
                break;

            case RakNetMessageId.OpenConnectionRequest1:
                await HandleOpenConnectionRequest1Async(remote, packet, cancellationToken).ConfigureAwait(false);
                break;

            case RakNetMessageId.OpenConnectionRequest2:
                await HandleOpenConnectionRequest2Async(remote, packet, cancellationToken).ConfigureAwait(false);
                break;

            default:
                _log.Debug($"来自 {remote} 的未知离线包 0x{packet.Span[0]:X2}");
                break;
        }
    }

    private async ValueTask HandleUnconnectedPingAsync(
        IPEndPoint remote,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        var ping = OfflineMessageCodec.DecodeUnconnectedPing(packet);
        var advertisedPort = _options.AdvertisedPort != 0 ? _options.AdvertisedPort : _boundPort;
        var motd = new ServerMotd(
            _options.Motd,
            _options.ProtocolVersion,
            _options.MinecraftVersion,
            0,
            _options.MaxPlayers,
            _serverGuid,
            _options.LevelName,
            _options.GameMode,
            _options.GameModeId,
            advertisedPort,
            advertisedPort);

        var pong = OfflineMessageCodec.EncodeUnconnectedPong(
            new UnconnectedPong(ping.Time, _serverGuid, motd.ToWireString()));

        await SendRawAsync(remote, pong, cancellationToken).ConfigureAwait(false);
        _observer.OnPacket(PacketTrace.Create(
            PacketDirection.ClientToServer,
            PacketLayer.RakNet,
            $"UnconnectedPing from {remote}",
            packet));
        _log.Debug($"已回应 UnconnectedPing: {remote}");
    }

    private async ValueTask HandleOpenConnectionRequest1Async(
        IPEndPoint remote,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        var request = OfflineMessageCodec.DecodeOpenConnectionRequest1(packet);
        var mtu = Math.Clamp(request.DatagramLength, _options.MinMtu, _options.MaxMtu);
        var reply = OfflineMessageCodec.EncodeOpenConnectionReply1(new OpenConnectionReply1(_serverGuid, mtu));
        await SendRawAsync(remote, reply, cancellationToken).ConfigureAwait(false);
        _log.Debug($"MTU 协商: {remote} 请求 {request.DatagramLength} -> 接受 {mtu} (RakNet 协议版本 {request.RakNetProtocolVersion})");
    }

    private async ValueTask HandleOpenConnectionRequest2Async(
        IPEndPoint remote,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        var request = OfflineMessageCodec.DecodeOpenConnectionRequest2(packet);
        var mtu = Math.Clamp(request.Mtu, _options.MinMtu, _options.MaxMtu);

        var key = KeyOf(remote);
        if (!_sessions.TryGetValue(key, out var session))
        {
            session = CreateSession(remote, request.ClientGuid, mtu);
            _sessions[key] = session;
        }

        var reply = OfflineMessageCodec.EncodeOpenConnectionReply2(
            new OpenConnectionReply2(_serverGuid, RakNetAddress.FromEndPoint(remote), mtu, false));
        await SendRawAsync(remote, reply, cancellationToken).ConfigureAwait(false);
        _log.Info($"RakNet 会话建立中: {remote} (clientGuid={request.ClientGuid:X16}, mtu={mtu})");
    }

    private RakNetSession CreateSession(IPEndPoint remote, ulong clientGuid, int mtu)
    {
        var key = KeyOf(remote);
        var session = new RakNetSession(
            Guid.NewGuid().ToString("N")[..8],
            remote,
            clientGuid,
            mtu,
            _options,
            _log,
            _observer,
            (datagram, ct) => SendRawAsync(remote, datagram, ct));

        session.Established += async establishedSession =>
        {
            var callback = _onSessionAccepted;
            if (callback is not null)
            {
                await callback(establishedSession).ConfigureAwait(false);
            }
        };

        session.Closed += closedSession =>
        {
            _sessions.TryRemove(key, out _);
            _log.Info($"RakNet 会话已移除: {closedSession.RemoteEndPoint}");
        };

        return session;
    }

    private async ValueTask SendRawAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        await socket.SendToAsync(payload, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
    }

    private static string KeyOf(IPEndPoint endPoint) => $"{endPoint.Address}:{endPoint.Port}";

    private static ulong GenerateServerGuid()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}
