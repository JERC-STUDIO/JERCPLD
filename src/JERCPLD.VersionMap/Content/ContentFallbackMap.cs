using System.Reflection;
using System.Text.Json;

namespace JERCPLD.VersionMap.Content;

/// <summary>
/// 新版本新增内容在旧版本中的降级映射。
///
/// 只在「旧客户端不认识该名称」时使用；映射目标选择语义最接近的旧内容，
/// 避免直接丢弃导致客户端解析失败。数据按协议对存放在 Content/fallbacks/*.json。
/// </summary>
public sealed class ContentFallbackMap
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, string> _blockTypes;
    private readonly Dictionary<string, string> _biomes;
    private readonly Dictionary<string, string> _entities;
    private readonly Dictionary<string, string> _mobEffects;

    private ContentFallbackMap(
        int sourceProtocol,
        int targetProtocol,
        Dictionary<string, string> blockTypes,
        Dictionary<string, string> biomes,
        Dictionary<string, string> entities,
        Dictionary<string, string> mobEffects)
    {
        SourceProtocol = sourceProtocol;
        TargetProtocol = targetProtocol;
        _blockTypes = blockTypes;
        _biomes = biomes;
        _entities = entities;
        _mobEffects = mobEffects;
    }

    public int SourceProtocol { get; }

    public int TargetProtocol { get; }

    public static ContentFallbackMap V2225ToV2193 { get; } = LoadEmbedded("v2225_to_v2193.json");

    public string MapBlockType(string name) => Map(_blockTypes, name);

    public string MapBiome(string name) => Map(_biomes, name);

    public string MapEntity(string name) => Map(_entities, name);

    /// <summary>返回映射后的效果 ID 字符串；找不到时返回原值。</summary>
    public string MapMobEffect(string nameOrId) => Map(_mobEffects, nameOrId);

    public static ContentFallbackMap LoadEmbedded(string resourceFileName)
    {
        var assembly = typeof(ContentFallbackMap).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceFileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"找不到嵌入资源 {resourceFileName}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取嵌入资源 {resourceName}");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        return new ContentFallbackMap(
            root.GetProperty("sourceProtocol").GetInt32(),
            root.GetProperty("targetProtocol").GetInt32(),
            ReadMap(root, "blockTypes"),
            ReadMap(root, "biomes"),
            ReadMap(root, "entities"),
            ReadMap(root, "mobEffects"));
    }

    private static Dictionary<string, string> ReadMap(JsonElement root, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.GetString() ?? property.Name;
        }

        return result;
    }

    private static string Map(IReadOnlyDictionary<string, string> map, string value)
        => map.TryGetValue(value, out var mapped) ? mapped : value;
}
