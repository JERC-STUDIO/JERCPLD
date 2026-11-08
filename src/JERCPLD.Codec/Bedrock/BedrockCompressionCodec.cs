using System.IO.Compression;

namespace JERCPLD.Codec.Bedrock;

public enum BedrockCompression
{
    None = 0,

    /// <summary>Bedrock 默认的 zlib（带 zlib 头）压缩。</summary>
    Zlib = 1,

    /// <summary>Snappy 压缩；TODO(bedrock): 尚未实现，遇到时明确报错而不是静默丢包。</summary>
    Snappy = 2,
}

/// <summary>
/// Bedrock 批次压缩/解压。
/// TODO(bedrock): 压缩算法由 NetworkSettings 包协商，当前由上层显式传入；协商逻辑待补。
/// </summary>
public static class BedrockCompressionCodec
{
    public static byte[] Compress(ReadOnlySpan<byte> data, BedrockCompression algorithm)
    {
        if (algorithm == BedrockCompression.None)
        {
            return data.ToArray();
        }

        if (algorithm == BedrockCompression.Snappy)
        {
            throw new NotSupportedException("Snappy 压缩尚未实现（TODO: bedrock snappy）");
        }

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data, BedrockCompression algorithm)
    {
        if (algorithm == BedrockCompression.None)
        {
            return data.ToArray();
        }

        if (algorithm == BedrockCompression.Snappy)
        {
            throw new NotSupportedException("Snappy 解压尚未实现（TODO: bedrock snappy）");
        }

        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
