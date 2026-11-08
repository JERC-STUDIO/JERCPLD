using System.Text.Json;
using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.Codec.Bedrock.Encryption;

/// <summary>
/// ServerToClientHandshake / ClientToServerHandshake 与 Login 身份链的读取。
///
/// 线上格式：
///  - ServerToClientHandshake(3)：Bedrock string（JWT），payload 含 salt，header 含 x5u；
///  - ClientToServerHandshake(4)：空包体；
///  - Login(1)：int32 BE ClientNetworkVersion + Bedrock string（connection request JSON）。
/// </summary>
public static class BedrockHandshakeCodec
{
    public static string ReadServerToClientHandshake(BedrockPacketFrame frame)
    {
        var reader = new BinaryReaderEx(frame.Body);
        return reader.ReadString();
    }

    public static BedrockPacketFrame WriteServerToClientHandshake(string jwt)
    {
        var writer = new BinaryWriterEx();
        writer.WriteString(jwt);
        return new BedrockPacketFrame(BedrockPacketIds.ServerToClientHandshake, writer.ToArray());
    }

    public static BedrockPacketFrame WriteClientToServerHandshake()
        => new(BedrockPacketIds.ClientToServerHandshake, ReadOnlyMemory<byte>.Empty);

    /// <summary>解析服务端握手 JWT，返回服务端公钥与 salt。</summary>
    public static bool TryGetServerHandshakeInfo(string jwt, out string publicKeyBase64, out byte[] salt)
    {
        publicKeyBase64 = string.Empty;
        salt = Array.Empty<byte>();

        if (!BedrockJwt.TryParse(jwt, out var header, out var payload))
        {
            return false;
        }

        if (!header.TryGetProperty("x5u", out var x5u) || x5u.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!payload.TryGetProperty("salt", out var saltValue) || saltValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        publicKeyBase64 = x5u.GetString() ?? string.Empty;
        salt = Convert.FromBase64String(saltValue.GetString() ?? string.Empty);
        return publicKeyBase64.Length > 0 && salt.Length > 0;
    }

    /// <summary>读取 Login 的 Client Network Version 与 connection request JSON。</summary>
    public static bool TryReadLogin(
        BedrockPacketFrame frame,
        out int clientProtocol,
        out string connectionRequestJson)
    {
        clientProtocol = 0;
        connectionRequestJson = string.Empty;
        if (frame.Body.Length < 4)
        {
            return false;
        }

        var reader = new BinaryReaderEx(frame.Body);
        clientProtocol = (int)reader.ReadUInt32BE();
        try
        {
            connectionRequestJson = reader.ReadString();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>从 Login 的 connection request 中提取客户端公钥（client JWT 优先，其次身份链末尾）。</summary>
    public static string? GetClientPublicKey(string connectionRequestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(connectionRequestJson);
            var root = document.RootElement;

            if (root.TryGetProperty("client", out var client))
            {
                var clientToken = client.ValueKind switch
                {
                    JsonValueKind.String => client.GetString(),
                    JsonValueKind.Object when client.TryGetProperty("Jwt", out var jwt) => jwt.GetString(),
                    JsonValueKind.Object when client.TryGetProperty("jwt", out var jwtLower) => jwtLower.GetString(),
                    _ => null,
                };

                if (clientToken is not null)
                {
                    var key = BedrockJwt.GetPublicKey(clientToken);
                    if (key is not null)
                    {
                        return key;
                    }
                }
            }

            if (root.TryGetProperty("chain", out var chain) && chain.ValueKind == JsonValueKind.Array)
            {
                for (var index = chain.GetArrayLength() - 1; index >= 0; index--)
                {
                    var token = chain[index].GetString();
                    if (token is null)
                    {
                        continue;
                    }

                    var key = BedrockJwt.GetPublicKey(token);
                    if (key is not null)
                    {
                        return key;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
