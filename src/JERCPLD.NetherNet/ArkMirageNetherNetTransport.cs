using JERCPLD.Core.Logging;
using JERCPLD.Core.Transport;
using Ark = global::NetherNet;
using ArkEndpoint = global::NetherNet.Endpoint;

namespace JERCPLD.NetherNet;

/// <summary>
/// 基于 ArkMirage.NetherNet 的上行传输：通过 HTTP 信令拨号到目标 NetherNet 服务端。
///
/// 当前约束：
///  - 只支持 HTTP 信令（BDS 原生信令方式尚未核实）；
///  - 不做身份签名（AllowIdentitylessServer = true）。
/// </summary>
public sealed class ArkMirageNetherNetTransport : IUpstreamTransport
{
    private readonly NetherNetOptions _options;
    private readonly ILog _log;

    public ArkMirageNetherNetTransport(NetherNetOptions options, ILog log)
    {
        _options = options;
        _log = log;
    }

    public async Task<IServerSession> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_options.Backend != NetherNetBackend.ArkMirage)
        {
            throw new NotSupportedException($"NetherNet 后端 {_options.Backend} 尚未实现");
        }

        var dialer = new Ark.Dialer
        {
            DisableTrickleICE = true,
            AllowIdentitylessServer = true,
            Log = message => _log.Debug($"[nethernet] {message}"),
        };

        Ark.Conn conn;
        LanDiscoveryClientSignaling? lanSignaling = null;
        if (_options.ConnectMode == NetherNetConnectMode.LanDiscovery)
        {
            var signaling = new LanDiscoveryClientSignaling(message => _log.Debug($"[nethernet-discovery] {message}"));
            lanSignaling = signaling;
            try
            {
                _log.Info($"NetherNet 正在通过局域网发现查找 BDS（UDP 7551，超时 {_options.DiscoveryTimeout.TotalSeconds:0.#}s）");
                var serverData = await signaling.DiscoverAsync(_options.DiscoveryTimeout, cancellationToken)
                    .ConfigureAwait(false);
                _log.Info(
                    $"已发现 BDS: id=0x{signaling.ServerId:X16} endpoint={signaling.ServerEndPoint} "
                    + $"name={serverData.ServerName} protocol={serverData.Protocol} version={serverData.VersionString}");

                conn = await dialer.DialContextAsync(
                        cancellationToken,
                        signaling.ServerId.ToString(),
                        signaling)
                    .ConfigureAwait(false);
            }
            catch
            {
                signaling.Close();
                throw;
            }
        }
        else
        {
            var address = NormalizeAddress(_options.ConnectEndPoint);
            var client = ArkEndpoint.Client.New();
            _log.Info($"NetherNet 正在连接上游: {address}");
            conn = await dialer.DialContextAsync(cancellationToken, address, client).ConfigureAwait(false);
        }

        _log.Info($"NetherNet 上游已连接: remote={conn.RemoteAddr()}");
        Action? closeSignaling = lanSignaling is null ? null : lanSignaling.Close;
        return new ArkMirageServerSession(conn, _log, closeSignaling);
    }

    private static string NormalizeAddress(string address)
    {
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        return "http://" + address;
    }
}

/// <summary>NetherNet 上游会话：把 <see cref="Ark.Conn"/> 适配成 <see cref="IServerSession"/>。</summary>
internal sealed class ArkMirageServerSession : IServerSession
{
    private readonly Ark.Conn _conn;
    private readonly ILog _log;
    private readonly Action? _onDispose;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _closed;

    public ArkMirageServerSession(Ark.Conn conn, ILog log, Action? onDispose = null)
    {
        _conn = conn;
        _log = log;
        _onDispose = onDispose;
        _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    public event Func<ReadOnlyMemory<byte>, ValueTask>? GamePacketReceived;

    public bool IsConnected => !_conn.IsClosed;

    public string RemoteEndPoint => _conn.RemoteAddr()?.ToString() ?? "nethernet";

    public ValueTask SendGamePacketAsync(ReadOnlyMemory<byte> batchPayload, CancellationToken cancellationToken = default)
    {
        _conn.Send(batchPayload.ToArray(), Ark.MessageReliability.Reliable);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _cts.Cancel();
            try
            {
                _conn.Close();
            }
            catch (Exception)
            {
                // 关闭阶段忽略。
            }
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 接收循环内部已记录。
            }
        }

        _cts.Dispose();
        _onDispose?.Invoke();
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
                _log.Warn($"NetherNet 上游接收结束: {exception.Message}");
            }
        }
    }
}
