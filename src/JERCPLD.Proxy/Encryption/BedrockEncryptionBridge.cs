using System.Security.Cryptography;
using JERCPLD.Codec.Bedrock;
using JERCPLD.Codec.Bedrock.Encryption;
using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.Proxy.Encryption;

/// <summary>
/// 代理侧加密握手桥接：
///  - 客户端方向：代理扮演服务端，读取客户端 Login 中的公钥，下发自己的 ServerToClientHandshake；
///  - 上游方向：代理扮演客户端，接受 BDS 的 ServerToClientHandshake 并派生密钥；
///  - 两个方向各自维护独立的 AES-256-CFB8 状态。
///
/// 注意：上游方向要求代理拥有可被 BDS 接受的身份（离线模式的自签名链，或已认证的 Xbox 链）。
/// 在线模式没有有效身份时，本类可以完成密码学计算，但 BDS 不会接受 Login。
/// </summary>
public sealed class BedrockEncryptionBridge : IDisposable
{
    private readonly BedrockIdentityKey _clientFacingIdentity;
    private readonly BedrockIdentityKey _upstreamIdentity;

    private byte[]? _pendingClientKey;
    private AesCfb8? _clientEncryptor;
    private AesCfb8? _clientDecryptor;
    private AesCfb8? _upstreamEncryptor;
    private AesCfb8? _upstreamDecryptor;

    public BedrockEncryptionBridge(BedrockIdentityKey upstreamIdentity, BedrockIdentityKey? clientFacingIdentity = null)
    {
        _upstreamIdentity = upstreamIdentity;
        _clientFacingIdentity = clientFacingIdentity ?? BedrockIdentityKey.Create();
    }

    public string? ClientPublicKey { get; private set; }

    public bool ClientEncryptionActive => _clientEncryptor is not null;

    public bool UpstreamEncryptionActive => _upstreamEncryptor is not null;

    public string ClientFacingPublicKey => _clientFacingIdentity.PublicKeyBase64;

    public string UpstreamPublicKey => _upstreamIdentity.PublicKeyBase64;

    /// <summary>从客户端 Login 中提取公钥；成功后 <see cref="ClientPublicKey"/> 可用。</summary>
    public bool TryCaptureClientPublicKey(BedrockPacketFrame loginFrame)
    {
        if (!BedrockHandshakeCodec.TryReadLogin(loginFrame, out _, out var connectionRequest))
        {
            return false;
        }

        ClientPublicKey = BedrockHandshakeCodec.GetClientPublicKey(connectionRequest);
        return ClientPublicKey is not null;
    }

    /// <summary>
    /// 生成给客户端的 ServerToClientHandshake。返回 JWT 与会话密钥；
    /// 客户端回复 ClientToServerHandshake 后再调用 <see cref="ActivateClientEncryption"/>。
    /// </summary>
    public (string Jwt, byte[] SessionKey) BuildClientHandshake()
    {
        if (ClientPublicKey is null)
        {
            throw new InvalidOperationException("尚未从客户端 Login 中取得公钥");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var header = $"{{\"alg\":\"ES384\",\"x5u\":\"{_clientFacingIdentity.PublicKeyBase64}\"}}";
        var payload = $"{{\"salt\":\"{Convert.ToBase64String(salt)}\"}}";
        var jwt = _clientFacingIdentity.SignJwt(header, payload);

        var sharedSecret = _clientFacingIdentity.DeriveSharedSecret(ClientPublicKey);
        var sessionKey = BedrockEncryptionKeys.DeriveKey(salt, sharedSecret);
        _pendingClientKey = sessionKey;
        return (jwt, sessionKey);
    }

    public void ActivateClientEncryption()
    {
        if (_pendingClientKey is null)
        {
            throw new InvalidOperationException("没有待激活的客户端会话密钥");
        }

        (_clientEncryptor, _clientDecryptor) = BedrockEncryptionKeys.CreateCiphers(_pendingClientKey);
        CryptographicOperations.ZeroMemory(_pendingClientKey);
        _pendingClientKey = null;
    }

    /// <summary>处理 BDS 发来的 ServerToClientHandshake，派生并激活上行密钥。</summary>
    public bool AcceptServerHandshake(BedrockPacketFrame frame)
    {
        var jwt = BedrockHandshakeCodec.ReadServerToClientHandshake(frame);
        if (!BedrockHandshakeCodec.TryGetServerHandshakeInfo(jwt, out var serverPublicKey, out var salt))
        {
            return false;
        }

        var sharedSecret = _upstreamIdentity.DeriveSharedSecret(serverPublicKey);
        var sessionKey = BedrockEncryptionKeys.DeriveKey(salt, sharedSecret);
        (_upstreamEncryptor, _upstreamDecryptor) = BedrockEncryptionKeys.CreateCiphers(sessionKey);
        CryptographicOperations.ZeroMemory(sessionKey);
        return true;
    }

    public byte[] DecryptFromClient(ReadOnlySpan<byte> ciphertext)
        => Transform(_clientDecryptor, ciphertext, decrypt: true);

    public byte[] EncryptToClient(ReadOnlySpan<byte> plaintext)
        => Transform(_clientEncryptor, plaintext, decrypt: false);

    public byte[] DecryptFromUpstream(ReadOnlySpan<byte> ciphertext)
        => Transform(_upstreamDecryptor, ciphertext, decrypt: true);

    public byte[] EncryptToUpstream(ReadOnlySpan<byte> plaintext)
        => Transform(_upstreamEncryptor, plaintext, decrypt: false);

    public void Dispose()
    {
        _clientEncryptor?.Dispose();
        _clientDecryptor?.Dispose();
        _upstreamEncryptor?.Dispose();
        _upstreamDecryptor?.Dispose();
        _clientFacingIdentity.Dispose();
        _upstreamIdentity.Dispose();
    }

    private static byte[] Transform(AesCfb8? cipher, ReadOnlySpan<byte> input, bool decrypt)
    {
        if (cipher is null)
        {
            return input.ToArray();
        }

        var output = new byte[input.Length];
        if (decrypt)
        {
            cipher.Decrypt(input, output);
        }
        else
        {
            cipher.Encrypt(input, output);
        }

        return output;
    }
}
