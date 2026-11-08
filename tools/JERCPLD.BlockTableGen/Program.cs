using System.Text.Json;
using JERCPLD.Codec.Nbt;

if (args.Length < 3)
{
    Console.WriteLine("用法: jercpld-blocktable <mojang-blocks.json> <protocol> <output.json> [little|network]");
    return 2;
}

var inputPath = args[0];
var protocol = int.Parse(args[1]);
var outputPath = args[2];
var encoding = args.Length >= 4 && args[3].Equals("network", StringComparison.OrdinalIgnoreCase)
    ? NbtEncoding.NetworkLittleEndian
    : NbtEncoding.LittleEndian;

using var document = JsonDocument.Parse(File.ReadAllBytes(inputPath));
var root = document.RootElement;

var propertyDefinitions = new Dictionary<string, List<NbtTag>>(StringComparer.Ordinal);
foreach (var property in root.GetProperty("block_properties").EnumerateArray())
{
    var name = property.GetProperty("name").GetString() ?? string.Empty;
    var values = new List<NbtTag>();
    foreach (var value in property.GetProperty("values").EnumerateArray())
    {
        var raw = value.GetProperty("value");
        values.Add(raw.ValueKind switch
        {
            JsonValueKind.True => new NbtByte(1),
            JsonValueKind.False => new NbtByte(0),
            JsonValueKind.Number when raw.TryGetInt32(out var number) => new NbtInt(number),
            JsonValueKind.String => new NbtString(raw.GetString() ?? string.Empty),
            _ => new NbtString(raw.ToString()),
        });
    }

    propertyDefinitions[name] = values;
}

var entries = new List<BlockEntry>();
var missingProperties = new HashSet<string>(StringComparer.Ordinal);

foreach (var block in root.GetProperty("data_items").EnumerateArray())
{
    var blockName = block.GetProperty("name").GetString() ?? string.Empty;
    var properties = new List<(string Name, List<NbtTag> Values)>();

    var resolved = true;
    foreach (var property in block.GetProperty("properties").EnumerateArray())
    {
        var propertyName = property.GetProperty("name").GetString() ?? string.Empty;
        if (!propertyDefinitions.TryGetValue(propertyName, out var values))
        {
            var stripped = propertyName.StartsWith("minecraft:", StringComparison.Ordinal)
                ? propertyName["minecraft:".Length..]
                : "minecraft:" + propertyName;
            if (!propertyDefinitions.TryGetValue(stripped, out values))
            {
                missingProperties.Add(propertyName);
                resolved = false;
                break;
            }
        }

        properties.Add((propertyName, values));
    }

    if (!resolved)
    {
        continue;
    }

    foreach (var state in EnumerateStates(properties))
    {
        var hash = BlockStateHash.Compute(blockName, state, encoding);
        entries.Add(new BlockEntry(blockName, state, hash));
    }
}

var duplicateHashes = entries
    .GroupBy(entry => entry.Hash)
    .Where(group => group.Count() > 1)
    .ToList();

using (var stream = File.Create(outputPath))
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
{
    writer.WriteStartObject();
    writer.WriteNumber("protocol", protocol);
    writer.WriteString("source", Path.GetFileName(inputPath));
    writer.WriteString("encoding", encoding.ToString());
    writer.WriteString("generatedAt", DateTimeOffset.UtcNow.ToString("O"));
    writer.WriteNumber("stateCount", entries.Count);
    writer.WriteNumber("blockCount", entries.Select(entry => entry.Name).Distinct().Count());
    writer.WriteNumber("duplicateHashCount", duplicateHashes.Count);
    writer.WriteStartArray("blocks");
    foreach (var entry in entries)
    {
        writer.WriteStartObject();
        writer.WriteString("name", entry.Name);
        writer.WriteStartObject("states");
        foreach (var pair in entry.States.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteTag(writer, pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        writer.WriteNumber("hash", entry.Hash);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
}

Console.WriteLine($"blocks={entries.Select(entry => entry.Name).Distinct().Count()} states={entries.Count} duplicates={duplicateHashes.Count}");
if (missingProperties.Count > 0)
{
    Console.WriteLine("缺少属性定义: " + string.Join(", ", missingProperties.OrderBy(name => name)));
}

return 0;

static IEnumerable<Dictionary<string, NbtTag>> EnumerateStates(List<(string Name, List<NbtTag> Values)> properties)
{
    if (properties.Count == 0)
    {
        yield return new Dictionary<string, NbtTag>(StringComparer.Ordinal);
        yield break;
    }

    var current = new Dictionary<string, NbtTag>(StringComparer.Ordinal);
    foreach (var result in Recurse(properties, 0, current))
    {
        yield return result;
    }
}

static IEnumerable<Dictionary<string, NbtTag>> Recurse(
    List<(string Name, List<NbtTag> Values)> properties,
    int index,
    Dictionary<string, NbtTag> current)
{
    if (index == properties.Count)
    {
        yield return new Dictionary<string, NbtTag>(current, StringComparer.Ordinal);
        yield break;
    }

    var (name, values) = properties[index];
    foreach (var value in values)
    {
        current[name] = value;
        foreach (var result in Recurse(properties, index + 1, current))
        {
            yield return result;
        }
    }

    current.Remove(name);
}

static void WriteTag(Utf8JsonWriter writer, string name, NbtTag tag)
{
    writer.WritePropertyName(name);
    switch (tag)
    {
        case NbtByte value:
            writer.WriteBooleanValue(value.Value != 0);
            break;
        case NbtInt value:
            writer.WriteNumberValue(value.Value);
            break;
        case NbtString value:
            writer.WriteStringValue(value.Value);
            break;
        default:
            writer.WriteStringValue(tag.ToString());
            break;
    }
}

internal sealed record BlockEntry(string Name, Dictionary<string, NbtTag> States, uint Hash);
