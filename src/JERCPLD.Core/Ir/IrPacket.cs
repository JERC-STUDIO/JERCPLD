namespace JERCPLD.Core.Ir;

/// <summary>
/// 版本无关的单个协议包。所有版本适配器都只与 IR 交互（星型拓扑的中心）。
/// </summary>
/// <param name="TypeId">规范化包类型名，例如 "login"、"start_game"。不是数字包 ID。</param>
/// <param name="Fields">规范化字段名（snake_case）到值的映射。</param>
/// <param name="SourceVersion">产生该 IR 的源版本，便于诊断与回退。</param>
/// <param name="RawPassthrough">可选：无法理解但需要原样透传的原始包体。</param>
public sealed record IrPacket(
    string TypeId,
    IrMap Fields,
    ProtocolVersion SourceVersion,
    byte[]? RawPassthrough = null)
{
    public const string UnknownTypeId = "unknown";

    public static IrPacket Passthrough(ProtocolVersion sourceVersion, ReadOnlyMemory<byte> rawPacket)
        => new(UnknownTypeId, IrMap.Empty, sourceVersion, rawPacket.ToArray());

    public IrValue? Get(string fieldName) => Fields.Get(fieldName);

    public IrPacket WithField(string fieldName, IrValue value)
    {
        var entries = new Dictionary<string, IrValue>(Fields.Entries)
        {
            [fieldName] = value,
        };

        return this with { Fields = new IrMap(entries) };
    }

    public override string ToString() => $"{TypeId}@{SourceVersion.Number}";
}
