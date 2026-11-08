using Avalonia.Threading;
using JERCPLD.Core.Logging;
using JERCPLD.Desktop.Models;

namespace JERCPLD.Desktop.Services;

/// <summary>把代理日志转发到 UI 线程的日志面板。</summary>
public sealed class UiLog : ILog
{
    public event Action<LogEntry>? EntryAdded;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
        var entry = new LogEntry(DateTime.Now, level, text);
        Dispatcher.UIThread.Post(() => EntryAdded?.Invoke(entry));
    }
}
