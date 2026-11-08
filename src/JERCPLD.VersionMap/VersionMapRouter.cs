using JERCPLD.Codec.Bedrock;
using JERCPLD.Core;
using JERCPLD.Core.Ir;
using JERCPLD.Protocol.Bedrock;

namespace JERCPLD.VersionMap;

/// <summary>
/// 版本路由执行器：按「星型为主、相邻版本链式兜底」选择路径并执行翻译。
///
/// 路径选择规则：
///  1. 源版本有星型适配器 -> 直接解码到 IR；否则找最近的锚点，用相邻步骤把原始包链式修正到锚点。
///  2. 目标版本同理，反向处理。
///  3. 找不到任何可行路径时返回 null 并给出具体失败原因，绝不静默丢弃或伪造结果。
/// </summary>
public sealed class VersionMapRouter
{
    private readonly VersionMapRegistry _registry;

    public VersionMapRouter(VersionMapRegistry registry) => _registry = registry;

    public TranslationRoute? TryCreateRoute(ProtocolVersion from, ProtocolVersion to, out string failureReason)
    {
        if (from.Number == to.Number)
        {
            failureReason = "源版本与目标版本相同，不需要翻译";
            return null;
        }

        if (!TryFindAnchor(from, out var sourceAdapter, out var preChain, out var sourceReason))
        {
            failureReason = $"源版本 {from} 无法接入 IR：{sourceReason}";
            return null;
        }

        if (!TryFindAnchor(to, out var targetAdapter, out var postChain, out var targetReason))
        {
            failureReason = $"目标版本 {to} 无法接入 IR：{targetReason}";
            return null;
        }

        failureReason = string.Empty;
        return new TranslationRoute(from, to, sourceAdapter!, targetAdapter!, preChain, postChain);
    }

    public TranslationOutcome Translate(
        TranslationRoute route,
        ReadOnlyMemory<byte> rawPacket,
        bool passUnknownPackets = false)
    {
        try
        {
            var current = rawPacket;
            foreach (var directed in route.PreChain)
            {
                current = ApplyStep(directed, current);
            }

            var frame = BedrockBatchCodec.DecodePacket(current);
            if (!route.SourceAdapter.Supports(frame.PacketId))
            {
                return passUnknownPackets
                    ? new TranslationOutcome(
                        TranslationStatus.PassedThrough,
                        rawPacket,
                        $"源适配器不支持包 0x{frame.PacketId:X}，已原样透传（目标版本未必接受）")
                    : new TranslationOutcome(
                        TranslationStatus.Dropped,
                        default,
                        $"源适配器不支持包 0x{frame.PacketId:X}，已丢弃");
            }

            var ir = route.SourceAdapter.Decode(frame);
            var translated = route.TargetAdapter.Encode(ir);
            var output = (ReadOnlyMemory<byte>)BedrockBatchCodec.EncodePacket(translated);

            foreach (var directed in route.PostChain)
            {
                output = ApplyStep(directed, output);
            }

            return new TranslationOutcome(TranslationStatus.Translated, output);
        }
        catch (Exception exception)
        {
            return new TranslationOutcome(TranslationStatus.Dropped, default, $"翻译失败: {exception.Message}");
        }
    }

    private bool TryFindAnchor(
        ProtocolVersion version,
        out IVersionStarAdapter? adapter,
        out IReadOnlyList<DirectedStep> chain,
        out string failureReason)
    {
        adapter = null;
        chain = Array.Empty<DirectedStep>();
        failureReason = string.Empty;

        var exact = _registry.GetAdapter(version.Number);
        if (exact is not null)
        {
            adapter = exact;
            return true;
        }

        // 优先最近的锚点；距离相同时优先更低版本，保证路径选择可预测。
        var candidates = _registry.Adapters
            .Select(candidate => candidate.Version.Number)
            .Distinct()
            .OrderBy(number => Math.Abs(number - version.Number))
            .ThenBy(number => number < version.Number ? 0 : 1)
            .ToList();

        if (candidates.Count == 0)
        {
            failureReason = "尚未注册任何星型适配器";
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (TryBuildChain(version.Number, candidate, out var steps, out var chainReason))
            {
                adapter = _registry.GetAdapter(candidate);
                chain = steps;
                return true;
            }

            failureReason = chainReason;
        }

        return false;
    }

    private bool TryBuildChain(int fromVersion, int toVersion, out List<DirectedStep> steps, out string failureReason)
    {
        steps = new List<DirectedStep>();
        failureReason = string.Empty;

        var cursor = fromVersion;
        while (cursor != toVersion)
        {
            if (toVersion > cursor)
            {
                var step = _registry.GetStep(cursor);
                if (step is null)
                {
                    failureReason = $"缺少相邻版本步骤 {cursor}->{cursor + 1}";
                    return false;
                }

                steps.Add(new DirectedStep(step, StepDirection.Upgrade));
                cursor++;
            }
            else
            {
                var step = _registry.GetStep(cursor - 1);
                if (step is null)
                {
                    failureReason = $"缺少相邻版本步骤 {cursor - 1}->{cursor}";
                    return false;
                }

                steps.Add(new DirectedStep(step, StepDirection.Downgrade));
                cursor--;
            }
        }

        return true;
    }

    private static ReadOnlyMemory<byte> ApplyStep(DirectedStep directed, ReadOnlyMemory<byte> packet)
        => directed.Direction == StepDirection.Upgrade
            ? directed.Step.Upgrade(packet)
            : directed.Step.Downgrade(packet);
}
