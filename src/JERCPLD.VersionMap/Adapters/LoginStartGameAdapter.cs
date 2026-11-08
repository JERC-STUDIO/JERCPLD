using JERCPLD.Codec.Bedrock;
using JERCPLD.Core;
using JERCPLD.Core.Ir;
using JERCPLD.Protocol.Bedrock;
using JERCPLD.VersionMap.Content;

namespace JERCPLD.VersionMap.Adapters;

/// <summary>
/// 26.50 (2193) ↔ 26.60 (2225) 的星型适配器。
///
/// 已实现真实翻译的包：
///  - Login / RequestNetworkSettings：协议号；
///  - StartGame：LevelSettings 新增字段与 GameType 枚举值；
///  - NetworkSettings：压缩算法枚举；
///  - LevelChunk：末尾新增的 Is Client Biome Update；
///  - SubChunk：结果枚举 + 地形调色板名称降级。
///
/// 其余包 <see cref="Supports"/> 返回 false，由路由层按配置透传或丢弃；
/// 这不是「已完成翻译」，UI 会把它计入「透传」而不是「已翻译」。
/// </summary>
public sealed class LoginStartGameAdapter : IVersionStarAdapter
{
    private const string PacketIdField = "packet_id";
    private const string BodyField = "body";

    private readonly ContentFallbackMap? _fallback;
    private readonly MetadataSlotMap? _metadataTo2193;
    private readonly MetadataSlotMap? _metadataTo2225;

    public LoginStartGameAdapter(
        ProtocolVersion version,
        ContentFallbackMap? fallback = null,
        MetadataSlotMap? metadataTo2193 = null,
        MetadataSlotMap? metadataTo2225 = null)
    {
        Version = version;
        _fallback = fallback;
        _metadataTo2193 = metadataTo2193;
        _metadataTo2225 = metadataTo2225;
    }

    public ProtocolVersion Version { get; }

    public string Name => $"login-startgame@{Version.Number}";

    public bool Supports(int packetId)
        => packetId is BedrockPacketIds.Login
            or BedrockPacketIds.StartGame
            or BedrockPacketIds.RequestNetworkSettings
            or BedrockPacketIds.NetworkSettings
            or BedrockPacketIds.LevelChunk
            or BedrockPacketIds.SubChunk
            or BedrockPacketIds.SetActorData;

    public IrPacket Decode(BedrockPacketFrame frame)
    {
        var fields = new Dictionary<string, IrValue>
        {
            [PacketIdField] = new IrInt(frame.PacketId),
            [BodyField] = new IrBytes(frame.Body.ToArray()),
        };

        var typeId = frame.PacketId switch
        {
            BedrockPacketIds.Login => "login",
            BedrockPacketIds.StartGame => "start_game",
            BedrockPacketIds.RequestNetworkSettings => "request_network_settings",
            BedrockPacketIds.NetworkSettings => "network_settings",
            BedrockPacketIds.LevelChunk => "level_chunk",
            BedrockPacketIds.SubChunk => "sub_chunk",
            BedrockPacketIds.SetActorData => "set_actor_data",
            _ => IrPacket.UnknownTypeId,
        };

        return new IrPacket(typeId, new IrMap(fields), Version);
    }

    public BedrockPacketFrame Encode(IrPacket packet)
    {
        if (packet.Get(PacketIdField) is not IrInt packetId || packet.Get(BodyField) is not IrBytes body)
        {
            throw new InvalidOperationException("Login/StartGame IR 缺少 packet_id 或 body 字段");
        }

        var target = Version.Number;
        var source = packet.SourceVersion.Number;
        var bytes = body.Value;

        if (packetId.Value == BedrockPacketIds.Login
            || packetId.Value == BedrockPacketIds.RequestNetworkSettings)
        {
            bytes = RewriteProtocolNumber(bytes, source, target);
        }
        else if (packetId.Value == BedrockPacketIds.StartGame && StartGameWireRewriter.CanRewrite(source, target))
        {
            bytes = StartGameWireRewriter.Rewrite(bytes, source, target);
        }
        else if (packetId.Value == BedrockPacketIds.NetworkSettings && StartGameWireRewriter.CanRewrite(source, target))
        {
            bytes = RewriteNetworkSettings(bytes, source, target);
        }
        else if (packetId.Value == BedrockPacketIds.LevelChunk && StartGameWireRewriter.CanRewrite(source, target))
        {
            var fallback = source == StartGameWireRewriter.Protocol2225
                           && target == StartGameWireRewriter.Protocol2193
                ? _fallback
                : null;
            bytes = LevelChunkWireRewriter.Rewrite(bytes, source, target, fallback);
        }
        else if (packetId.Value == BedrockPacketIds.SubChunk && StartGameWireRewriter.CanRewrite(source, target))
        {
            // 只有 2225 -> 2193 需要把新方块名称降级；反向保持原样。
            var fallback = source == StartGameWireRewriter.Protocol2225
                           && target == StartGameWireRewriter.Protocol2193
                ? _fallback
                : null;
            bytes = SubChunkWireRewriter.Rewrite(bytes, source, target, fallback);
        }
        else if (packetId.Value == BedrockPacketIds.SetActorData && StartGameWireRewriter.CanRewrite(source, target))
        {
            var slotMap = source == StartGameWireRewriter.Protocol2225
                          && target == StartGameWireRewriter.Protocol2193
                ? _metadataTo2193
                : _metadataTo2225;
            bytes = SetActorDataWireRewriter.Rewrite(bytes, source, target, slotMap);
        }

        return new BedrockPacketFrame((int)packetId.Value, bytes);
    }

    private static byte[] RewriteProtocolNumber(byte[] body, int source, int target)
    {
        if (source == target)
        {
            return body;
        }

        if (body.Length < 4)
        {
            throw new InvalidDataException("包体长度不足 4 字节，无法改写协议号");
        }

        var copy = (byte[])body.Clone();
        copy[0] = (byte)(target >> 24);
        copy[1] = (byte)(target >> 16);
        copy[2] = (byte)(target >> 8);
        copy[3] = (byte)target;
        return copy;
    }

    /// <summary>
    /// NetworkSettings 的 CompressionAlgorithm 枚举在 2225 中把 None 从 2 改成 65535。
    /// 其他值（ZLib=0、Snappy=1）不变。
    /// </summary>
    private static byte[] RewriteNetworkSettings(byte[] body, int source, int target)
    {
        if (body.Length < 4)
        {
            throw new InvalidDataException("NetworkSettings 包体长度不足 4 字节");
        }

        var copy = (byte[])body.Clone();
        var algorithm = (ushort)(copy[2] | (copy[3] << 8));

        if (source == StartGameWireRewriter.Protocol2193 && target == StartGameWireRewriter.Protocol2225 && algorithm == 2)
        {
            copy[2] = 0xFF;
            copy[3] = 0xFF;
        }
        else if (source == StartGameWireRewriter.Protocol2225 && target == StartGameWireRewriter.Protocol2193 && algorithm == 65535)
        {
            copy[2] = 2;
            copy[3] = 0;
        }

        return copy;
    }
}
