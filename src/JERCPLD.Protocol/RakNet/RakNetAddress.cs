using System.Net;
using System.Net.Sockets;

namespace JERCPLD.Protocol.RakNet;

/// <summary>
/// RakNet 地址模型。IPv4 序列化时 IP 字节需要按位取反，端口为大端。
/// TODO(raknet): IPv6 地址（版本号 6）的序列化/反序列化尚未实现。
/// </summary>
public sealed record RakNetAddress(IPAddress Address, ushort Port)
{
    public static RakNetAddress FromEndPoint(IPEndPoint endPoint)
        => new(endPoint.Address, checked((ushort)endPoint.Port));

    public IPEndPoint ToEndPoint() => new(Address, Port);

    public override string ToString()
        => Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{Address}]:{Port}"
            : $"{Address}:{Port}";
}
