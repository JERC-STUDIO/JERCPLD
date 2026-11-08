using System.Security.Cryptography;
using System.Text.Json;

namespace JERCPLD.Codec.Bedrock.Encryption;

/// <summary>
/// 一份 P-384 身份密钥，同时用于 JWT 签名（ECDSA）与握手密钥协商（ECDH）。
/// 公钥以 SubjectPublicKeyInfo 的 Base64 形式出现在 JWT header 的 x5u。
/// </summary>
public sealed class BedrockIdentityKey : IDisposable
{
    private readonly ECDsa _ecdsa;
    private readonly ECDiffieHellman _ecdh;

    private BedrockIdentityKey(ECDsa ecdsa, ECDiffieHellman ecdh)
    {
        _ecdsa = ecdsa;
        _ecdh = ecdh;
        PublicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    public string PublicKeyBase64 { get; }

    public static BedrockIdentityKey Create()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(pkcs8, out _);
        CryptographicOperations.ZeroMemory(pkcs8);
        return new BedrockIdentityKey(ecdsa, ecdh);
    }

    public byte[] DeriveSharedSecret(string peerPublicKeyBase64)
    {
        var peerBytes = Convert.FromBase64String(peerPublicKeyBase64);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerBytes, out _);
        return _ecdh.DeriveKeyMaterial(peer.PublicKey);
    }

    /// <summary>按 JWT ES384 规则签名，返回完整的三段式 token。</summary>
    public string SignJwt(string headerJson, string payloadJson, string? signatureOverride = null)
    {
        var signingInput = BedrockJwt.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(headerJson))
                           + "."
                           + BedrockJwt.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payloadJson));

        var signature = signatureOverride is null
            ? _ecdsa.SignData(System.Text.Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA384)
            : Convert.FromBase64String(signatureOverride);

        return signingInput + "." + BedrockJwt.Base64UrlEncode(signature);
    }

    /// <summary>
    /// 生成离线模式可用的 Login connection request（自签名身份链）。
    /// 仅适用于 online-mode=false 的 BDS；在线模式仍需真实 Xbox 身份链。
    /// </summary>
    public string CreateOfflineLoginRequest(string displayName, string? identity = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var extraData = new
        {
            displayName,
            identity = identity ?? Guid.NewGuid().ToString(),
            XUID = string.Empty,
            titleId = "89692877",
        };

        var header = $"{{\"alg\":\"ES384\",\"x5u\":\"{PublicKeyBase64}\"}}";

        var chainPayload = JsonSerializer.Serialize(new
        {
            certificateAuthority = true,
            extraData,
            identityPublicKey = PublicKeyBase64,
            nbf = now - 60,
            exp = now + 86400,
        });
        var chainJwt = SignJwt(header, chainPayload);

        var clientPayload = JsonSerializer.Serialize(new
        {
            extraData,
            identityPublicKey = PublicKeyBase64,
            nbf = now - 60,
            exp = now + 86400,
        });
        var clientJwt = SignJwt(header, clientPayload);

        return JsonSerializer.Serialize(new
        {
            chain = new[] { chainJwt },
            client = clientJwt,
        });
    }

    public void Dispose()
    {
        _ecdsa.Dispose();
        _ecdh.Dispose();
    }
}
