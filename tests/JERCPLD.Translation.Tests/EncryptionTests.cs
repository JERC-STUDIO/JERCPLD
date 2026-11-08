using System.Text;
using System.Text.Json;
using JERCPLD.Codec.Bedrock.Encryption;
using JERCPLD.Core;
using JERCPLD.Core.Logging;
using JERCPLD.Protocol.Bedrock;

internal static class EncryptionTests
{
    public static void Run()
    {
        TestEcdh();
        TestAesCfb8();
        TestJwtAndHandshakeInfo();
        TestLoginPublicKey();
        TestHandshakeSimulation();
        TestPacketCodec();
        Console.WriteLine("PASS 加密握手基础（ECDH / AES-CFB8 / JWT / 包编解码）");
    }

    private static void TestEcdh()
    {
        using var a = BedrockEncryptionKeys.CreateKeyPair();
        using var b = BedrockEncryptionKeys.CreateKeyPair();
        var aPub = BedrockEncryptionKeys.ExportPublicKey(a);
        var bPub = BedrockEncryptionKeys.ExportPublicKey(b);
        var ab = BedrockEncryptionKeys.DeriveSharedSecret(a, bPub);
        var ba = BedrockEncryptionKeys.DeriveSharedSecret(b, aPub);
        Assert(ab.SequenceEqual(ba), "ECDH 双方共享密钥不一致");
        Assert(ab.Length > 0, "ECDH 共享密钥为空");
    }

    private static void TestAesCfb8()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var iv = key.AsSpan(0, 16).ToArray();
        var plaintext = Encoding.UTF8.GetBytes("JERCPLD bedrock encryption test 0123456789");

        var encrypted = new byte[plaintext.Length];
        using (var encryptor = new AesCfb8(key, iv))
        {
            encryptor.Encrypt(plaintext, encrypted);
        }

        Assert(!encrypted.SequenceEqual(plaintext), "AES-CFB8 未加密");

        var decrypted = new byte[encrypted.Length];
        using (var decryptor = new AesCfb8(key, iv))
        {
            decryptor.Decrypt(encrypted, decrypted);
        }

        Assert(decrypted.SequenceEqual(plaintext), "AES-CFB8 往返不一致");

        // 流式分段必须与一次性加密结果一致（cipher 状态连续）。
        var chunked = new byte[plaintext.Length];
        using (var encryptor = new AesCfb8(key, iv))
        {
            encryptor.Encrypt(plaintext.AsSpan(0, 10), chunked.AsSpan(0, 10));
            encryptor.Encrypt(plaintext.AsSpan(10), chunked.AsSpan(10));
        }

