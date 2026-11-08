using System.Text.Json;
using JERCPLD.Codec.Bedrock.Metadata;

namespace JERCPLD.VersionMap.Content;

/// <summary>
/// 每版本实体元数据槽位映射表：源槽 -> 目标槽 + 目标版本默认槽。
/// 表内 verified=false 表示尚未用真实客户端包体验证。
/// </summary>
public sealed class MetadataSlotMap
{
    private readonly Dictionary<int, int> _remap;
    private readonly List<EntityMetadataEntry> _defaults;

    private MetadataSlotMap(int source, int target, bool verified, string note,
        Dictionary<int, int> remap, List<EntityMetadataEntry> defaults)
    {
        SourceProtocol = source;
        TargetProtocol = target;
        Verified = verified;
        Note = note;
        _remap = remap;
        _defaults = defaults;
    }

    public int SourceProtocol { get; }

    public int TargetProtocol { get; }

    public bool Verified { get; }

    public string Note { get; }

    public static MetadataSlotMap V2225ToV2193 { get; } = LoadEmbedded("v2225_to_v2193.json");

    public static MetadataSlotMap V2193ToV2225 { get; } = LoadEmbedded("v2193_to_v2225.json");

    public IReadOnlyList<EntityMetadataEntry> Apply(IEnumerable<EntityMetadataEntry> entries)
    {
        var result = new SortedDictionary<int, EntityMetadataEntry>();
        foreach (var entry in entries)
        {
            var targetId = _remap.TryGetValue(entry.Id, out var remapped) ? remapped : entry.Id;
            if (targetId < 0)
            {
                continue;
            }

            result[targetId] = entry with { Id = targetId };
        }

        foreach (var defaultEntry in _defaults)
        {
            result.TryAdd(defaultEntry.Id, defaultEntry);
        }

        return result.Values.ToList();
    }

    public static MetadataSlotMap LoadEmbedded(string resourceFileName)
    {
        var assembly = typeof(MetadataSlotMap).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.Contains(".metadata.", StringComparison.OrdinalIgnoreCase)
                                    && name.EndsWith(resourceFileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"找不到嵌入资源 {resourceFileName}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取嵌入资源 {resourceName}");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var remap = new Dictionary<int, int>();
        if (root.TryGetProperty("remap", out var remapElement))
        {
            foreach (var property in remapElement.EnumerateObject())
            {
                remap[int.Parse(property.Name)] = property.Value.GetInt32();
            }
        }

        var defaults = new List<EntityMetadataEntry>();
        if (root.TryGetProperty("defaults", out var defaultsElement))
        {
            foreach (var item in defaultsElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetInt32();
                var kind = Enum.Parse<MetadataValueKind>(item.GetProperty("kind").GetString() ?? "Byte");
                var value = item.GetProperty("value");
                defaults.Add(new EntityMetadataEntry(id, kind, ConvertValue(kind, value)));
            }
        }

        return new MetadataSlotMap(
            root.GetProperty("source").GetInt32(),
            root.GetProperty("target").GetInt32(),
            root.GetProperty("verified").GetBoolean(),
            root.GetProperty("note").GetString() ?? string.Empty,
            remap,
            defaults);
    }

    private static object ConvertValue(MetadataValueKind kind, JsonElement value)
        => kind switch
        {
            MetadataValueKind.Byte => (sbyte)value.GetInt32(),
            MetadataValueKind.Short => (short)value.GetInt32(),
            MetadataValueKind.Int => value.GetInt32(),
            MetadataValueKind.Int64 => value.GetInt64(),
            MetadataValueKind.Float => value.GetSingle(),
            MetadataValueKind.String => value.GetString() ?? string.Empty,
            _ => throw new NotSupportedException($"默认槽不支持类型 {kind}"),
        };
}
