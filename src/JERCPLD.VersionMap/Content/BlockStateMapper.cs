namespace JERCPLD.VersionMap.Content;

public enum BlockStateMapKind
{
    /// <summary>名称与状态完全一致。</summary>
    Exact = 0,

    /// <summary>名称被内容降级表改写，状态完全一致。</summary>
    FallbackName = 1,

    /// <summary>按目标方块的状态键过滤后匹配（旧版没有这些状态，如红石线连接位）。</summary>
    FilteredState = 2,

    /// <summary>目标版本没有可用状态，退化为目标方块的首个状态。</summary>
    DefaultState = 3,

    /// <summary>无法映射。</summary>
    Unmapped = 4,
}

public readonly record struct BlockStateMapResult(
    BlockStateMapKind Kind,
    uint SourceHash,
    uint TargetHash,
    string SourceName,
    string TargetName);

/// <summary>把源协议的方块状态哈希映射为目标协议。</summary>
public sealed class BlockStateMapper
{
    private readonly BlockStateTable _source;
    private readonly BlockStateTable _target;
    private readonly ContentFallbackMap? _fallback;

    public BlockStateMapper(BlockStateTable source, BlockStateTable target, ContentFallbackMap? fallback = null)
    {
        _source = source;
        _target = target;
        _fallback = fallback;
    }

    public BlockStateMapResult Map(uint sourceHash)
    {
        if (!_source.TryGet(sourceHash, out var entry))
        {
            return new BlockStateMapResult(BlockStateMapKind.Unmapped, sourceHash, 0, string.Empty, string.Empty);
        }

        if (_target.TryGetHash(entry.Name, entry.States, out var exact))
        {
            return new BlockStateMapResult(BlockStateMapKind.Exact, sourceHash, exact, entry.Name, entry.Name);
        }

        var mappedName = _fallback?.MapBlockType(entry.Name) ?? entry.Name;
        if (!string.Equals(mappedName, entry.Name, StringComparison.Ordinal)
            && _target.TryGetHash(mappedName, entry.States, out var fallbackHash))
        {
            return new BlockStateMapResult(
                BlockStateMapKind.FallbackName, sourceHash, fallbackHash, entry.Name, mappedName);
        }

        var candidates = _target.GetByName(mappedName);
        if (candidates.Count > 0)
        {
            // 旧版本可能没有新版本新增的状态键（例如红石线的连接位、火/紫颂植株的连接位）。
            // 按目标方块实际存在的状态键过滤；如果过滤后能精确命中，则不是丢信息，
            // 而是旧客户端会自行推导这些状态。
            var targetKeys = candidates
                .SelectMany(candidate => candidate.States.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            var filtered = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in entry.States)
            {
                if (targetKeys.Contains(pair.Key))
                {
                    filtered[pair.Key] = pair.Value;
                }
            }

            if (_target.TryGetHash(mappedName, filtered, out var filteredHash))
            {
                return new BlockStateMapResult(
                    BlockStateMapKind.FilteredState, sourceHash, filteredHash, entry.Name, mappedName);
            }

            var chosen = candidates.FirstOrDefault(candidate => candidate.States.Count == 0) ?? candidates[0];
            return new BlockStateMapResult(
                BlockStateMapKind.DefaultState, sourceHash, chosen.Hash, entry.Name, mappedName);
        }

        return new BlockStateMapResult(BlockStateMapKind.Unmapped, sourceHash, 0, entry.Name, mappedName);
    }

    public BlockStateMapReport MapAll()
    {
        var counts = new Dictionary<BlockStateMapKind, int>();
        var samples = new Dictionary<BlockStateMapKind, List<BlockStateMapResult>>();
        foreach (var entry in _source.Entries)
        {
            var result = Map(entry.Hash);
            counts[result.Kind] = counts.GetValueOrDefault(result.Kind) + 1;
            if (result.Kind != BlockStateMapKind.Exact)
            {
                if (!samples.TryGetValue(result.Kind, out var list))
                {
                    list = new List<BlockStateMapResult>();
                    samples[result.Kind] = list;
                }

                if (list.Count < 20)
                {
                    list.Add(result);
                }
            }
        }

        return new BlockStateMapReport(_source.Protocol, _target.Protocol, _source.StateCount, counts, samples);
    }
}

public sealed record BlockStateMapReport(
    int SourceProtocol,
    int TargetProtocol,
    int TotalStates,
    IReadOnlyDictionary<BlockStateMapKind, int> Counts,
    IReadOnlyDictionary<BlockStateMapKind, List<BlockStateMapResult>> Samples)
{
    public double ExactRate => TotalStates == 0 ? 0 : (double)Counts.GetValueOrDefault(BlockStateMapKind.Exact) / TotalStates;
}
