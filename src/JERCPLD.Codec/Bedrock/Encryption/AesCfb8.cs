using System.Security.Cryptography;

namespace JERCPLD.Codec.Bedrock.Encryption;

/// <summary>
/// Bedrock 使用的 AES-256-CFB8 流加密。
///
/// Bedrock 在 ServerToClientHandshake 之后对双向流量使用 CFB8：
///  - key = SHA256(salt || ECDH shared secret)，32 字节；
///  - IV = key 的前 16 字节；
///  - 每个方向的 cipher 状态独立。
/// </summary>
public sealed class AesCfb8 : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _register;
    private readonly byte[] _keystream = new byte[16];
    private int _keystreamPosition = 16;

    public AesCfb8(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Bedrock 加密要求 32 字节 key", nameof(key));
        }

        if (iv.Length != 16)
        {
            throw new ArgumentException("Bedrock 加密要求 16 字节 IV", nameof(iv));
        }

        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key.ToArray();
        _register = iv.ToArray();
    }

    public void Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> ciphertext)
    {
        if (ciphertext.Length < plaintext.Length)
        {
            throw new ArgumentException("输出缓冲区太小", nameof(ciphertext));
        }

        for (var i = 0; i < plaintext.Length; i++)
        {
            var keyByte = NextKeyByte();
            var encrypted = (byte)(plaintext[i] ^ keyByte);
            ciphertext[i] = encrypted;
            ShiftRegister(encrypted);
        }
    }

    public void Decrypt(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
    {
        if (plaintext.Length < ciphertext.Length)
        {
            throw new ArgumentException("输出缓冲区太小", nameof(plaintext));
        }

        for (var i = 0; i < ciphertext.Length; i++)
        {
            var keyByte = NextKeyByte();
            var encrypted = ciphertext[i];
            plaintext[i] = (byte)(encrypted ^ keyByte);
            ShiftRegister(encrypted);
        }
    }

    public void Dispose() => _aes.Dispose();

    private byte NextKeyByte()
    {
        if (_keystreamPosition == 16)
        {
            _aes.EncryptEcb(_register, _keystream, PaddingMode.None);
            _keystreamPosition = 0;
        }

        return _keystream[_keystreamPosition++];
    }

    private void ShiftRegister(byte feedback)
    {
        Buffer.BlockCopy(_register, 1, _register, 0, 15);
        _register[15] = feedback;
    }
}
