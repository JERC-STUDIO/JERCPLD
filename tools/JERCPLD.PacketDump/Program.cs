using System.Text;
using System.Text.RegularExpressions;
using JERCPLD.Codec.Bedrock;
using JERCPLD.Codec.RakNet;
using JERCPLD.Protocol.Bedrock;
using JERCPLD.Protocol.RakNet;

namespace JERCPLD.PacketDump;

/// <summary>
/// 离线抓包解析工具：把十六进制抓包还原成 RakNet / Bedrock 的可读结构。
/// 用法：jercpld-dump "84 00 00 00 ..."   或   jercpld-dump capture.hex
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = ParseOptions(args, out var positionals);

        string text;
        var source = Get(options, "file") ?? positionals.FirstOrDefault();
        if (source is null)
        {
            if (!Console.IsInputRedirected)
            {
                PrintUsage();
                return 0;
            }

            text = Console.In.ReadToEnd();
        }
        else if (File.Exists(source))
        {
            text = File.ReadAllText(source);
        }
        else
        {
            text = source;
        }

        byte[] packet;
        try
        {
            packet = ParseHex(text);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"十六进制解析失败: {exception.Message}");
            return 1;
        }

        Console.WriteLine($"== 输入 {packet.Length} 字节 ==");
        PrintHexDump(packet);
        Console.WriteLine();
        Console.WriteLine("== 解析 ==");
        Decode(packet);
        return 0;
    }

    private static void Decode(byte[] packet)
    {
        if (packet.Length == 0)
        {
            Console.WriteLine("空输入");
            return;
        }

        var id = packet[0];

        try
        {
            if (id == RakNetConstants.GamePacketId)
            {
                DecodeGamePacket(packet);
                return;
            }

            if (id is RakNetMessageId.Ack or RakNetMessageId.Nack)
            {
                DecodeAck(packet);
                return;
            }

            switch (id)
            {
                case RakNetMessageId.UnconnectedPing:
                case RakNetMessageId.UnconnectedPingOpenConnections:
                    var ping = OfflineMessageCodec.DecodeUnconnectedPing(packet);
                    Console.WriteLine($"类型: UnconnectedPing (open={ping.OpenConnections})");
                    Console.WriteLine($"  time       = {ping.Time}");
                    Console.WriteLine($"  clientGuid = 0x{ping.ClientGuid:X16}");
                    return;

                case RakNetMessageId.UnconnectedPong:
                    var pong = OfflineMessageCodec.DecodeUnconnectedPong(packet);
                    Console.WriteLine("类型: UnconnectedPong");
                    Console.WriteLine($"  time       = {pong.Time}");
                    Console.WriteLine($"  serverGuid = 0x{pong.ServerGuid:X16}");
                    Console.WriteLine($"  motd       = {pong.Motd}");
                    var parsedMotd = ServerMotd.TryParse(pong.Motd);
                    if (parsedMotd is not null)
                    {
                        Console.WriteLine($"  解析       = protocol {parsedMotd.Protocol}, version {parsedMotd.Version}, players {parsedMotd.Players}/{parsedMotd.MaxPlayers}");
                    }

                    return;

                case RakNetMessageId.OpenConnectionRequest1:
                    var request1 = OfflineMessageCodec.DecodeOpenConnectionRequest1(packet);
                    Console.WriteLine("类型: OpenConnectionRequest1");
                    Console.WriteLine($"  raknetProtocol = {request1.RakNetProtocolVersion}");
                    Console.WriteLine($"  数据报长度     = {request1.DatagramLength} (MTU 探测)");
                    return;

                case RakNetMessageId.OpenConnectionRequest2:
                    var request2 = OfflineMessageCodec.DecodeOpenConnectionRequest2(packet);
                    Console.WriteLine("类型: OpenConnectionRequest2");
                    Console.WriteLine($"  serverAddress = {request2.ServerAddress}");
                    Console.WriteLine($"  mtu           = {request2.Mtu}");
                    Console.WriteLine($"  clientGuid    = 0x{request2.ClientGuid:X16}");
                    return;
            }

            if ((id & RakNetMessageId.DatagramValid) != 0)
            {
                DecodeDatagram(packet);
                return;
            }

            Console.WriteLine($"未识别的包 ID: 0x{id:X2}（骨架阶段未提供解码器）");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"解码失败: {exception.Message}");
        }
    }

    private static void DecodeDatagram(byte[] packet)
    {
        if (!RakNetDatagramCodec.TryRead(packet, out var datagram) || datagram is null)
        {
            Console.WriteLine("不是有效的 RakNet 数据报");
            return;
        }

        Console.WriteLine($"类型: RakNet 数据报 (header=0x{datagram.Header:X2}, packetNumber={datagram.PacketNumber}, frames={datagram.Frames.Count})");
        foreach (var frame in datagram.Frames)
        {
            Console.WriteLine($"  - reliability={frame.Reliability}, reliable={frame.ReliableIndex}, order={frame.OrderIndex}, channel={frame.OrderChannel}, payload={frame.Payload.Length} bytes");
            if (frame.IsSplit)
            {
                Console.WriteLine($"    split {frame.SplitIndex + 1}/{frame.SplitCount} id={frame.SplitId}");
            }

            if (!frame.Payload.IsEmpty)
            {
                Console.WriteLine($"    first=0x{frame.Payload.Span[0]:X2}, preview={Preview(frame.Payload.Span)}");
            }
        }
    }

    private static void DecodeAck(byte[] packet)
    {
        var ranges = RakNetDatagramCodec.ReadAckRanges(packet);
        Console.WriteLine($"类型: {(packet[0] == RakNetMessageId.Ack ? "ACK" : "NAK")} ({ranges.Count} 段)");
        foreach (var (start, end) in ranges)
        {
            Console.WriteLine(start == end ? $"  #{start}" : $"  #{start}-{end}");
        }
    }

    private static void DecodeGamePacket(byte[] packet)
    {
        if (!BedrockGamePacketCodec.TryUnwrap(packet, out var batch))
        {
            Console.WriteLine("0xFE 游戏包帧非法");
            return;
        }

        Console.WriteLine($"类型: Bedrock 游戏包帧, 批次载荷 {batch.Length} 字节");
        Console.WriteLine($"  批次预览: {Preview(batch.Span, 32)}");

        IReadOnlyList<BedrockPacketFrame>? frames = null;
        try
        {
            frames = BedrockBatchCodec.DecodeBatch(batch);
        }
        catch (Exception plainException)
        {
            try
            {
                var decompressed = BedrockCompressionCodec.Decompress(batch.Span, BedrockCompression.Zlib);
                frames = BedrockBatchCodec.DecodeBatch(decompressed);
                Console.WriteLine($"  按 zlib 解压成功: {batch.Length} -> {decompressed.Length} 字节");
            }
            catch (Exception zlibException)
            {
                Console.WriteLine($"  批次无法按明文解析: {plainException.Message}");
                Console.WriteLine($"  zlib 解压也失败: {zlibException.Message}");
                return;
            }
        }

        Console.WriteLine($"  批次内包数: {frames.Count}");
        foreach (var frame in frames)
        {
            Console.WriteLine($"  - {BedrockPacketIds.NameOf(frame.PacketId)} (0x{frame.PacketId:X}), body={frame.Body.Length} bytes");
            if (!frame.Body.IsEmpty)
            {
                Console.WriteLine($"    preview={Preview(frame.Body.Span, 24)}");
            }
        }
    }

    private static byte[] ParseHex(string text)
    {
        var withoutPrefix = Regex.Replace(text, "0[xX]", string.Empty);
        var builder = new StringBuilder(withoutPrefix.Length);
        foreach (var character in withoutPrefix)
        {
            if (Uri.IsHexDigit(character))
            {
                builder.Append(character);
            }
        }

        if (builder.Length == 0)
        {
            throw new FormatException("没有找到十六进制内容");
        }

        if (builder.Length % 2 != 0)
        {
            builder.Insert(0, '0');
        }

        return Convert.FromHexString(builder.ToString());
    }

    private static void PrintHexDump(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var length = Math.Min(16, data.Length - offset);
            var line = data.Slice(offset, length);
            var hex = new StringBuilder();
            var ascii = new StringBuilder();

            for (var i = 0; i < length; i++)
            {
                hex.Append(line[i].ToString("X2")).Append(' ');
                ascii.Append(line[i] is >= 0x20 and <= 0x7E ? (char)line[i] : '.');
            }

            Console.WriteLine($"{offset:X8}  {hex.ToString(),-48} |{ascii}|");
        }
    }

    private static string Preview(ReadOnlySpan<byte> data, int max = 16)
        => Convert.ToHexString(data[..Math.Min(max, data.Length)]) + (data.Length > max ? " ..." : string.Empty);

    private static Dictionary<string, string?> ParseOptions(string[] args, out List<string> positionals)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            var key = argument[2..];
            if (key.Length == 0)
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = null;
            }
        }

        return options;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) ? value : null;

    private static void PrintUsage()
    {
        Console.WriteLine("用法: jercpld-dump \"<十六进制>\" | jercpld-dump <文件> | jercpld-dump --file <文件>");
        Console.WriteLine("支持粘贴带空格/换行/0x 前缀的十六进制；也可以从标准输入读取。");
    }
}
