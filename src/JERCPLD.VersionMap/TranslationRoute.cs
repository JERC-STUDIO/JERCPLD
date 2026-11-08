using JERCPLD.Codec.Bedrock;
using JERCPLD.Core;
using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.VersionMap;

public enum StepDirection
{
    Upgrade = 0,
    Downgrade = 1,
}

public sealed record DirectedStep(IAdjacentVersionStep Step, StepDirection Direction);

/// <summary>
/// 一条翻译路径：
/// 源版本 --PreChain(原始包链式修正)--> 源锚点 --解码--> IR --编码--> 目标锚点 --PostChain--> 目标版本。
/// </summary>
public sealed record TranslationRoute(
    ProtocolVersion From,
    ProtocolVersion To,
    IVersionStarAdapter SourceAdapter,
    IVersionStarAdapter TargetAdapter,
    IReadOnlyList<DirectedStep> PreChain,
    IReadOnlyList<DirectedStep> PostChain)
{
    public string Describe()
    {
        var source = PreChain.Count == 0
            ? $"源锚点 {SourceAdapter.Version}"
            : $"{From} 经 {PreChain.Count} 步相邻链到源锚点 {SourceAdapter.Version}";
        var target = PostChain.Count == 0
            ? $"目标锚点 {TargetAdapter.Version}"
            : $"目标锚点 {TargetAdapter.Version} 经 {PostChain.Count} 步相邻链到 {To}";
        return $"{source} | IR | {target}";
    }
}

public enum TranslationStatus
{
    Translated = 0,
    PassedThrough = 1,
    Dropped = 2,
}

public sealed record TranslationOutcome(TranslationStatus Status, ReadOnlyMemory<byte> Output, string? Detail = null);
