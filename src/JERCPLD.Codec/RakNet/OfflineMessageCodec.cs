using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.Codec.RakNet;

/// <summary>
/// RakNet 离线消息（未进入会话前的明文包）编解码。
/// </summary>
public static class OfflineMessageCodec
{
    public static UnconnectedPing DecodeUnconnectedPing(ReadOnlyMemory<byte> datagram)
    {
        var reader = new BinaryReaderEx(datagram);
        var id = reader.ReadByte();
        if (id is not (RakNetMessageId.UnconnectedPing or RakNetMessageId.UnconnectedPingOpenConnections))
        {
            throw new InvalidDataException($"不是 UnconnectedPing: 0x{id:X2}");
        }

        var time = reader.ReadUInt64BE();
        RakNetMagic.ReadAndValidate(reader);
        var clientGuid = reader.Remaining >= 8 ? reader.ReadUInt64BE() : 0UL;
        return new UnconnectedPing(time, clientGuid, id == RakNetMessageId.UnconnectedPingOpenConnections);
    }

    public static byte[] EncodeUnconnectedPong(UnconnectedPong pong)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetMessageId.UnconnectedPong);
        writer.WriteUInt64BE(pong.Time);
        writer.WriteUInt64BE(pong.ServerGuid);
        RakNetMagic.Write(writer);

        var motd = System.Text.Encoding.UTF8.GetBytes(pong.Motd);
        if (motd.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("MOTD 过长");
        }

        writer.WriteUInt16BE((ushort)motd.Length);
        writer.WriteBytes(motd);
        return writer.ToArray();
    }

    public static UnconnectedPong DecodeUnconnectedPong(ReadOnlyMemory<byte> datagram)
    {
        var reader = new BinaryReaderEx(datagram);
        var id = reader.ReadByte();
        if (id != RakNetMessageId.UnconnectedPong)
        {
            throw new InvalidDataException($"不是 UnconnectedPong: 0x{id:X2}");
        }

        var time = reader.ReadUInt64BE();
        var serverGuid = reader.ReadUInt64BE();
        RakNetMagic.ReadAndValidate(reader);
        var length = reader.ReadUInt16BE();
        var motd = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(length).Span);
        return new UnconnectedPong(time, serverGuid, motd);
    }

    public static OpenConnectionRequest1 DecodeOpenConnectionRequest1(ReadOnlyMemory<byte> datagram)
    {
        var reader = new BinaryReaderEx(datagram);
        var id = reader.ReadByte();
        if (id != RakNetMessageId.OpenConnectionRequest1)
        {
            throw new InvalidDataException($"不是 OpenConnectionRequest1: 0x{id:X2}");
        }

        RakNetMagic.ReadAndValidate(reader);
        var protocol = reader.ReadByte();
        // 剩余部分为 MTU 填充，客户端用它把数据报撑到期望 MTU。
        return new OpenConnectionRequest1(protocol, datagram.Length);
    }

    public static byte[] EncodeOpenConnectionReply1(OpenConnectionReply1 reply)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetMessageId.OpenConnectionReply1);
        RakNetMagic.Write(writer);
        writer.WriteUInt64BE(reply.ServerGuid);
        writer.WriteByte(0); // 不启用加密
        writer.WriteUInt16BE((ushort)reply.Mtu);
        return writer.ToArray();
    }

    public static OpenConnectionRequest2 DecodeOpenConnectionRequest2(ReadOnlyMemory<byte> datagram)
    {
        var reader = new BinaryReaderEx(datagram);
        var id = reader.ReadByte();
        if (id != RakNetMessageId.OpenConnectionRequest2)
        {
            throw new InvalidDataException($"不是 OpenConnectionRequest2: 0x{id:X2}");
        }

        RakNetMagic.ReadAndValidate(reader);
        var serverAddress = RakNetAddressCodec.Read(reader);
        var mtu = reader.ReadUInt16BE();
        var clientGuid = reader.ReadUInt64BE();
        return new OpenConnectionRequest2(serverAddress, mtu, clientGuid);
    }

    public static byte[] EncodeOpenConnectionReply2(OpenConnectionReply2 reply)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetMessageId.OpenConnectionReply2);
        writer.WriteUInt64BE(reply.ServerGuid);
        RakNetAddressCodec.Write(writer, reply.ClientAddress);
        writer.WriteUInt16BE((ushort)reply.Mtu);
        writer.WriteByte(reply.EncryptionEnabled ? (byte)1 : (byte)0);
        return writer.ToArray();
    }
}
