using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.Proxy;

public sealed class TranslationProxyOptions
{
    public required Core.ProtocolVersion ClientVersion { get; init; }

    public required Core.ProtocolVersion ServerVersion { get; init; }

    /// <summary>源适配器不支持的包是否原样透传。默认丢弃，避免把错误格式送给对端。</summary>
    public bool PassUnknownPackets { get; init; }

    /// <summary>是否记录逐包诊断（方向、包 ID、长度、翻译结果）。</summary>
    public bool EnableDiagnostics { get; init; } = true;
}
