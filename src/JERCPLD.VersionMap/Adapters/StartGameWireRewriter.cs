using JERCPLD.Codec.Binary;

namespace JERCPLD.VersionMap.Adapters;

/// <summary>
/// 2193 (26.50) ↔ 2225 (26.60) 的 StartGame 包增量改写。
///
/// 已知差异（来源：Mojang 官方协议文档 1.26.50 / 1.26.60）：
///  - LevelSettings 在 Is Exported From Editor 之后新增 int8 Editor Level Migration Version；
///  - GameType 枚举值整体重排（Survival 1→0、Creative 2→1、Adventure 3→2、Default 4→5、Spectator 5→6）。
///
/// 只解析到插入点为止，剩余字节原样复制，避免依赖后续尚未建模的类型。
/// </summary>
public static class StartGameWireRewriter
{
    public const int Protocol2193 = 2193;
    public const int Protocol2225 = 2225;

    public static bool CanRewrite(int from, int to)
        => (from == Protocol2193 && to == Protocol2225) || (from == Protocol2225 && to == Protocol2193);

    public static byte[] Rewrite(ReadOnlyMemory<byte> body, int from, int to)
    {
        if (!CanRewrite(from, to))
        {
            return body.ToArray();
        }

        var upgrade = from == Protocol2193;
        var reader = new BinaryReaderEx(body);
        var writer = new BinaryWriterEx();

        // ActorUniqueID / ActorRuntimeID：varint64，原样复制。
        CopyVarUInt(reader, writer);
        CopyVarUInt(reader, writer);

        // StartGame 顶层 Game Type（zigzag varint32）。
        writer.WriteVarInt(MapGameType(reader.ReadVarInt(), upgrade));

        // Position(Vec3) + Rotation(Vec2)：5 个 little-endian float。
        for (var i = 0; i < 5; i++)
        {
            writer.WriteSingleLE(reader.ReadSingleLE());
        }

        // LevelSettings
        writer.WriteUInt64LE(reader.ReadUInt64LE()); // Seed
        writer.WriteBytes(reader.ReadBytes(2).Span); // Spawn Biome Type (int16 LE)
        CopyString(reader, writer); // User Defined Biome Name
        CopyVarUInt(reader, writer); // Dimension
        CopyVarUInt(reader, writer); // Generator Type
        writer.WriteVarInt(MapGameType(reader.ReadVarInt(), upgrade)); // Game Type
        writer.WriteByte(reader.ReadByte()); // Is Hardcore
        CopyVarUInt(reader, writer); // Game Difficulty
        CopyVarUInt(reader, writer); // BlockPos.X
        CopyVarUInt(reader, writer); // BlockPos.Y
        CopyVarUInt(reader, writer); // BlockPos.Z
        writer.WriteByte(reader.ReadByte()); // Achievements Disabled
        CopyVarUInt(reader, writer); // Editor World Type
        writer.WriteByte(reader.ReadByte()); // Is Created In Editor
        writer.WriteByte(reader.ReadByte()); // Is Exported From Editor

        if (upgrade)
        {
            writer.WriteByte(0); // Editor Level Migration Version = Legacy
        }
        else
        {
            _ = reader.ReadByte(); // 丢弃 2225 的 Editor Level Migration Version
        }

        // 其余字段两个版本一致，原样复制。
        writer.WriteBytes(reader.ReadRemaining().Span);
        return writer.ToArray();
    }

    private static int MapGameType(int value, bool upgrade)
    {
        if (upgrade)
        {
            // 2193 -> 2225
            return value switch
            {
                0 => -1,
                1 => 0,
                2 => 1,
                3 => 2,
                4 => 5,
                5 => 6,
                6 => 0,
                _ => value,
            };
        }

        // 2225 -> 2193
        return value switch
        {
            -1 => 0,
            0 => 1,
            1 => 2,
            2 => 3,
            5 => 4,
            6 => 5,
            _ => value,
        };
    }

    private static void CopyVarUInt(BinaryReaderEx reader, BinaryWriterEx writer)
    {
        for (var i = 0; i < 10; i++)
        {
            var current = reader.ReadByte();
            writer.WriteByte(current);
            if ((current & 0x80) == 0)
            {
                return;
            }
        }

        throw new InvalidDataException("varint 超过 10 字节");
    }

    private static void CopyString(BinaryReaderEx reader, BinaryWriterEx writer)
    {
        var length = reader.ReadVarUInt();
        if (length > int.MaxValue)
        {
            throw new InvalidDataException($"字符串长度非法: {length}");
        }

        writer.WriteVarUInt(length);
        writer.WriteBytes(reader.ReadBytes((int)length).Span);
    }
}
