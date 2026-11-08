using System.Net;
using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.Codec.RakNet;

/// <summary>
/// RakNet 地址编解码。
/// TODO(raknet): IPv6（版本号 6）尚未实现，当前仅支持 IPv4。
/// </summary>
public static class RakNetAddressCodec
{
    public const byte IPv4 = 4;

    public static RakNetAddress Read(BinaryReaderEx reader)
    {
        var version = reader.ReadByte();
        if (version == IPv4)
        {
            var raw = reader.ReadBytes(4).Span;
            Span<byte> bytes = stackalloc byte[4];
            for (var i = 0; i < 4; i++)
            {
                bytes[i] = (byte)~raw[i];
            }

            var port = reader.ReadUInt16BE();
            return new RakNetAddress(new IPAddress(bytes), port);
        }

        throw new NotSupportedException($"TODO(raknet): RakNet 地址版本 {version} 尚未实现（当前仅支持 IPv4）");
    }

    public static void Write(BinaryWriterEx writer, RakNetAddress address)
    {
        if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("TODO(raknet): 仅支持写入 IPv4 RakNet 地址");
        }

        writer.WriteByte(IPv4);
        var bytes = address.Address.GetAddressBytes();
        foreach (var value in bytes)
        {
            writer.WriteByte((byte)~value);
        }

        writer.WriteUInt16BE(address.Port);
    }
}
