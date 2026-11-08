using JERCPLD.Codec.Binary;
using JERCPLD.Codec.Nbt;
using JERCPLD.VersionMap.Content;

namespace JERCPLD.VersionMap.Adapters;

/// <summary>
/// LevelChunk (58) 的 2193 ↔ 2225 信封改写：
/// 2225 在包体末尾新增 boolean Is Client Biome Update；
/// 内部 Serialized Chunk Data 会按 SubChunkCount 逐个子区块重写调色板。
/// </summary>
public static class LevelChunkWireRewriter
{
    public static byte[] Rewrite(
        ReadOnlyMemory<byte> body,
        int from,
        int to,
        ContentFallbackMap? fallback = null)
    {
        try
        {
            return RewriteParsed(body.ToArray(), from, to, fallback);
        }
        catch (Exception)
        {
            return RewriteEnvelopeOnly(body, from, to);
        }
    }

    /// <summary>解析 LevelChunk 信封，重写内部 Serialized Chunk Data 里的子区块调色板。</summary>
    private static byte[] RewriteParsed(byte[] body, int from, int to, ContentFallbackMap? fallback)
    {
        var reader = new BinaryReaderEx(body);
        var writer = new BinaryWriterEx();

        CopyVarUInt(reader, writer); // Chunk X
        CopyVarUInt(reader, writer); // Chunk Z
        CopyVarUInt(reader, writer); // Dimension

        var subChunkCount = reader.ReadVarUInt();
        writer.WriteVarUInt(subChunkCount);

        var hasRequestLimit = reader.ReadByte() != 0;
        writer.WriteByte(hasRequestLimit ? (byte)1 : (byte)0);
        if (hasRequestLimit)
        {
            CopyVarUInt(reader, writer);
        }

        var cacheEnabled = reader.ReadByte() != 0;
        writer.WriteByte(cacheEnabled ? (byte)1 : (byte)0);
        if (cacheEnabled)
        {
            var blobCount = reader.ReadVarUInt();
            writer.WriteVarUInt(blobCount);
            writer.WriteBytes(reader.ReadBytes((int)blobCount * 8).Span);
        }

        var payloadLength = reader.ReadVarUInt();
        var payload = reader.ReadBytes((int)payloadLength).ToArray();
        var rewrittenPayload = subChunkCount == uint.MaxValue || fallback is null
            ? payload
            : RewritePayload(payload, subChunkCount, fallback);

        writer.WriteVarUInt((uint)rewrittenPayload.Length);
        writer.WriteBytes(rewrittenPayload);

        if (from == 2225)
        {
            _ = reader.ReadByte(); // 丢弃 Is Client Biome Update
        }

        if (to == 2225)
        {
            writer.WriteByte(0); // 追加 Is Client Biome Update
        }

        if (reader.Remaining != 0)
        {
            return RewriteEnvelopeOnly(body, from, to);
        }

        return writer.ToArray();
    }

    private static byte[] RewritePayload(byte[] payload, uint subChunkCount, ContentFallbackMap fallback)
    {
        var reader = new BinaryReaderEx(payload);
        var writer = new BinaryWriterEx();

        for (var i = 0; i < subChunkCount; i++)
        {
            var remaining = reader.PeekRemaining();
            if (!SubChunkTerrainRewriter.TryRewrite(remaining.Span, fallback, out var rewritten, out var consumed))
            {
                return payload;
            }

            writer.WriteBytes(rewritten);
            reader.ReadBytes(consumed);
        }

        writer.WriteBytes(reader.ReadRemaining().Span);
        return writer.ToArray();
    }

    private static byte[] RewriteEnvelopeOnly(ReadOnlyMemory<byte> body, int from, int to)
    {
        if (from == 2225 && to == 2193)
        {
            return body.Length == 0 ? body.ToArray() : body[..^1].ToArray();
        }

        if (from == 2193 && to == 2225)
        {
            var copy = new byte[body.Length + 1];
            body.Span.CopyTo(copy);
            copy[^1] = 0;
            return copy;
        }

        return body.ToArray();
    }

