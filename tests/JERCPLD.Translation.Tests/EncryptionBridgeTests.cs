using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JERCPLD.Codec.Bedrock;
using JERCPLD.Codec.Bedrock.Encryption;
using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.Bedrock;
using JERCPLD.Proxy.Encryption;

internal static class EncryptionBridgeTests
{
    public static void Run()
    {
        using var upstreamIdentity = BedrockIdentityKey.Create();
        using var bridge = new BedrockEncryptionBridge(upstreamIdentity);
        using var clientKey = BedrockEncryptionKeys.CreateKeyPair();
        var clientPublicKey = BedrockEncryptionKeys.ExportPublicKey(clientKey);

        // 构造带客户端公钥的 Login。
        var header = JsonSerializer.Serialize(new { alg = "ES384", x5u = clientPublicKey });
        var payload = JsonSerializer.Serialize(new
        {
            identityPublicKey = clientPublicKey,
            extraData = new { displayName = "BridgeTest" },
        });
        var clientJwt = BedrockJwt.ComposeForTesting(header, payload);
        var requestJson = JsonSerializer.Serialize(new { chain = Array.Empty<string>(), client = clientJwt });
        var body = new BinaryWriterEx();
        body.WriteUInt32BE(2225);
        body.WriteString(requestJson);
        var loginFrame = new BedrockPacketFrame(BedrockPacketIds.Login, body.ToArray());

        Assert(bridge.TryCaptureClientPublicKey(loginFrame), "桥接未能提取客户端公钥");

        // 客户端侧握手。
        var (jwt, bridgeKey) = bridge.BuildClientHandshake();
        Assert(BedrockHandshakeCodec.TryGetServerHandshakeInfo(jwt, out var proxyPublicKey, out var salt),
            "客户端侧握手 JWT 无法解析");
        var clientSecret = BedrockEncryptionKeys.DeriveSharedSecret(clientKey, proxyPublicKey);
        var clientKeyBytes = BedrockEncryptionKeys.DeriveKey(salt, clientSecret);
        Assert(bridgeKey.SequenceEqual(clientKeyBytes), "客户端侧会话密钥不一致");

        bridge.ActivateClientEncryption();
        var (clientSend, clientReceive) = BedrockEncryptionKeys.CreateCiphers(clientKeyBytes);
        using var _ = clientSend;
        using var __ = clientReceive;

        var toClient = Encoding.UTF8.GetBytes("server->client encrypted");
        var encryptedToClient = bridge.EncryptToClient(toClient);
        var decryptedAtClient = new byte[encryptedToClient.Length];
        clientReceive.Decrypt(encryptedToClient, decryptedAtClient);
        Assert(decryptedAtClient.SequenceEqual(toClient), "客户端方向加密失败");

        var toServer = Encoding.UTF8.GetBytes("client->server encrypted");
        var encryptedToServer = new byte[toServer.Length];
        clientSend.Encrypt(toServer, encryptedToServer);
        Assert(bridge.DecryptFromClient(encryptedToServer).SequenceEqual(toServer), "客户端方向解密失败");

        // 上游侧握手。
        using var serverIdentity = BedrockIdentityKey.Create();
        var serverSalt = RandomNumberGenerator.GetBytes(16);
        var serverHeader = JsonSerializer.Serialize(new { alg = "ES384", x5u = serverIdentity.PublicKeyBase64 });
        var serverPayload = JsonSerializer.Serialize(new { salt = Convert.ToBase64String(serverSalt) });
        var serverJwt = serverIdentity.SignJwt(serverHeader, serverPayload);
        var serverFrame = BedrockHandshakeCodec.WriteServerToClientHandshake(serverJwt);
        Assert(bridge.AcceptServerHandshake(serverFrame), "桥接未能接受 BDS 握手");

        var serverSecret = serverIdentity.DeriveSharedSecret(bridge.UpstreamPublicKey);
        var serverKeyBytes = BedrockEncryptionKeys.DeriveKey(serverSalt, serverSecret);
        var (serverSend, serverReceive) = BedrockEncryptionKeys.CreateCiphers(serverKeyBytes);
        using var ___ = serverSend;
        using var ____ = serverReceive;

        var fromServer = Encoding.UTF8.GetBytes("bds->proxy encrypted");
        var encryptedFromServer = new byte[fromServer.Length];
        serverSend.Encrypt(fromServer, encryptedFromServer);
        Assert(bridge.DecryptFromUpstream(encryptedFromServer).SequenceEqual(fromServer), "上游方向解密失败");

        var toServer2 = Encoding.UTF8.GetBytes("proxy->bds encrypted");
        var encryptedToServer2 = bridge.EncryptToUpstream(toServer2);
        var decryptedAtServer = new byte[encryptedToServer2.Length];
        serverReceive.Decrypt(encryptedToServer2, decryptedAtServer);
        Assert(decryptedAtServer.SequenceEqual(toServer2), "上游方向加密失败");

        Console.WriteLine("PASS 加密握手桥接（客户端侧 + 上游侧 + 双向 CFB8）");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            Console.Error.WriteLine("FAIL " + message);
            Environment.Exit(1);
        }
    }
}
