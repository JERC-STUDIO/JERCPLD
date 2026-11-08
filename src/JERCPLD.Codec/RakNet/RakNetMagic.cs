using JERCPLD.Codec.Binary;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.Codec.RakNet;

public static class RakNetMagic
{
    public static void Write(BinaryWriterEx writer) => writer.WriteBytes(RakNetConstants.Magic);

    public static void ReadAndValidate(BinaryReaderEx reader)
    {
        var actual = reader.ReadBytes(RakNetConstants.MagicLength).Span;
        if (!actual.SequenceEqual(RakNetConstants.Magic))
        {
            throw new InvalidDataException("RakNet magic 不匹配，可能不是 RakNet 流量");
        }
    }
}
