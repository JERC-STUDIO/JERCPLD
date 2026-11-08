using System.Net;

namespace JERCPLD.Core.Networking;

public static class EndpointParser
{
    /// <summary>
    /// 解析 "host:port" / "[ipv6]:port" / "host"。只支持字面 IP；
    /// TODO(network): 主机名解析（DNS）尚未实现，避免引入隐式阻塞调用。
    /// </summary>
    public static IPEndPoint Parse(string text, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("地址不能为空", nameof(text));
        }

        var value = text.Trim();
        string host;
        var port = defaultPort;

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = value.IndexOf(']');
            if (closing < 0)
            {
                throw new FormatException($"IPv6 地址缺少 ']': {text}");
            }

            host = value[1..closing];
            var rest = value[(closing + 1)..];
            if (rest.StartsWith(':'))
            {
                port = ParsePort(rest[1..], text);
            }
        }
        else
        {
            var lastColon = value.LastIndexOf(':');
            var firstColon = value.IndexOf(':');
            if (lastColon > 0 && firstColon == lastColon)
            {
                host = value[..lastColon];
                port = ParsePort(value[(lastColon + 1)..], text);
            }
            else
            {
                host = value;
            }
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            throw new NotSupportedException($"TODO(network): 暂不支持解析主机名 '{host}'，请使用 IP 字面量。");
        }

        return new IPEndPoint(address, port);
    }

    private static int ParsePort(string text, string original)
    {
        if (!int.TryParse(text, out var port) || port is < 1 or > 65535)
        {
            throw new FormatException($"端口无效: {original}");
        }

        return port;
    }
}
