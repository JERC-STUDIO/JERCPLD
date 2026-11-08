using System.Text;
using JERCPLD.Core;
using JERCPLD.Core.Logging;
using JERCPLD.NetherNet;
using JERCPLD.Proxy;
using JERCPLD.RakNet;
using JERCPLD.VersionMap;
using JERCPLD.VersionMap.Adapters;

namespace JERCPLD.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向时可能无法设置编码，忽略即可。
        }

        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "serve" => await ServeAsync(args[1..]).ConfigureAwait(false),
                "versions" => PrintVersions(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误: {exception.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string value)
        => value is "-h" or "--help" or "help" or "/?";

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令: {command}");
        PrintHelp();
        return 2;
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        var options = ParseOptions(args, out _);
        var log = new ConsoleLog(ParseLogLevel(Get(options, "log-level")));

        var listen = Get(options, "listen") ?? "0.0.0.0:19132";
        var clientVersion = ResolveVersion(Get(options, "client-version") ?? "1.20.80");
        var serverVersion = ResolveVersion(Get(options, "server-version") ?? "1.21.2");

        if (clientVersion.Number == serverVersion.Number)
        {
            log.Warn("客户端版本与 BDS 版本相同，版本翻译层不会工作，仅转发原始流量。");
        }

        var registry = new VersionMapRegistry();
        if (options.ContainsKey("passthrough"))
        {
            registry.Register(new PassthroughVersionAdapter(clientVersion));
            if (serverVersion.Number != clientVersion.Number)
            {
                registry.Register(new PassthroughVersionAdapter(serverVersion));
            }

            log.Warn("已启用 passthrough 适配器：仅用于验证管线连通性，不做任何真实协议翻译。");
        }

        var router = new VersionMapRouter(registry);
        var listener = new RakNetServer(
            new RakNetServerOptions
            {
                ListenEndPoint = listen,
                ProtocolVersion = clientVersion.Number,
                MinecraftVersion = clientVersion.MinecraftVersion,
                Motd = Get(options, "motd") ?? "JERCPLD 跨版本代理",
                MaxPlayers = ParseInt(Get(options, "max-players"), 100),
            },
            log);

        var upstream = NetherNetTransportFactory.Create(
            new NetherNetOptions
            {
                ConnectEndPoint = Get(options, "upstream") ?? "127.0.0.1:19132",
            },
            log);

        var proxy = new TranslationProxy(
            listener,
            upstream,
            router,
            new TranslationProxyOptions
            {
                ClientVersion = clientVersion,
                ServerVersion = serverVersion,
                PassUnknownPackets = options.ContainsKey("pass-unknown"),
            },
            log);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            await proxy.StartAsync(cancellation.Token).ConfigureAwait(false);
            log.Info("代理已启动，按 Ctrl+C 停止。");
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            await proxy.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static int PrintVersions()
    {
        Console.WriteLine("已登记的协议版本（来源: [官方] mojang.github.io/bedrock-protocol-docs, [社区] PrismarineJS）:");
        Console.WriteLine("  MC 版本      协议号  来源  说明");
        foreach (var version in KnownProtocolVersions.All)
        {
            var source = version.Source switch
            {
                ProtocolVersionSource.MojangOfficial => "官方",
                ProtocolVersionSource.Community => "社区",
                _ => "未核实",
            };
            var equivalents = KnownProtocolVersions.FindWireEquivalent(version);
            var note = equivalents.Count > 1
                ? $"同字节等价版本: {string.Join(" / ", equivalents.Select(item => item.MinecraftVersion))}"
                : version.SourceNote ?? string.Empty;
            Console.WriteLine($"  {version.MinecraftVersion,-12} {version.Number,-7} {source}  {note}");
        }

        Console.WriteLine();
        Console.WriteLine("提示: 版本翻译需要 VersionMapRegistry 中注册星型适配器；骨架默认一个都没有。");
        Console.WriteLine("      `serve --passthrough` 只注册直通适配器，用于验证管线连通性。");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("JERCPLD - Minecraft 基岩版跨版本翻译代理（骨架）");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  jercpld serve [options]     启动 RakNet 监听并等待旧版本客户端");
        Console.WriteLine("  jercpld versions            列出已登记的协议版本");
        Console.WriteLine("  jercpld help                显示帮助");
        Console.WriteLine();
        Console.WriteLine("serve 参数:");
        Console.WriteLine("  --listen <ip:port>        RakNet 监听地址，默认 0.0.0.0:19132");
        Console.WriteLine("  --upstream <ip:port>      新版 BDS 的 NetherNet 地址，默认 127.0.0.1:19132");
        Console.WriteLine("  --client-version <v>      旧客户端版本，可写协议号或 MC 版本，默认 1.20.80");
        Console.WriteLine("  --server-version <v>      BDS 版本，默认 1.21.2");
        Console.WriteLine("  --motd <text>             服务器列表 MOTD");
        Console.WriteLine("  --max-players <n>         服务器列表显示的最大人数，默认 100");
        Console.WriteLine("  --passthrough             注册直通适配器（仅验证管线，不做真实翻译）");
        Console.WriteLine("  --pass-unknown            适配器不支持的包原样透传（默认丢弃）");
        Console.WriteLine("  --log-level <level>       debug/info/warn/error，默认 info");
        Console.WriteLine();
        Console.WriteLine("说明: NetherNet 上游目前是占位实现，会明确报告未实现；RakNet 监听与握手可用。");
    }

    private static Dictionary<string, string?> ParseOptions(string[] args, out List<string> positionals)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            var key = argument[2..];
            if (key.Length == 0)
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = null;
            }
        }

        return options;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) ? value : null;

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, out var value) ? value : fallback;

    private static LogLevel ParseLogLevel(string? text)
    {
        return text?.ToLowerInvariant() switch
        {
            "debug" => LogLevel.Debug,
            "warn" => LogLevel.Warn,
            "error" => LogLevel.Error,
            _ => LogLevel.Info,
        };
    }

    private static ProtocolVersion ResolveVersion(string text)
    {
        if (int.TryParse(text, out var number))
        {
            return KnownProtocolVersions.FindByNumber(number) ?? new ProtocolVersion(number, $"unknown-{number}");
        }

        var known = KnownProtocolVersions.FindByMinecraftVersion(text);
        if (known is not null)
        {
            return known.Value;
        }

        throw new ArgumentException($"未知版本 '{text}'。用 `jercpld versions` 查看已登记版本，或直接传协议号。");
    }
}
