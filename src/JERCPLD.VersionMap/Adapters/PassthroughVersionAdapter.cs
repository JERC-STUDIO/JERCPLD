using JERCPLD.Codec.Bedrock;
using JERCPLD.Core;
using JERCPLD.Core.Ir;
using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.VersionMap.Adapters;

/// <summary>
/// 直通适配器：把整个包的 ID 与包体原样塞进 IR，再原样取出。
///
/// 它不解析任何字段、不做任何版本翻译，只用于验证
/// 「RakNet -> Proxy -> VersionMap -> NetherNet」管线的连通性。
/// 生产环境绝不能注册它来冒充已完成的版本适配。
/// </summary>
public sealed class PassthroughVersionAdapter : IVersionStarAdapter
{
    private const string PacketIdField = "packet_id";
    private const string BodyField = "body";

    public PassthroughVersionAdapter(ProtocolVersion version) => Version = version;

    public ProtocolVersion Version { get; }

    public string Name => $"passthrough@{Version.Number}";

    public bool Supports(int packetId) => true;

    public IrPacket Decode(BedrockPacketFrame frame)
    {
        var fields = new Dictionary<string, IrValue>
        {
            [PacketIdField] = new IrInt(frame.PacketId),
            [BodyField] = new IrBytes(frame.Body.ToArray()),
        };

        return new IrPacket("bedrock.passthrough", new IrMap(fields), Version);
    }

    public BedrockPacketFrame Encode(IrPacket packet)
    {
        if (packet.Get(PacketIdField) is not IrInt packetId)
        {
            throw new InvalidOperationException("直通 IR 缺少 packet_id 字段");
        }

        if (packet.Get(BodyField) is not IrBytes body)
        {
            throw new InvalidOperationException("直通 IR 缺少 body 字段");
        }

        return new BedrockPacketFrame((int)packetId.Value, body.Value);
    }
}
