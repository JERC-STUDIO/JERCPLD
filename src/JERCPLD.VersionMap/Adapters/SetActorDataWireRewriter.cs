using JERCPLD.Codec.Bedrock.Metadata;
using JERCPLD.Codec.Binary;
using JERCPLD.VersionMap.Content;

namespace JERCPLD.VersionMap.Adapters;

/// <summary>
/// SetActorData (39) 的元数据重写：
///  - 解析 runtime ID 后的 SynchedActorDataList；
///  - 按目标版本槽位表重映射 / 补默认槽；
///  - 其余字段（Synched Properties、Tick）原样保留。
/// </summary>
public static class SetActorDataWireRewriter
{
    public static byte[] Rewrite(
        ReadOnlyMemory<byte> body,
        int from,
        int to,
        MetadataSlotMap? slotMap)
    {
        if (!StartGameWireRewriter.CanRewrite(from, to) || slotMap is null)
        {
            return body.ToArray();
        }

        try
        {
            var reader = new BinaryReaderEx(body);
            var writer = new BinaryWriterEx();

            CopyVarUInt(reader, writer); // Target Runtime ID (varuint64)

            var remaining = reader.PeekRemaining();
            if (!EntityMetadataCodec.TryRead(remaining.Span, out var entries, out var consumed))
            {
                return body.ToArray();
            }

            reader.ReadBytes(consumed);
            writer.WriteBytes(EntityMetadataCodec.Write(slotMap.Apply(entries)));
            writer.WriteBytes(reader.ReadRemaining().Span);
            return writer.ToArray();
        }
        catch (Exception)
        {
            return body.ToArray();
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
