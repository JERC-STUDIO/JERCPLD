using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JERCPLD.Core;
using JERCPLD.Core.Logging;
using JERCPLD.Core.Transport;
using JERCPLD.Desktop.Models;
using JERCPLD.Desktop.Services;
using JERCPLD.NetherNet;
using JERCPLD.Proxy;
using JERCPLD.RakNet;
using JERCPLD.VersionMap;
using JERCPLD.VersionMap.Adapters;
using JERCPLD.VersionMap.Content;

namespace JERCPLD.Desktop.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const int MaxLogEntries = 600;

    private readonly UiLog _log;
    private readonly DispatcherTimer _statisticsTimer;
    private TranslationProxy? _proxy;

    [ObservableProperty]
    private VersionOption? _selectedClientVersion;

    [ObservableProperty]
    private VersionOption? _selectedServerVersion;

    [ObservableProperty]
    private string _listenEndPoint = "0.0.0.0:7551";

    [ObservableProperty]
    private string _upstreamEndPoint = "http://127.0.0.1:19132";

    [ObservableProperty]
    private string _motd = "JERCPLD 跨版本代理（P1 直通）";

    [ObservableProperty]
    private string _maxPlayersText = "100";

    [ObservableProperty]
    private bool _passthroughMode;

    [ObservableProperty]
    private int _selectedTransportIndex; // 0 = NetherNet, 1 = RakNet

    [ObservableProperty]
    private int _selectedListenModeIndex; // 0 = LAN 发现, 1 = HTTP 信令

    [ObservableProperty]
    private int _selectedUpstreamModeIndex; // 0 = LAN 发现, 1 = HTTP 信令

    [ObservableProperty]
    private bool _diagnosticsEnabled = true;

    [ObservableProperty]
    private int _diagnosticCount;

    [ObservableProperty]
    private string _lastDiagnosticsPath = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "已停止";

    [ObservableProperty]
    private string _routeSummary = string.Empty;

    [ObservableProperty]
    private int _activeSessions;

    [ObservableProperty]
    private long _packetsTotal;

    [ObservableProperty]
    private long _translatedCount;

    [ObservableProperty]
    private long _passedThroughCount;

    [ObservableProperty]
    private long _droppedCount;

    [ObservableProperty]
    private long _decodeErrors;

    public MainWindowViewModel()
    {
        ClientVersions = new ObservableCollection<VersionOption>(
            KnownProtocolVersions.SupportedRange.Select(version => new VersionOption(version)));
        ServerVersions = new ObservableCollection<VersionOption>(
            KnownProtocolVersions.SupportedRange.Select(version => new VersionOption(version)));

        SelectedClientVersion = ClientVersions.FirstOrDefault(option => option.Version.MinecraftVersion == "1.26.60")
            ?? ClientVersions.LastOrDefault();
        SelectedServerVersion = ServerVersions.FirstOrDefault(option => option.Version.MinecraftVersion == "1.26.50")
            ?? ServerVersions.FirstOrDefault();

        _log = new UiLog();
        _log.EntryAdded += OnLogEntryAdded;
        Logs = new ObservableCollection<LogEntry>();

        _statisticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statisticsTimer.Tick += (_, _) => RefreshStatistics();
        _statisticsTimer.Start();

        UpdateRouteSummary();
        _log.Info("JERCPLD 桌面版已启动（P1：直通模式，真实版本翻译尚未启用）");
    }

    public ObservableCollection<VersionOption> ClientVersions { get; }

    public ObservableCollection<VersionOption> ServerVersions { get; }

    public ObservableCollection<LogEntry> Logs { get; }

    public IReadOnlyList<string> TransportModes { get; } = new[]
    {
        "NetherNet（ArkMirage，实验）",
        "RakNet（诊断）",
    };

    public IReadOnlyList<string> NetherNetListenModes { get; } = new[]
    {
        "局域网发现（UDP 7551）",
        "HTTP 信令（自测）",
    };

    public IReadOnlyList<string> NetherNetUpstreamModes { get; } = new[]
    {
        "局域网发现（真实 BDS）",
        "HTTP 信令",
    };

    public bool IsNetherNetMode => SelectedTransportIndex == 0;

    public bool CanStart => !IsRunning;

    public bool CanStop => IsRunning;

    public bool CanExportDiagnostics => _proxy is not null && DiagnosticCount > 0;

    public string CapabilityBanner =>
        "部分翻译：Login/StartGame/NetworkSettings 已双向改写；LevelChunk 信封与 SubChunk 调色板（2225→2193 降级）已实现；"
        + "其余包透传。NetherNet 已连通真实 BDS 1.26.51 并验证 NetworkSettings；"
        + "加密握手密码学层已通过测试但未接线；登录仍需有效 Xbox 身份；物品/实体元数据未完成。";

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    partial void OnSelectedClientVersionChanged(VersionOption? value) => UpdateRouteSummary();

    partial void OnSelectedServerVersionChanged(VersionOption? value) => UpdateRouteSummary();

    partial void OnPassthroughModeChanged(bool value) => UpdateRouteSummary();

    partial void OnSelectedTransportIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsNetherNetMode));

        if (value == 0)
        {
            ListenEndPoint = SelectedListenModeIndex == 0 ? "0.0.0.0:7551" : "0.0.0.0:19132";
            if (!UpstreamEndPoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                UpstreamEndPoint = "http://" + UpstreamEndPoint;
            }
        }
        else
        {
            ListenEndPoint = "0.0.0.0:19132";
        }

        UpdateRouteSummary();
    }

    partial void OnSelectedListenModeIndexChanged(int value)
    {
        if (SelectedTransportIndex == 0)
        {
            ListenEndPoint = value == 0 ? "0.0.0.0:7551" : "0.0.0.0:19132";
        }
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsRunning || _proxy is not null)
        {
            return;
        }

        var client = SelectedClientVersion?.Version
            ?? throw new InvalidOperationException("请选择客户端版本");
        var server = SelectedServerVersion?.Version
            ?? throw new InvalidOperationException("请选择服务端版本");

        if (!int.TryParse(MaxPlayersText, out var maxPlayers) || maxPlayers <= 0)
        {
            maxPlayers = 100;
        }

        try
        {
            var registry = BuildRegistry(client, server);
            var router = new VersionMapRouter(registry);

            IClientTransportServer listener;
            IUpstreamTransport upstream;

            if (IsNetherNetMode)
            {
                var netherNetOptions = new NetherNetOptions
                {
                    Backend = NetherNetBackend.ArkMirage,
                    ListenEndPoint = ListenEndPoint,
                    ListenMode = SelectedListenModeIndex == 0
                        ? NetherNetListenMode.LanDiscovery
                        : NetherNetListenMode.HttpSignaling,
                    ProtocolVersion = client.Number,
                    MinecraftVersion = client.MinecraftVersion,
                    ServerName = Motd,
                    MaxPlayers = maxPlayers,
                    ConnectEndPoint = UpstreamEndPoint,
                    ConnectMode = SelectedUpstreamModeIndex == 0
                        ? NetherNetConnectMode.LanDiscovery
                        : NetherNetConnectMode.HttpSignaling,
                };

                listener = NetherNetTransportFactory.CreateListener(netherNetOptions, _log);
                upstream = NetherNetTransportFactory.Create(netherNetOptions, _log);
            }
            else
            {
                listener = new RakNetServer(
                    new RakNetServerOptions
                    {
                        ListenEndPoint = ListenEndPoint,
                        ProtocolVersion = client.Number,
                        MinecraftVersion = client.MinecraftVersion,
                        Motd = Motd,
                        MaxPlayers = maxPlayers,
                    },
                    _log);

                upstream = new PlaceholderNetherNetTransport(
                    new NetherNetOptions { ConnectEndPoint = UpstreamEndPoint },
                    _log);
            }

            _proxy = new TranslationProxy(
                listener,
                upstream,
                router,
                new TranslationProxyOptions
                {
                    ClientVersion = client,
                    ServerVersion = server,
                    PassUnknownPackets = true,
                    EnableDiagnostics = DiagnosticsEnabled,
                },
                _log);

            await _proxy.StartAsync().ConfigureAwait(true);

            IsRunning = true;
            StatusText = $"监听中 · {ListenEndPoint} · 客户端 {client.MinecraftVersion} → 服务端 {server.MinecraftVersion}";
            _log.Info($"代理已启动：{client} -> {server}（直通模式，未做真实翻译）");
        }
        catch (Exception exception)
        {
            _log.Error("启动失败", exception);
            StatusText = $"启动失败：{exception.Message}";
            await StopCoreAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task Stop()
    {
        ExportDiagnosticsInternal();
        await StopCoreAsync().ConfigureAwait(true);
        StatusText = "已停止";
        _log.Info("代理已停止");
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var path = ExportDiagnosticsInternal();
        if (path is not null)
        {
            _log.Info($"诊断已导出: {path}");
        }
    }

    private string? ExportDiagnosticsInternal()
    {
        var proxy = _proxy;
        if (proxy is null || proxy.Diagnostics.Count == 0)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            var path = Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            proxy.Diagnostics.Export(path);
            LastDiagnosticsPath = path;
            return path;
        }
        catch (Exception exception)
        {
            _log.Warn($"导出诊断失败: {exception.Message}");
            return null;
        }
    }

    public async Task ShutdownAsync()
    {
        _statisticsTimer.Stop();
        await StopCoreAsync().ConfigureAwait(false);
    }

    private async Task StopCoreAsync()
    {
        if (_proxy is null)
        {
            IsRunning = false;
            return;
        }

        var proxy = _proxy;
        _proxy = null;
        try
        {
            await proxy.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _log.Warn($"停止时出现异常: {exception.Message}");
        }

        IsRunning = false;
        ActiveSessions = 0;
    }

    private VersionMapRegistry BuildRegistry(ProtocolVersion client, ProtocolVersion server)
    {
        var registry = new VersionMapRegistry();

        if (!PassthroughMode)
        {
            const int v2193 = 2193;
            const int v2225 = 2225;
            var supportedPair = (client.Number == v2193 && server.Number == v2225)
                                || (client.Number == v2225 && server.Number == v2193);

            registry.Register(new LoginStartGameAdapter(
                client,
                ContentFallbackMap.V2225ToV2193,
                MetadataSlotMap.V2225ToV2193,
                MetadataSlotMap.V2193ToV2225));
            if (server.Number != client.Number)
            {
                registry.Register(new LoginStartGameAdapter(
                    server,
                    ContentFallbackMap.V2225ToV2193,
                    MetadataSlotMap.V2225ToV2193,
                    MetadataSlotMap.V2193ToV2225));
            }

            if (supportedPair)
            {
                _log.Info("已启用 2193↔2225 部分翻译：Login / StartGame / NetworkSettings / LevelChunk / SubChunk / SetActorData；其余包透传。");
            }
            else
            {
                _log.Warn(
                    $"当前真实翻译只覆盖 2193↔2225；所选 {client.Number}↔{server.Number} 仅会改写 Login 协议号。");
            }

            return registry;
        }

        // 直通适配器只用于验证管线，不解析任何字段，也不做版本字段迁移。
        registry.Register(new PassthroughVersionAdapter(client));
        if (server.Number != client.Number)
        {
            registry.Register(new PassthroughVersionAdapter(server));
        }

        _log.Warn("已启用直通适配器：仅验证管线连通性，不代表该版本对已完成翻译。");
        return registry;
    }

    private void UpdateRouteSummary()
    {
        var client = SelectedClientVersion?.Version;
        var server = SelectedServerVersion?.Version;
        if (client is null || server is null)
        {
            RouteSummary = string.Empty;
            return;
        }

        if (client.Value.Number == server.Value.Number)
        {
            RouteSummary =
                $"{client.Value.MinecraftVersion} ({client.Value.Number}) → {server.Value.MinecraftVersion} ({server.Value.Number})"
                + " · 同字节等价版本，翻译应为恒等映射（当前仍走直通）";
            return;
        }

        var mode = PassthroughMode
            ? "直通适配器（无真实翻译）"
            : "Login/StartGame 翻译，其余透传";
        RouteSummary =
            $"{client.Value.MinecraftVersion} ({client.Value.Number}) → {server.Value.MinecraftVersion} ({server.Value.Number})"
            + $" · {mode}";
    }

    private void RefreshStatistics()
    {
        var proxy = _proxy;
        if (proxy is null)
        {
            ActiveSessions = 0;
            return;
        }

        ActiveSessions = proxy.ActiveSessions;
        DiagnosticCount = proxy.Diagnostics.Count;
        OnPropertyChanged(nameof(CanExportDiagnostics));
        PacketsTotal = proxy.Statistics.PacketsTotal;
        TranslatedCount = proxy.Statistics.Translated;
        PassedThroughCount = proxy.Statistics.PassedThrough;
        DroppedCount = proxy.Statistics.Dropped;
        DecodeErrors = proxy.Statistics.DecodeErrors;
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        Logs.Add(entry);
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(0);
        }
    }
}
