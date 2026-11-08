namespace JERCPLD.Core.Ir;

/// <summary>
/// 版本无关中间表示（IR）的值节点。刻意保持极小：只覆盖协议字段所需的标量、字节串、列表和映射。
/// </summary>
public abstract record IrValue
{
    public static IrValue Null { get; } = new IrNull();

    public static IrValue From(long value) => new IrInt(value);

    public static IrValue From(double value) => new IrFloat(value);

    public static IrValue From(string value) => new IrString(value);

    public static IrValue From(bool value) => new IrBool(value);

    public static IrValue From(byte[] value) => new IrBytes(value);

    public long? AsInt() => this is IrInt value ? value.Value : null;

    public string? AsString() => this is IrString value ? value.Value : null;

    public bool? AsBool() => this is IrBool value ? value.Value : null;
}

public sealed record IrNull : IrValue;

public sealed record IrInt(long Value) : IrValue;

public sealed record IrFloat(double Value) : IrValue;

public sealed record IrString(string Value) : IrValue;

public sealed record IrBool(bool Value) : IrValue;

public sealed record IrBytes(byte[] Value) : IrValue;

public sealed record IrList(IReadOnlyList<IrValue> Items) : IrValue;

public sealed record IrMap(IReadOnlyDictionary<string, IrValue> Entries) : IrValue
{
    public static readonly IrMap Empty = new(new Dictionary<string, IrValue>());

    public IrValue? Get(string key) => Entries.TryGetValue(key, out var value) ? value : null;
}
