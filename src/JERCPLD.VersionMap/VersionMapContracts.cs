using JERCPLD.Codec.Bedrock;
using JERCPLD.Core;
using JERCPLD.Core.Ir;
using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.VersionMap;

/// <summary>
/// 星型拓扑的边：某个具体版本与 IR 之间的双向映射。
/// 每个版本实现一个适配器，翻译时版本之间不直接两两对接。
/// </summary>
public interface IVersionStarAdapter
{
    ProtocolVersion Version { get; }

    string Name { get; }

    bool Supports(int packetId);

    /// <summary>该版本的原始包 -> IR。</summary>
    IrPacket Decode(BedrockPacketFrame frame);

    /// <summary>IR -> 该版本的原始包。</summary>
    BedrockPacketFrame Encode(IrPacket packet);
}

/// <summary>
/// 相邻版本之间的链式兜底步骤：直接对线格式包（varint ID + 包体）做增量改写。
/// 只适用于两个相邻版本差异很小、可以逐字段或逐 ID 修正的场景；
/// 差异较大时应当新增星型适配器，而不是继续堆链式步骤。
/// </summary>
public interface IAdjacentVersionStep
{
    ProtocolVersion From { get; }

    /// <summary>必须等于 From 的下一个版本。</summary>
    ProtocolVersion To { get; }

    /// <summary>该步骤会改写的包 ID；未列出的包默认原样通过。</summary>
    IReadOnlyCollection<int> AffectedPacketIds { get; }

    /// <summary>From -> To。</summary>
    ReadOnlyMemory<byte> Upgrade(ReadOnlyMemory<byte> rawPacket);

    /// <summary>To -> From。</summary>
    ReadOnlyMemory<byte> Downgrade(ReadOnlyMemory<byte> rawPacket);
}
