namespace JERCPLD.Proxy;

/// <summary>
/// 代理运行期统计。所有计数使用 Interlocked，可安全地从 UI 线程轮询。
/// </summary>
public sealed class ProxyStatistics
{
    private long _packetsClientToServer;
    private long _packetsServerToClient;
    private long _translated;
    private long _passedThrough;
    private long _dropped;
    private long _decodeErrors;

    public long PacketsClientToServer => Interlocked.Read(ref _packetsClientToServer);

    public long PacketsServerToClient => Interlocked.Read(ref _packetsServerToClient);

    public long PacketsTotal => PacketsClientToServer + PacketsServerToClient;

    public long Translated => Interlocked.Read(ref _translated);

    public long PassedThrough => Interlocked.Read(ref _passedThrough);

    public long Dropped => Interlocked.Read(ref _dropped);

    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);

    internal void CountSourcePacket(bool clientToServer)
    {
        Interlocked.Increment(ref clientToServer ? ref _packetsClientToServer : ref _packetsServerToClient);
    }

    internal void CountTranslated() => Interlocked.Increment(ref _translated);

    internal void CountPassedThrough() => Interlocked.Increment(ref _passedThrough);

    internal void CountDropped() => Interlocked.Increment(ref _dropped);

    internal void CountDecodeError() => Interlocked.Increment(ref _decodeErrors);
}