    private static void CopyVarUInt(BinaryReaderEx reader, BinaryWriterEx writer)
    {
        for (var i = 0; i < 10; i++)
        {
            var value = reader.ReadByte();
            writer.WriteByte(value);
            if ((value & 0x80) == 0)
            {
                return;
            }
        }

        throw new InvalidDataException("varint 超过 10 字节");
    }
}

/// <summary>
/// SubChunk (174) 的 2193 ↔ 2225 改写：
///  - SubChunk Request Result 枚举整体 +1；
///  - Serialized Sub Chunk 的调色板名称按内容降级表改写。
/// </summary>
public static class SubChunkWireRewriter
{
    private const int HeightmapBytes = 16 * 16;

    public static byte[] Rewrite(
        ReadOnlyMemory<byte> body,
        int from,
        int to,
        ContentFallbackMap? fallback)
    {
        if (!StartGameWireRewriter.CanRewrite(from, to))
        {
            return body.ToArray();
        }

        try
        {
            var reader = new BinaryReaderEx(body);
            var writer = new BinaryWriterEx();

            writer.WriteByte(reader.ReadByte()); // Cache Enabled
            CopyVarUInt(reader, writer); // Dimension Type
            writer.WriteBytes(reader.ReadBytes(12).Span); // Center Pos: 3 × int32 LE

            var count = reader.ReadVarUInt();
            writer.WriteVarUInt(count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteBytes(reader.ReadBytes(3).Span); // SubChunk Pos Offset

                var result = reader.ReadByte();
                writer.WriteByte(MapResult(result, from, to));

                var hasTerrain = reader.ReadByte() != 0;
                writer.WriteByte(hasTerrain ? (byte)1 : (byte)0);
                if (hasTerrain)
                {
                    var terrainLength = reader.ReadVarUInt();
                    var terrain = reader.ReadBytes((int)terrainLength).ToArray();
                    var rewritten = fallback is null
                        ? terrain
                        : SubChunkTerrainRewriter.Rewrite(terrain, fallback);
                    writer.WriteVarUInt((uint)rewritten.Length);
                    writer.WriteBytes(rewritten);
                }

                CopyHeightmap(reader, writer); // Height Map Data
                CopyHeightmap(reader, writer); // Render Height Map Data

                var hasBlob = reader.ReadByte() != 0;
                writer.WriteByte(hasBlob ? (byte)1 : (byte)0);
                if (hasBlob)
                {
                    writer.WriteBytes(reader.ReadBytes(8).Span);
                }
            }

            if (reader.Remaining != 0)
            {
                // 解析不完整时保留原包，避免把未知字段截断。
                return body.ToArray();
            }

            return writer.ToArray();
        }
        catch (Exception)
        {
            return body.ToArray();
        }
    }

    private static byte MapResult(byte result, int from, int to)
    {
        if (from == 2193 && to == 2225)
        {
            return result switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                3 => 4,
                4 => 5,
                5 => 6,
                _ => result,
            };
        }

        return result switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            4 => 3,
            5 => 4,
            6 => 5,
            _ => result,
        };
    }

    private static void CopyHeightmap(BinaryReaderEx reader, BinaryWriterEx writer)
    {
        var type = reader.ReadByte();
        writer.WriteByte(type);
        if (type == 1)
        {
            writer.WriteBytes(reader.ReadBytes(HeightmapBytes).Span);
        }
    }

    private static void CopyVarUInt(BinaryReaderEx reader, BinaryWriterEx writer)
    {
        for (var i = 0; i < 10; i++)
        {
            var value = reader.ReadByte();
            writer.WriteByte(value);
            if ((value & 0x80) == 0)
            {
                return;
            }
        }

        throw new InvalidDataException("varint 超过 10 字节");
    }
}

