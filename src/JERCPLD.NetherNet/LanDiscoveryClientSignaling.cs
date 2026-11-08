using System.Net;
using System.Net.Sockets;
using Ark = global::NetherNet;
using ArkDiscovery = global::NetherNet.Discovery;

namespace JERCPLD.NetherNet;

/// <summary>
/// NetherNet 局域网发现客户端信令：通过 UDP 7551 广播 RequestPacket 找到 BDS，
/// 然后用 MessagePacket 承载 CONNECTREQUEST/CONNECTRESPONSE/CANDIDATEADD 信令。
/// </summary>
public sealed class LanDiscoveryClientSignaling : Ark.ISignaling
{
    private const int DiscoveryPort = 7551;

    private readonly Socket _socket;
    private readonly ulong _localId;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<uint, Ark.INotifier> _notifiers = new();
    private readonly TaskCompletionSource<ArkDiscovery.ServerData> _discovered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private ulong _serverId;
    private IPEndPoint? _serverEndPoint;
    private uint _notifierCount;
    private int _closed;

    public LanDiscoveryClientSignaling(Action<string>? log = null)
    {
        _log = log;
        _localId = RandomUInt64();

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        _ = Task.Run(ListenLoop);
        _ = Task.Run(BroadcastLoopAsync);
    }

    public CancellationToken Context => _cts.Token;

    public Exception? ContextCause { get; private set; }

    public string NetworkID() => _localId.ToString();

    public ulong ServerId => _serverId;

    public IPEndPoint? ServerEndPoint => _serverEndPoint;

    public Task<Ark.Credentials?> CredentialsAsync(CancellationToken ctx)
        => Task.FromResult<Ark.Credentials?>(null);

    /// <summary>接口要求；客户端不使用该回调。</summary>
    public void PongData(byte[] data)
    {
    }

    public Action Notify(Ark.INotifier notifier)
    {
        lock (_gate)
        {
            var index = _notifierCount;
            _notifiers[index] = notifier;
            _notifierCount++;
            return () =>
            {
                lock (_gate)
                {
                    _notifiers.Remove(index);
                }
            };
        }
    }

    /// <summary>等待发现 BDS；返回其 NetworkID 与服务器信息。</summary>
    public async Task<ArkDiscovery.ServerData> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var delay = Task.Delay(timeout, linked.Token);
        var completed = await Task.WhenAny(_discovered.Task, delay).ConfigureAwait(false);
        if (completed != _discovered.Task)
        {
            throw new TimeoutException($"在 {timeout.TotalSeconds:0.#} 秒内未发现 NetherNet 服务器（UDP {DiscoveryPort}）");
        }

        return await _discovered.Task.ConfigureAwait(false);
    }

    public Task SignalAsync(CancellationToken ctx, Ark.Signal signal)
    {
        if (!ulong.TryParse(signal.NetworkID, out var remoteId))
        {
            throw new Ark.NetherNetException($"invalid network ID: {signal.NetworkID}");
        }

        var endpoint = _serverEndPoint
            ?? throw new Ark.NetherNetException("尚未发现 NetherNet 服务器地址");
        if (remoteId != _serverId)
        {
            throw new Ark.NetherNetException($"信令目标 {remoteId} 与已发现服务器 {_serverId} 不一致");
        }

        Send(new ArkDiscovery.MessagePacket { RecipientID = remoteId, Data = signal.ToString() }, endpoint);
        return Task.CompletedTask;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        ContextCause = new Ark.NetherNetException("nethernet: discovery client is closed");
        try
        {
            _cts.Cancel();
            _socket.Close();
        }
        catch (Exception)
        {
            // 关闭阶段忽略。
        }

        lock (_gate)
        {
            _notifiers.Clear();
        }

        _cts.Dispose();
    }

    private async Task BroadcastLoopAsync()
    {
        var target = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Send(new ArkDiscovery.RequestPacket(), target);
                await Task.Delay(TimeSpan.FromMilliseconds(500), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                if (Volatile.Read(ref _closed) != 0)
                {
                    return;
                }

                _log?.Invoke($"discovery client broadcast failed: {exception.Message}");
            }
        }
    }

    private void ListenLoop()
    {
        var buffer = new byte[65535];
        while (Volatile.Read(ref _closed) == 0)
        {
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var length = _socket.ReceiveFrom(buffer, ref from);
                if (from is not IPEndPoint remote)
                {
                    continue;
                }

                HandleDatagram(buffer.AsSpan(0, length).ToArray(), remote);
            }
            catch (Exception exception)
            {
                if (Volatile.Read(ref _closed) != 0)
                {
                    return;
                }

                _log?.Invoke($"discovery client receive failed: {exception.Message}");
            }
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint remote)
    {
        ArkDiscovery.Packet packet;
        ulong senderId;
        try
        {
            (packet, senderId) = ArkDiscovery.PacketCodec.Unmarshal(data);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"discovery client decode failed: {exception.Message}");
            return;
        }

        if (senderId == _localId)
        {
            return;
        }

        if (packet is ArkDiscovery.ResponsePacket response)
        {
            if (_serverEndPoint is null)
            {
                _serverId = senderId;
                _serverEndPoint = remote;
                try
                {
                    var serverData = ArkDiscovery.ServerData.UnmarshalBinary(response.ApplicationData);
                    _log?.Invoke(
                        $"discovered NetherNet server: id=0x{senderId:X16} endpoint={remote} "
                        + $"name={serverData.ServerName} protocol={serverData.Protocol}");
                    _discovered.TrySetResult(serverData);
                }
                catch (Exception exception)
                {
                    _log?.Invoke($"discovery client server data parse failed: {exception.Message}");
                }
            }

            return;
        }

        if (packet is ArkDiscovery.MessagePacket message)
        {
            if (message.RecipientID != _localId || message.Data is "Ping" or "")
            {
                return;
            }

            Ark.Signal signal;
            try
            {
                signal = Ark.Signal.Parse(message.Data);
            }
            catch (Exception exception)
            {
                _log?.Invoke($"discovery client signal decode failed: {exception.Message}");
                return;
            }

            signal.NetworkID = senderId.ToString();
            List<Ark.INotifier> notifiers;
            lock (_gate)
            {
                notifiers = _notifiers.Values.ToList();
            }

            foreach (var notifier in notifiers)
            {
                notifier.NotifySignal(signal);
            }
        }
    }

    private void Send(ArkDiscovery.Packet packet, EndPoint remote)
    {
        var bytes = ArkDiscovery.PacketCodec.Marshal(packet, _localId);
        _socket.SendTo(bytes, SocketFlags.None, remote);
    }

    private static ulong RandomUInt64()
    {
        Span<byte> buffer = stackalloc byte[8];
        Random.Shared.NextBytes(buffer);
        var value = BitConverter.ToUInt64(buffer);
        return value == 0 ? 1UL : value;
    }
}
