using System.Text;
using System.Text.Json;

namespace JERCPLD.Codec.Bedrock.Encryption;

/// <summary>Bedrock 身份链/握手 JWT 的最小解析工具（只读，不做签名校验）。</summary>
public static class BedrockJwt
{
    /// <summary>拆分 JWT 并返回 header / payload 的 JSON 元素。</summary>
    public static bool TryParse(string token, out JsonElement header, out JsonElement payload)
    {
        header = default;
        payload = default;

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            header = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement.Clone();
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement.Clone();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>从 JWT header 的 x5u 或 payload 的 identityPublicKey 读取公钥。</summary>
    public static string? GetPublicKey(string token)
    {
        if (!TryParse(token, out var header, out var payload))
        {
            return null;
        }

        if (header.TryGetProperty("x5u", out var x5u) && x5u.ValueKind == JsonValueKind.String)
        {
            return x5u.GetString();
        }

        if (payload.TryGetProperty("identityPublicKey", out var identityKey)
            && identityKey.ValueKind == JsonValueKind.String)
        {
            return identityKey.GetString();
        }

        return null;
    }

    public static JsonElement? GetPayloadProperty(string token, string propertyName)
    {
        if (!TryParse(token, out _, out var payload)
            || !payload.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value;
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        return Convert.FromBase64String(normalized);
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>测试/离线身份用：把 header/payload 拼成未签名 JWT（第三段为占位）。</summary>
    public static string ComposeForTesting(string headerJson, string payloadJson, string signature = "sig")
        => Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))
           + "."
           + Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))
           + "."
           + Base64UrlEncode(Encoding.UTF8.GetBytes(signature));
}
