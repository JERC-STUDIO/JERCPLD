using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.Codec.RakNet;

/// <summary>
/// RakNet 在线握手消息编解码（位于可靠有序帧内部）。
/// </summary>
public static class HandshakeCodec
{
    public const int SystemAddressCount = 10;

    public static ConnectionRequest DecodeConnectionRequest(BinaryReaderEx reader)
    {
        var id = reader.ReadByte();
        if (id != RakNetMessageId.ConnectionRequest)
        {
            throw new InvalidDataException($"不是 ConnectionRequest: 0x{id:X2}");
        }

        var clientGuid = reader.ReadUInt64BE();
        var time = reader.ReadUInt64BE();
        var useSecurity = reader.ReadByte() != 0;
        return new ConnectionRequest(clientGuid, time, useSecurity);
    }

    public static byte[] EncodeConnectionRequestAccepted(ConnectionRequestAccepted message)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetMessageId.ConnectionRequestAccepted);
        RakNetAddressCodec.Write(writer, message.ClientAddress);
        writer.WriteUInt16BE(message.SystemIndex);

        for (var i = 0; i < SystemAddressCount; i++)
        {
            var address = i < message.SystemAddresses.Count
                ? message.SystemAddresses[i]
                : new RakNetAddress(System.Net.IPAddress.Any, 0);
            RakNetAddressCodec.Write(writer, address);
        }

        writer.WriteUInt64BE(message.RequestTime);
        writer.WriteUInt64BE(message.Time);
        return writer.ToArray();
    }

    public static ConnectionRequestAccepted DecodeConnectionRequestAccepted(BinaryReaderEx reader)
    {
        var id = reader.ReadByte();
        if (id != RakNetMessageId.ConnectionRequestAccepted)
        {
            throw new InvalidDataException($"不是 ConnectionRequestAccepted: 0x{id:X2}");
        }

        var clientAddress = RakNetAddressCodec.Read(reader);
        var systemIndex = reader.ReadUInt16BE();
        var addresses = new List<RakNetAddress>(SystemAddressCount);
        for (var i = 0; i < SystemAddressCount; i++)
        {
            addresses.Add(RakNetAddressCodec.Read(reader));
        }

        var requestTime = reader.ReadUInt64BE();
        var time = reader.ReadUInt64BE();
        return new ConnectionRequestAccepted(clientAddress, systemIndex, addresses, requestTime, time);
    }

    public static NewIncomingConnection DecodeNewIncomingConnection(BinaryReaderEx reader)
    {
        var id = reader.ReadByte();
        if (id != RakNetMessageId.NewIncomingConnection)
        {
            throw new InvalidDataException($"不是 NewIncomingConnection: 0x{id:X2}");
        }

        var serverAddress = RakNetAddressCodec.Read(reader);
        var addresses = new List<RakNetAddress>(SystemAddressCount);
        for (var i = 0; i < SystemAddressCount; i++)
        {
            addresses.Add(RakNetAddressCodec.Read(reader));
        }

        var requestTime = reader.ReadUInt64BE();
        var time = reader.ReadUInt64BE();
        return new NewIncomingConnection(serverAddress, addresses, requestTime, time);
    }

    public static ConnectedPing DecodeConnectedPing(BinaryReaderEx reader)
    {
        var id = reader.ReadByte();
        if (id != RakNetMessageId.ConnectedPing)
        {
            throw new InvalidDataException($"不是 ConnectedPing: 0x{id:X2}");
        }

        return new ConnectedPing(reader.ReadUInt64BE());
    }

    public static byte[] EncodeConnectedPong(ConnectedPong pong)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetMessageId.ConnectedPong);
        writer.WriteUInt64BE(pong.PingTime);
        writer.WriteUInt64BE(pong.PongTime);
        return writer.ToArray();
    }

    public static byte[] EncodeDisconnectNotification()
        => new[] { RakNetMessageId.DisconnectNotification };

    public static byte[] EncodeNewIncomingConnection(NewIncomingConnection message)
    {
        var writer = new BinaryWriterEx();
        writer.WriteByte(RakNetMessageId.NewIncomingConnection);
        RakNetAddressCodec.Write(writer, message.ServerAddress);
        for (var i = 0; i < SystemAddressCount; i++)
        {
            var address = i < message.InternalAddresses.Count
                ? message.InternalAddresses[i]
                : new RakNetAddress(System.Net.IPAddress.Any, 0);
            RakNetAddressCodec.Write(writer, address);
        }

        writer.WriteUInt64BE(message.RequestTime);
        writer.WriteUInt64BE(message.Time);
        return writer.ToArray();
    }
}
