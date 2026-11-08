using Avalonia.Media;
using JERCPLD.Core.Logging;

namespace JERCPLD.Desktop.Models;

public sealed class LogEntry
{
    public LogEntry(DateTime timestamp, LogLevel level, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }

    public DateTime Timestamp { get; }

    public LogLevel Level { get; }

    public string Message { get; }

    public string Text => $"{Timestamp:HH:mm:ss.fff}  {Level.ToString().ToUpperInvariant(),-5}  {Message}";

    public IBrush Brush => Level switch
    {
        LogLevel.Debug => Brushes.Gray,
        LogLevel.Warn => Brushes.Orange,
        LogLevel.Error => Brushes.IndianRed,
        _ => Brushes.Gainsboro,
    };
}
