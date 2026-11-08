using System.Security.Cryptography;

namespace JERCPLD.Codec.Bedrock.Encryption;

/// <summary>
/// Bedrock 加密握手的密钥派生：
///  - ECDH 使用 P-384；
///  - 对称密钥 = SHA256(salt || sharedSecret)。
/// 公钥在线格式是 SubjectPublicKeyInfo 的 Base64（JWT header 的 x5u / identityPublicKey）。
/// </summary>
public static class BedrockEncryptionKeys
{
    public static ECDiffieHellman CreateKeyPair()
        => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

    public static string ExportPublicKey(ECDiffieHellman keyPair)
        => Convert.ToBase64String(keyPair.ExportSubjectPublicKeyInfo());

    public static byte[] DeriveSharedSecret(ECDiffieHellman keyPair, string peerPublicKeyBase64)
    {
        var peerBytes = Convert.FromBase64String(peerPublicKeyBase64);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerBytes, out _);
        return keyPair.DeriveKeyMaterial(peer.PublicKey);
    }

    public static byte[] DeriveKey(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> sharedSecret)
    {
        var buffer = new byte[salt.Length + sharedSecret.Length];
        salt.CopyTo(buffer);
        sharedSecret.CopyTo(buffer.AsSpan(salt.Length));
        return SHA256.HashData(buffer);
    }

    public static (AesCfb8 Encryptor, AesCfb8 Decryptor) CreateCiphers(byte[] key)
    {
        var iv = key.AsSpan(0, 16);
        return (new AesCfb8(key, iv), new AesCfb8(key, iv));
    }
}
