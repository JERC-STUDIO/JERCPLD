using System.Net;
using JERCPLD.Codec.Bedrock;
using JERCPLD.Codec.Binary;
using JERCPLD.Codec.RakNet;
using JERCPLD.Core.Diagnostics;
using JERCPLD.Core.Logging;
using JERCPLD.Core.Transport;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.RakNet;

/// <summary>
/// 单个 RakNet 客户端会话：负责握手状态机、可靠有序收发和 ACK。
/// 所有协议细节中未实现的部分都以 TODO 标注，不做静默兜底。
/// </summary>
internal sealed class RakNetSession : IClientSession
{
    private readonly IPEndPoint _remote;
    private readonly ulong _clientGuid;
    private readonly int _mtu;
    private readonly RakNetServerOptions _options;
    private readonly ILog _log;
    private readonly IPacketObserver _observer;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendRaw;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _orderGate = new();
    private readonly Dictionary<uint, RakNetFrame> _orderedBuffer = new();

    private uint _expectedOrderIndex;
    private uint _nextPacketNumber;
    private uint _nextReliableIndex;
    private uint _nextOrderIndex;
    private bool _established;
    private bool _closed;

    public RakNetSession(
        string sessionId,
        IPEndPoint remote,
        ulong clientGuid,
        int mtu,
        RakNetServerOptions options,
        ILog log,
        IPacketObserver observer,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendRaw)
    {
        SessionId = sessionId;
        _remote = remote;
        _clientGuid = clientGuid;
        _mtu = mtu;
        _options = options;
        _log = log;
        _observer = observer;
        _sendRaw = sendRaw;
    }

    public string SessionId { get; }

    public string RemoteEndPoint => _remote.ToString();

    public bool IsEstablished => _established;

    public event Func<ReadOnlyMemory<byte>, ValueTask>? GamePacketReceived;

    internal event Func<RakNetSession, ValueTask>? Established;

    internal event Action<RakNetSession>? Closed;

    internal async ValueTask HandleDatagramAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (!RakNetDatagramCodec.TryRead(packet, out var datagram) || datagram is null)
        {
            _log.Warn($"无法解析来自 {RemoteEndPoint} 的 RakNet 数据报");
            return;
        }

        _observer.OnPacket(PacketTrace.Create(
            PacketDirection.ClientToServer,
            PacketLayer.RakNet,
            $"datagram #{datagram.PacketNumber} frames={datagram.Frames.Count}",
            packet));

        await SendAckAsync(datagram.PacketNumber, cancellationToken).ConfigureAwait(false);

        foreach (var frame in datagram.Frames)
        {
            if (frame.IsSplit)
            {
                // TODO(raknet): 分片帧重组尚未实现，当前直接丢弃以避免产生错误状态。
                _log.Warn($"收到 RakNet 分片帧 (split {frame.SplitIndex + 1}/{frame.SplitCount})，骨架阶段丢弃");
                continue;
            }

            if (IsOrdered(frame.Reliability))
            {
                await HandleOrderedFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ProcessFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal void HandleAck(ReadOnlyMemory<byte> packet)
    {
        try
        {
            var ranges = RakNetDatagramCodec.ReadAckRanges(packet);
            var summary = string.Join(", ", ranges.Select(range => range.Start == range.End
                ? range.Start.ToString()
                : $"{range.Start}-{range.End}"));
            _log.Debug($"收到 ACK/NAK: {summary}");
            // TODO(raknet): 未确认帧的超时重传与 NAK 处理尚未实现。
            // 当前握手阶段依赖客户端自行重发 ConnectionRequest。
        }
        catch (Exception exception)
        {
            _log.Warn($"解析 ACK 失败: {exception.Message}");
        }
    }

    public async ValueTask SendGamePacketAsync(ReadOnlyMemory<byte> batchPayload, CancellationToken cancellationToken = default)
    {
        if (!_established)
        {
            throw new InvalidOperationException("RakNet 会话尚未完成握手，不能发送游戏包");
        }

        var frame = BedrockGamePacketCodec.Wrap(batchPayload);
        await SendReliableOrderedAsync(frame, cancellationToken).ConfigureAwait(false);
        _observer.OnPacket(PacketTrace.Create(
            PacketDirection.ServerToClient,
            PacketLayer.Bedrock,
            $"game batch -> {RemoteEndPoint} ({batchPayload.Length} bytes)",
            batchPayload));
    }

    public async ValueTask CloseAsync(string reason)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            var frame = new RakNetFrame
            {
                Reliability = RakNetReliability.Unreliable,
                Payload = HandshakeCodec.EncodeDisconnectNotification(),
            };

            byte[] datagram;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                datagram = RakNetDatagramCodec.Write(_nextPacketNumber++, new[] { frame });
            }
            finally
            {
                _sendLock.Release();
            }

            await _sendRaw(datagram, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Debug($"发送 DisconnectNotification 失败（可忽略）: {exception.Message}");
        }

        _log.Info($"RakNet 会话关闭: {RemoteEndPoint} ({reason})");
        Closed?.Invoke(this);
    }

    public ValueTask DisposeAsync() => CloseAsync("会话释放");

    private async ValueTask HandleOrderedFrameAsync(RakNetFrame frame, CancellationToken cancellationToken)
    {
        while (true)
        {
            uint expected;
            lock (_orderGate)
            {
                expected = _expectedOrderIndex;
            }

            if (frame.OrderIndex == expected)
            {
                await ProcessFrameAsync(frame, cancellationToken).ConfigureAwait(false);

                RakNetFrame? next = null;
                lock (_orderGate)
                {
                    _expectedOrderIndex++;
                    if (_orderedBuffer.Remove(_expectedOrderIndex, out var buffered))
                    {
                        next = buffered;
                    }
                }

                if (next is null)
                {
                    return;
                }

                frame = next;
                continue;
            }

            if (frame.OrderIndex > expected)
            {
                // 乱序到达：先缓存，等缺口补齐。
                lock (_orderGate)
                {
                    _orderedBuffer[frame.OrderIndex] = frame;
                }

                return;
            }

            // 重复或过期帧。
            return;
        }
    }

