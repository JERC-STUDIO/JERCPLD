using System.Net;
using System.Net.Sockets;
using Ark = global::NetherNet;
using ArkDiscovery = global::NetherNet.Discovery;

namespace JERCPLD.NetherNet;

/// <summary>
/// 自实现的 NetherNet 局域网发现信令（UDP 7551）。
///
/// 不使用 ArkMirage.NetherNet.Discovery.Listener：该版本（1.0.0）的 Listener 构造函数是私有的，
/// 公开的 Listen() 又是实例方法，外部无法创建实例（库缺陷）。
/// 这里基于其公开的 PacketCodec / Packet 类型复刻同样的线格式。
/// </summary>
public sealed class LanDiscoverySignaling : Ark.ISignaling
{
    private const int DefaultPort = 7551;

    private readonly Socket _socket;
    private readonly ulong _networkId;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<ulong, IPEndPoint> _addresses = new();
    private readonly Dictionary<uint, Ark.INotifier> _notifiers = new();
    private readonly object _gate = new();

    private byte[] _pongData = Array.Empty<byte>();
    private uint _notifierCount;
    private int _closed;

    public LanDiscoverySignaling(string listenAddress, ulong networkId, Action<string>? log = null)
    {
        _networkId = networkId;
        _log = log;

        var endPoint = ParseAddress(listenAddress);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };
        _socket.Bind(endPoint);

        _ = Task.Run(ListenLoop);
        _ = Task.Run(BroadcastLoopAsync);
    }

    public CancellationToken Context => _cts.Token;

    public Exception? ContextCause { get; private set; }

    public void SetServerData(ArkDiscovery.ServerData serverData)
        => _pongData = serverData.MarshalBinary();

    /// <summary>接口要求；服务器信息通过 <see cref="SetServerData"/> 显式设置，这里不重复解析。</summary>
    public void PongData(byte[] data)
    {
    }

    public string NetworkID() => _networkId.ToString();

    public Task<Ark.Credentials?> CredentialsAsync(CancellationToken ctx)
        => Task.FromResult<Ark.Credentials?>(null);

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

    public Task SignalAsync(CancellationToken ctx, Ark.Signal signal)
    {
        if (!ulong.TryParse(signal.NetworkID, out var remoteId))
        {
            throw new Ark.NetherNetException($"invalid network ID: {signal.NetworkID}");
        }

        IPEndPoint? remote;
        lock (_gate)
        {
            _addresses.TryGetValue(remoteId, out remote);
        }

        if (remote is null)
        {
            throw new Ark.NetherNetException($"no address for network ID: {remoteId}");
        }

        Send(new ArkDiscovery.MessagePacket { RecipientID = remoteId, Data = signal.ToString() }, remote);
        return Task.CompletedTask;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        ContextCause = new Ark.NetherNetException("nethernet: discovery listener is closed");
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

                _log?.Invoke($"discovery receive failed: {exception.Message}");
            }
        }
    }

    private async Task BroadcastLoopAsync()
    {
        var broadcast = new IPEndPoint(IPAddress.Broadcast, DefaultPort);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false);
                Send(new ArkDiscovery.RequestPacket(), broadcast);
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

                _log?.Invoke($"discovery broadcast failed: {exception.Message}");
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
            _log?.Invoke($"discovery decode failed: {exception.Message}");
            return;
        }

        if (senderId == _networkId)
        {
            return;
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _addresses[senderId] = remote;
        }

        switch (packet)
        {
            case ArkDiscovery.RequestPacket:
                if (_pongData.Length > 0)
                {
                    Send(new ArkDiscovery.ResponsePacket { ApplicationData = _pongData }, remote);
                }

                break;

            case ArkDiscovery.ResponsePacket:
                break;

            case ArkDiscovery.MessagePacket message:
                if (message.RecipientID != _networkId || message.Data is "Ping" or "")
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
                    _log?.Invoke($"discovery signal decode failed: {exception.Message}");
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

                break;
        }
    }

    private void Send(ArkDiscovery.Packet packet, EndPoint remote)
    {
        var bytes = ArkDiscovery.PacketCodec.Marshal(packet, _networkId);
        _socket.SendTo(bytes, SocketFlags.None, remote);
    }

    private static IPEndPoint ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new IPEndPoint(IPAddress.Any, DefaultPort);
        }

        if (address.StartsWith(':'))
        {
            return new IPEndPoint(IPAddress.Any, int.Parse(address[1..]));
        }

        return IPEndPoint.Parse(address);
    }
}
