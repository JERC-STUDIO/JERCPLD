namespace JERCPLD.Protocol.Bedrock;

/// <summary>
/// 解包后的 Bedrock 游戏包：数字包 ID + 包体（不含 ID 的 varint 编码）。
/// 线格式上的完整包 = varint(PacketId) + Body。
/// </summary>
public sealed record BedrockPacketFrame(int PacketId, ReadOnlyMemory<byte> Body)
{
    public override string ToString() => $"{BedrockPacketIds.NameOf(PacketId)} ({Body.Length} bytes)";
}
