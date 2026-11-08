using System.Text;
using JERCPLD.Core.Diagnostics;
using JERCPLD.VersionMap;

namespace JERCPLD.Proxy;

public sealed record PacketDiagnostic(
    DateTimeOffset Timestamp,
    string SessionId,
    PacketDirection Direction,
    int PacketId,
    string PacketName,
    int InputBytes,
    int OutputBytes,
    TranslationStatus Status,
    string Detail);

/// <summary>
/// 会话级包诊断记录。记录方向、包 ID、长度、是否被改写，导出为可读文本。
/// 只保留最近 <see cref="Capacity"/> 条，避免长时间运行占满内存。
/// </summary>
public sealed class ProxyDiagnostics
{
    public const int Capacity = 200_000;

    private readonly object _gate = new();
    private readonly List<PacketDiagnostic> _records = new();
    private int _dropped;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public int DroppedRecords
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    public void Record(PacketDiagnostic record)
    {
        lock (_gate)
        {
            if (_records.Count >= Capacity)
            {
                _dropped++;
                return;
            }

            _records.Add(record);
        }
    }

    public string Export(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<PacketDiagnostic> snapshot;
        int dropped;
        lock (_gate)
        {
            snapshot = _records.ToList();
            dropped = _dropped;
        }

        var builder = new StringBuilder(snapshot.Count * 80);
        builder.AppendLine("# JERCPLD packet diagnostics");
        builder.AppendLine($"# exported={DateTimeOffset.Now:O}");
        builder.AppendLine("# 字段: time\tsession\tdirection\tpacket_id\tname\tin_bytes\tout_bytes\tstatus\tdetail");
        foreach (var record in snapshot)
        {
            builder.Append(record.Timestamp.ToString("HH:mm:ss.fff")).Append('\t')
                .Append(record.SessionId).Append('\t')
                .Append(record.Direction).Append('\t')
                .Append(record.PacketId).Append('\t')
                .Append(record.PacketName).Append('\t')
                .Append(record.InputBytes).Append('\t')
                .Append(record.OutputBytes).Append('\t')
                .Append(record.Status).Append('\t')
                .Append(record.Detail.Replace('\t', ' ').Replace('\n', ' '))
                .AppendLine();
        }

        if (dropped > 0)
        {
            builder.AppendLine($"# warning: {dropped} records dropped due to capacity {Capacity}");
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
            _dropped = 0;
        }
    }
}