        Assert(chunked.SequenceEqual(encrypted), "AES-CFB8 分段状态不一致");
    }

    private static void TestJwtAndHandshakeInfo()
    {
        using var serverKey = BedrockEncryptionKeys.CreateKeyPair();
        var publicKey = BedrockEncryptionKeys.ExportPublicKey(serverKey);
        var salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        var header = JsonSerializer.Serialize(new { alg = "ES384", x5u = publicKey });
        var payload = JsonSerializer.Serialize(new
        {
            salt = Convert.ToBase64String(salt),
            exp = 4102444800,
        });
        var jwt = BedrockJwt.ComposeForTesting(header, payload);

        Assert(BedrockHandshakeCodec.TryGetServerHandshakeInfo(jwt, out var parsedKey, out var parsedSalt),
            "服务端握手 JWT 解析失败");
        Assert(parsedKey == publicKey, "服务端握手公钥不一致");
        Assert(parsedSalt.SequenceEqual(salt), "服务端握手 salt 不一致");
    }

    private static void TestLoginPublicKey()
    {
        using var clientKey = BedrockEncryptionKeys.CreateKeyPair();
        var clientPublicKey = BedrockEncryptionKeys.ExportPublicKey(clientKey);

        var header = JsonSerializer.Serialize(new { alg = "ES384", x5u = clientPublicKey });
        var payload = JsonSerializer.Serialize(new
        {
            identityPublicKey = clientPublicKey,
            extraData = new { displayName = "TestPlayer", identity = Guid.NewGuid().ToString() },
        });
        var clientJwt = BedrockJwt.ComposeForTesting(header, payload);
        var connectionRequest = JsonSerializer.Serialize(new { chain = Array.Empty<string>(), client = clientJwt });

        var body = new JERCPLD.Codec.Binary.BinaryWriterEx();
        body.WriteUInt32BE(2225);
        body.WriteString(connectionRequest);
        var frame = new BedrockPacketFrame(BedrockPacketIds.Login, body.ToArray());

        Assert(BedrockHandshakeCodec.TryReadLogin(frame, out var protocol, out var json), "Login 包解析失败");
        Assert(protocol == 2225, $"Login 协议号错误: {protocol}");
        var extracted = BedrockHandshakeCodec.GetClientPublicKey(json);
        Assert(extracted == clientPublicKey, "客户端公钥提取失败");
    }

    private static void TestHandshakeSimulation()
    {
        using var serverKey = BedrockEncryptionKeys.CreateKeyPair();
        using var clientKey = BedrockEncryptionKeys.CreateKeyPair();

        var serverPublicKey = BedrockEncryptionKeys.ExportPublicKey(serverKey);
        var clientPublicKey = BedrockEncryptionKeys.ExportPublicKey(clientKey);
        var salt = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();

        var serverSecret = BedrockEncryptionKeys.DeriveSharedSecret(serverKey, clientPublicKey);
        var clientSecret = BedrockEncryptionKeys.DeriveSharedSecret(clientKey, serverPublicKey);
        var serverKeyBytes = BedrockEncryptionKeys.DeriveKey(salt, serverSecret);
        var clientKeyBytes = BedrockEncryptionKeys.DeriveKey(salt, clientSecret);
        Assert(serverKeyBytes.SequenceEqual(clientKeyBytes), "握手后双方对称密钥不一致");

        var (clientEncryptor, clientDecryptor) = BedrockEncryptionKeys.CreateCiphers(clientKeyBytes);
        var (serverEncryptor, serverDecryptor) = BedrockEncryptionKeys.CreateCiphers(serverKeyBytes);
        using var _ = clientEncryptor;
        using var __ = clientDecryptor;
        using var ___ = serverEncryptor;
        using var ____ = serverDecryptor;

        var clientMessage = Encoding.UTF8.GetBytes("client -> server");
        var encryptedUp = new byte[clientMessage.Length];
        clientEncryptor.Encrypt(clientMessage, encryptedUp);
        var decryptedUp = new byte[encryptedUp.Length];
        serverDecryptor.Decrypt(encryptedUp, decryptedUp);
        Assert(decryptedUp.SequenceEqual(clientMessage), "客户端到服务端加密往返失败");

        var serverMessage = Encoding.UTF8.GetBytes("server -> client");
        var encryptedDown = new byte[serverMessage.Length];
        serverEncryptor.Encrypt(serverMessage, encryptedDown);
        var decryptedDown = new byte[encryptedDown.Length];
        clientDecryptor.Decrypt(encryptedDown, decryptedDown);
        Assert(decryptedDown.SequenceEqual(serverMessage), "服务端到客户端加密往返失败");
    }

    private static void TestPacketCodec()
    {
        var jwt = "header.payload.signature";
        var frame = BedrockHandshakeCodec.WriteServerToClientHandshake(jwt);
        Assert(frame.PacketId == BedrockPacketIds.ServerToClientHandshake, "握手包 ID 错误");
        Assert(BedrockHandshakeCodec.ReadServerToClientHandshake(frame) == jwt, "握手包字符串往返失败");

        var clientFrame = BedrockHandshakeCodec.WriteClientToServerHandshake();
        Assert(clientFrame.PacketId == BedrockPacketIds.ClientToServerHandshake, "客户端握手包 ID 错误");
        Assert(clientFrame.Body.Length == 0, "客户端握手包应为空");
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