/// <summary>
/// SubChunk 地形调色板改写（版本 8/9）。
/// 只改写调色板 NBT 里的方块名称，保留状态与索引；索引数量不变，因此不需要重排方块索引。
/// </summary>
public static class SubChunkTerrainRewriter
{
    public static byte[] Rewrite(ReadOnlySpan<byte> terrain, ContentFallbackMap fallback)
        => TryRewrite(terrain, fallback, out var rewritten, out _) ? rewritten : terrain.ToArray();

    /// <summary>解析并改写一个完整的地形 blob；consumed 为输入中实际使用的字节数。</summary>
    public static bool TryRewrite(
        ReadOnlySpan<byte> terrain,
        ContentFallbackMap fallback,
        out byte[] rewritten,
        out int consumed)
    {
        rewritten = Array.Empty<byte>();
        consumed = 0;

        try
        {
            var reader = new BinaryReaderEx(terrain.ToArray());
            var version = reader.ReadByte();
            if (version is not (8 or 9))
            {
                return false;
            }

            var writer = new BinaryWriterEx();
            writer.WriteByte(version);
            var layerCount = reader.ReadByte();
            writer.WriteByte(layerCount);
            if (version == 9)
            {
                writer.WriteByte(reader.ReadByte()); // Y index
            }

            for (var layer = 0; layer < layerCount; layer++)
            {
                var header = reader.ReadByte();
                writer.WriteByte(header);
                var bits = header >> 1;
                var paletteType = header & 1;
                if (bits is not (1 or 2 or 3 or 4 or 5 or 6 or 8 or 16))
                {
                    return false;
                }

                var indicesPerBlock = 32 / bits;
                var wordCount = (4096 + indicesPerBlock - 1) / indicesPerBlock;
                writer.WriteBytes(reader.ReadBytes(wordCount * 4).Span);

                var paletteCount = paletteType == 0
                    ? (int)reader.ReadUInt32LE()
                    : reader.ReadVarInt();
                if (paletteType == 0)
                {
                    writer.WriteUInt32LE((uint)paletteCount);
                }
                else
                {
                    writer.WriteVarInt(paletteCount);
                }

                for (var entry = 0; entry < paletteCount; entry++)
                {
                    var remaining = reader.PeekRemaining();
                    if (!NbtReader.TryReadRoot(remaining.Span, out var tag, out var nbtConsumed,
                            NbtEncoding.LittleEndian))
                    {
                        return false;
                    }

                    var entryBytes = RewritePaletteEntry(tag, fallback);
                    if (entryBytes is null)
                    {
                        writer.WriteBytes(remaining.Span[..nbtConsumed]);
                    }
                    else
                    {
                        writer.WriteBytes(entryBytes);
                    }

                    reader.ReadBytes(nbtConsumed);
                }
            }

            rewritten = writer.ToArray();
            consumed = reader.Position;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[]? RewritePaletteEntry(NbtTag tag, ContentFallbackMap fallback)
    {
        if (tag is not NbtCompound compound || compound["name"] is not NbtString name)
        {
            return null;
        }

        var mapped = fallback.MapBlockType(name.Value);
        if (string.Equals(mapped, name.Value, StringComparison.Ordinal))
        {
            return null;
        }

        // 保守策略：目标方块的状态集合未知时清空 states，避免把源方块独有的状态
        // （例如 disc_press 的 cardinal_direction）带给不认识的方块。
        // TODO(chunk): 接入目标 BlockStateTable 后，可按目标状态键过滤并保留可识别状态。
        var rewritten = new NbtCompound();
        foreach (var entry in compound.Entries)
        {
            rewritten.Add(entry.Key, entry.Key switch
            {
                "name" => new NbtString(mapped),
                "states" => new NbtCompound(),
                _ => entry.Value,
            });
        }

        return NbtWriter.Write(rewritten, string.Empty, NbtEncoding.LittleEndian);
    }
}
