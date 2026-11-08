using JERCPLD.Codec.Bedrock;
using JERCPLD.Core.Diagnostics;
using JERCPLD.Core.Logging;
using JERCPLD.Core.Transport;
using JERCPLD.Protocol.Bedrock;
using JERCPLD.VersionMap;

namespace JERCPLD.Proxy;

/// <summary>
/// 翻译代理主流程：
/// RakNet 客户端会话 <-> VersionMap 翻译 <-> NetherNet 上游会话。
///
/// 当前是骨架实现：
///  - 版本路径来自 <see cref="VersionMapRouter"/>，没有注册适配器时会明确拒绝会话；
///  - NetherNet 上游是占位实现，连接必然失败并给出明确原因；
///  - 批次压缩/加密协商未实现，批次按明文处理（TODO）。
/// </summary>
public sealed class TranslationProxy : IAsyncDisposable
{
    private readonly IClientTransportServer _clientListener;
    private readonly IUpstreamTransport _upstream;
    private readonly VersionMapRouter _router;
    private readonly TranslationProxyOptions _options;
    private readonly ILog _log;
    private readonly IPacketObserver _observer;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IClientSession> _sessions = new();
    private CancellationTokenSource? _listenerCancellation;

    private TranslationRoute? _upstreamRoute;
    private TranslationRoute? _downstreamRoute;
    private int _disposed;

    public ProxyStatistics Statistics { get; } = new();

    public ProxyDiagnostics Diagnostics { get; } = new();

    /// <summary>当前活跃客户端会话数。</summary>
    public int ActiveSessions => _clientListener.ActiveSessionCount;

