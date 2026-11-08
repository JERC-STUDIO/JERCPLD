using System.Text.Json;

namespace JERCPLD.VersionMap.Content;

public sealed record BlockStateEntry(string Name, IReadOnlyDictionary<string, object> States, uint Hash)
{
    public string Key => BuildKey(Name, States);

    public static string BuildKey(string name, IReadOnlyDictionary<string, object> states)
    {
        if (states.Count == 0)
        {
            return name + "|";
        }

        var parts = states
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={FormatValue(pair.Value)}");
        return name + "|" + string.Join(",", parts);
    }

    private static string FormatValue(object value)
        => value switch
        {
            bool flag => flag ? "1" : "0",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };
}

/// <summary>
/// 某一协议版本的方块状态表（由官方 bedrock-samples 的 mojang-blocks.json 生成）。
/// 表内保存状态的运行时哈希，供区块/SubChunk 翻译使用。
/// </summary>
public sealed class BlockStateTable
{
    private readonly Dictionary<uint, BlockStateEntry> _byHash;
    private readonly Dictionary<string, uint> _byKey;
    private readonly Dictionary<string, List<BlockStateEntry>> _byName;

    private BlockStateTable(
        int protocol,
        Dictionary<uint, BlockStateEntry> byHash,
        Dictionary<string, uint> byKey,
        Dictionary<string, List<BlockStateEntry>> byName)
    {
        Protocol = protocol;
        _byHash = byHash;
        _byKey = byKey;
        _byName = byName;
    }

    public int Protocol { get; }

    public int StateCount => _byHash.Count;

    public IEnumerable<BlockStateEntry> Entries => _byHash.Values;

    public static BlockStateTable Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var protocol = root.GetProperty("protocol").GetInt32();

        var byHash = new Dictionary<uint, BlockStateEntry>();
        var byKey = new Dictionary<string, uint>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<BlockStateEntry>>(StringComparer.Ordinal);

        foreach (var block in root.GetProperty("blocks").EnumerateArray())
        {
            var name = block.GetProperty("name").GetString() ?? string.Empty;
            var states = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var state in block.GetProperty("states").EnumerateObject())
            {
                states[state.Name] = state.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => state.Value.GetInt32(),
                    _ => state.Value.GetString() ?? string.Empty,
                };
            }

            var hash = block.GetProperty("hash").GetUInt32();
            var entry = new BlockStateEntry(name, states, hash);
            byHash[hash] = entry;
            byKey[entry.Key] = hash;

            if (!byName.TryGetValue(name, out var list))
            {
                list = new List<BlockStateEntry>();
                byName[name] = list;
            }

            list.Add(entry);
        }

        return new BlockStateTable(protocol, byHash, byKey, byName);
    }

    public bool TryGet(uint hash, out BlockStateEntry entry) => _byHash.TryGetValue(hash, out entry!);

    public bool TryGetHash(string name, IReadOnlyDictionary<string, object> states, out uint hash)
        => _byKey.TryGetValue(BlockStateEntry.BuildKey(name, states), out hash);

    public IReadOnlyList<BlockStateEntry> GetByName(string name)
        => _byName.TryGetValue(name, out var list) ? list : Array.Empty<BlockStateEntry>();
}