    private async ValueTask ProcessFrameAsync(RakNetFrame frame, CancellationToken cancellationToken)
    {
        var payload = frame.Payload;
        if (payload.Length == 0)
        {
            return;
        }

        var messageId = payload.Span[0];
        switch (messageId)
        {
            case RakNetMessageId.ConnectionRequest:
                await HandleConnectionRequestAsync(payload, cancellationToken).ConfigureAwait(false);
                break;

            case RakNetMessageId.NewIncomingConnection:
                await HandleNewIncomingConnectionAsync(payload).ConfigureAwait(false);
                break;

            case RakNetMessageId.ConnectedPing:
                var ping = HandshakeCodec.DecodeConnectedPing(new BinaryReaderEx(payload));
                await SendReliableOrderedAsync(
                    HandshakeCodec.EncodeConnectedPong(new ConnectedPong(ping.Time, (ulong)Environment.TickCount64)),
                    cancellationToken).ConfigureAwait(false);
                break;

            case RakNetMessageId.DisconnectNotification:
                await CloseAsync("客户端发送 DisconnectNotification").ConfigureAwait(false);
                break;

            case RakNetConstants.GamePacketId:
                await HandleGamePacketAsync(payload).ConfigureAwait(false);
                break;

            default:
                _log.Debug($"会话 {RemoteEndPoint} 收到未处理的消息 0x{messageId:X2}");
                break;
        }
    }

    private async ValueTask HandleConnectionRequestAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_established)
        {
            return;
        }

        var request = HandshakeCodec.DecodeConnectionRequest(new BinaryReaderEx(payload));
        _log.Info($"收到 ConnectionRequest: {RemoteEndPoint} clientGuid={request.ClientGuid:X16} security={request.UseSecurity}");

        // TODO(raknet): systemAddresses 应填写本机可达地址（含 IPv6），当前用 10 个 0.0.0.0:0 占位。
        var systemAddresses = Enumerable.Repeat(new RakNetAddress(System.Net.IPAddress.Any, 0), HandshakeCodec.SystemAddressCount)
            .ToList();

        var accepted = new ConnectionRequestAccepted(
            RakNetAddress.FromEndPoint(_remote),
            0,
            systemAddresses,
            request.Time,
            (ulong)Environment.TickCount64);

        await SendReliableOrderedAsync(HandshakeCodec.EncodeConnectionRequestAccepted(accepted), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask HandleNewIncomingConnectionAsync(ReadOnlyMemory<byte> payload)
    {
        if (_established)
        {
            return;
        }

        _ = HandshakeCodec.DecodeNewIncomingConnection(new BinaryReaderEx(payload));
        _established = true;
        _log.Info($"RakNet 握手完成: {RemoteEndPoint} (session={SessionId}, clientGuid={_clientGuid:X16}, mtu={_mtu})");

        var handler = Established;
        if (handler is null)
        {
            return;
        }

        foreach (var callback in handler.GetInvocationList())
        {
            await ((Func<RakNetSession, ValueTask>)callback)(this).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleGamePacketAsync(ReadOnlyMemory<byte> payload)
    {
        if (!_established)
        {
            _log.Warn($"会话 {RemoteEndPoint} 在握手完成前收到游戏包，已丢弃");
            return;
        }

        if (!BedrockGamePacketCodec.TryUnwrap(payload, out var batch))
        {
            _log.Warn($"0xFE 游戏包帧格式非法，长度 {payload.Length}");
            return;
        }

        _observer.OnPacket(PacketTrace.Create(
            PacketDirection.ClientToServer,
            PacketLayer.Bedrock,
            $"game batch from {RemoteEndPoint} ({batch.Length} bytes)",
            batch));
        // TODO(bedrock): 批次可能经过 zlib/snappy 压缩，压缩算法由 NetworkSettings 协商。
        // 骨架阶段把原始批次直接交给上层。

        var handlers = GamePacketReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            await ((Func<ReadOnlyMemory<byte>, ValueTask>)handler)(batch).ConfigureAwait(false);
        }
    }

    private async ValueTask SendReliableOrderedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] datagram;
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = new RakNetFrame
            {
                Reliability = RakNetReliability.ReliableOrdered,
                ReliableIndex = _nextReliableIndex++,
                OrderIndex = _nextOrderIndex++,
                OrderChannel = 0,
                Payload = payload,
            };
            datagram = RakNetDatagramCodec.Write(_nextPacketNumber++, new[] { frame });
        }
        finally
        {
            _sendLock.Release();
        }

        if (datagram.Length > _mtu)
        {
            // TODO(raknet): MTU 超限时应拆分为多个帧，当前仅告警。
            _log.Warn($"发送数据报 {datagram.Length} 字节超过 MTU {_mtu}，未做分片");
        }

        await _sendRaw(datagram, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendAckAsync(uint packetNumber, CancellationToken cancellationToken)
    {
        var ack = RakNetDatagramCodec.WriteAck(new[] { (packetNumber, packetNumber) });
        await _sendRaw(ack, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsOrdered(RakNetReliability reliability)
        => reliability is RakNetReliability.ReliableOrdered
            or RakNetReliability.ReliableSequenced
            or RakNetReliability.ReliableOrderedWithAckReceipt;
}