    public TranslationProxy(
        IClientTransportServer clientListener,
        IUpstreamTransport upstream,
        VersionMapRouter router,
        TranslationProxyOptions options,
        ILog log,
        IPacketObserver? observer = null)
    {
        _clientListener = clientListener;
        _upstream = upstream;
        _router = router;
        _options = options;
        _log = log;
        _observer = observer ?? NullPacketObserver.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _upstreamRoute = _router.TryCreateRoute(_options.ClientVersion, _options.ServerVersion, out var upstreamReason);
        _downstreamRoute = _router.TryCreateRoute(_options.ServerVersion, _options.ClientVersion, out var downstreamReason);

        if (_upstreamRoute is null)
        {
            _log.Warn($"客户端 -> BDS 翻译路径不可用: {upstreamReason}");
        }
        else
        {
            _log.Info($"客户端 -> BDS 翻译路径: {_upstreamRoute.Describe()}");
        }

        if (_downstreamRoute is null)
        {
            _log.Warn($"BDS -> 客户端翻译路径不可用: {downstreamReason}");
        }
        else
        {
            _log.Info($"BDS -> 客户端翻译路径: {_downstreamRoute.Describe()}");
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        _listenerCancellation = linked;
        await _clientListener.StartAsync(HandleClientAsync, linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        List<IClientSession> sessions;
        lock (_sessions)
        {
            sessions = _sessions.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                await session.CloseAsync("代理停止").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Debug($"关闭客户端会话失败: {exception.Message}");
            }
        }

        await _clientListener.DisposeAsync().ConfigureAwait(false);
        _listenerCancellation?.Dispose();
        _listenerCancellation = null;
        _cts.Dispose();
    }

    private async ValueTask HandleClientAsync(IClientSession client)
    {
        lock (_sessions)
        {
            _sessions.Add(client);
        }

        _log.Info($"客户端已连接: {client.RemoteEndPoint} (session={client.SessionId})");

        if (_upstreamRoute is null || _downstreamRoute is null)
        {
            await client.CloseAsync("没有可用的版本翻译路径").ConfigureAwait(false);
            return;
        }

        // 上游连接需要时间（NetherNet 信令 + WebRTC），期间客户端可能已经发出首个批次。
        // 这里先挂缓冲处理器，等上游就绪后按顺序补发，避免丢包。
        var pending = new Queue<ReadOnlyMemory<byte>>();
        var upstreamGate = new SemaphoreSlim(1, 1);
        var upstreamReady = false;
        var compression = new SessionCompressionState();
        IServerSession? upstream = null;
        var sessionId = client.SessionId;

        async ValueTask SendToUpstreamAsync(ReadOnlyMemory<byte> payload)
        {
            var translated = TranslateBatch(payload, _upstreamRoute, PacketDirection.ClientToServer, compression, sessionId);
            if (translated is not null)
            {
                await upstream!.SendGamePacketAsync(translated, _cts.Token).ConfigureAwait(false);
            }
        }

        Func<ReadOnlyMemory<byte>, ValueTask> clientHandler = async payload =>
        {
            await upstreamGate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (!upstreamReady)
                {
                    pending.Enqueue(payload.ToArray());
                    return;
                }

                await SendToUpstreamAsync(payload).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn($"客户端 -> 上游转发失败: {exception.Message}");
            }
            finally
            {
                upstreamGate.Release();
            }
        };

        client.GamePacketReceived += clientHandler;

        try
        {
            upstream = await _upstream.ConnectAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            client.GamePacketReceived -= clientHandler;
            _log.Error($"连接上游 BDS 失败: {exception.Message}");
            await client.CloseAsync("上游连接失败").ConfigureAwait(false);
            return;
        }

        var upstreamRoute = _upstreamRoute;
        var downstreamRoute = _downstreamRoute;

        upstream.GamePacketReceived += async payload =>
        {
            var translated = TranslateBatch(payload, downstreamRoute, PacketDirection.ServerToClient, compression, sessionId);
            if (translated is not null)
            {
                await client.SendGamePacketAsync(translated, _cts.Token).ConfigureAwait(false);
            }
        };

        await upstreamGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            upstreamReady = true;
            var buffered = pending.Count;
            while (pending.Count > 0)
            {
                await SendToUpstreamAsync(pending.Dequeue()).ConfigureAwait(false);
            }

            _log.Info($"上游已就绪，会话 {client.SessionId} 开始转发（缓冲补发 {buffered} 条）");
        }
        finally
        {
            upstreamGate.Release();
        }
    }

    private byte[]? TranslateBatch(
        ReadOnlyMemory<byte> batch,
        TranslationRoute route,
        PacketDirection direction,
        SessionCompressionState compression,
        string sessionId)
    {
        IReadOnlyList<BedrockPacketFrame> frames;
        try
        {
            frames = BedrockBatchCodec.DecodeBatch(batch);
        }
        catch
        {
            try
            {
                var algorithm = compression.Algorithm == BedrockCompression.None
                    ? BedrockCompression.Zlib
                    : compression.Algorithm;
                var decompressed = BedrockCompressionCodec.Decompress(batch.Span, algorithm);
                frames = BedrockBatchCodec.DecodeBatch(decompressed);
            }
            catch (Exception exception)
            {
                Statistics.CountDecodeError();
                _log.Warn($"[{direction}] 批次解析失败（压缩算法未协商或为 Snappy）: {exception.Message}");
                RecordDiagnostic(sessionId, direction, -1, "batch_error", batch.Length, 0,
                    TranslationStatus.Dropped, exception.Message);
                return null;
            }
        }

        foreach (var frame in frames)
        {
            if (frame.PacketId == BedrockPacketIds.NetworkSettings)
            {
                compression.ApplyNetworkSettings(frame.Body.Span);
            }
        }

        var output = new List<BedrockPacketFrame>(frames.Count);
        foreach (var frame in frames)
        {
            Statistics.CountSourcePacket(direction == PacketDirection.ClientToServer);
            var raw = BedrockBatchCodec.EncodePacket(frame);
            var outcome = _router.Translate(route, raw, _options.PassUnknownPackets);

            switch (outcome.Status)
            {
                case TranslationStatus.Translated:
                    Statistics.CountTranslated();
                    var translatedFrame = BedrockBatchCodec.DecodePacket(outcome.Output);
                    output.Add(translatedFrame);
                    RecordDiagnostic(sessionId, direction, frame.PacketId, BedrockPacketIds.NameOf(frame.PacketId),
                        frame.Body.Length, translatedFrame.Body.Length, TranslationStatus.Translated, "rewritten");
                    break;

                case TranslationStatus.PassedThrough:
                    Statistics.CountPassedThrough();
                    _log.Warn($"[{direction}] 包 0x{frame.PacketId:X} 原样透传: {outcome.Detail}");
                    output.Add(frame);
                    RecordDiagnostic(sessionId, direction, frame.PacketId, BedrockPacketIds.NameOf(frame.PacketId),
                        frame.Body.Length, frame.Body.Length, TranslationStatus.PassedThrough, outcome.Detail ?? string.Empty);
                    break;

                default:
                    Statistics.CountDropped();
                    _log.Warn($"[{direction}] 包 0x{frame.PacketId:X} 丢弃: {outcome.Detail}");
                    RecordDiagnostic(sessionId, direction, frame.PacketId, BedrockPacketIds.NameOf(frame.PacketId),
                        frame.Body.Length, 0, TranslationStatus.Dropped, outcome.Detail ?? string.Empty);
                    break;
            }
        }

        if (output.Count == 0)
        {
            return null;
        }

        var encoded = BedrockBatchCodec.EncodeBatch(output);

        var containsNetworkSettings = output.Any(frame => frame.PacketId == BedrockPacketIds.NetworkSettings);
        if (!containsNetworkSettings && compression.ShouldCompress(encoded.Length))
        {
            try
            {
                encoded = BedrockCompressionCodec.Compress(encoded, compression.Algorithm);
            }
            catch (NotSupportedException exception)
            {
                Statistics.CountDecodeError();
                _log.Error($"[{direction}] 无法按协商算法压缩批次: {exception.Message}");
                return null;
            }
        }

        _observer.OnPacket(PacketTrace.Create(
            direction,
            PacketLayer.VersionMap,
            $"translated batch {frames.Count} -> {output.Count} packets via {route.From.Number}->{route.To.Number}",
            encoded));
        return encoded;
    }

    private void RecordDiagnostic(
        string sessionId,
        PacketDirection direction,
        int packetId,
        string packetName,
        int inputBytes,
        int outputBytes,
        TranslationStatus status,
        string detail)
    {
        if (!_options.EnableDiagnostics)
        {
            return;
        }

        Diagnostics.Record(new PacketDiagnostic(
            DateTimeOffset.Now, sessionId, direction, packetId, packetName,
            inputBytes, outputBytes, status, detail));
    }

    /// <summary>会话级压缩状态，由双向的 NetworkSettings 包协商。</summary>
    private sealed class SessionCompressionState
    {
        public BedrockCompression Algorithm { get; private set; } = BedrockCompression.None;

        public int Threshold { get; private set; }

        public bool Negotiated { get; private set; }

        public void ApplyNetworkSettings(ReadOnlySpan<byte> body)
        {
            if (body.Length < 4)
            {
                return;
            }

            Threshold = body[0] | (body[1] << 8);
            var rawAlgorithm = body[2] | (body[3] << 8);
            Algorithm = rawAlgorithm switch
            {
                0 => BedrockCompression.Zlib,
                1 => BedrockCompression.Snappy,
                // 2193 用 2 表示 None，2225 改成 65535；两种都接受。
                2 => BedrockCompression.None,
                65535 => BedrockCompression.None,
                _ => BedrockCompression.None,
            };
            Negotiated = true;
        }

        public bool ShouldCompress(int length)
            => Negotiated
               && Algorithm != BedrockCompression.None
               && Threshold > 0
               && length >= Threshold;
    }
}
