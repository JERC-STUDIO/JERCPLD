namespace JERCPLD.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public interface ILog
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Log(LogLevel.Debug, message);

    public static void Info(this ILog log, string message) => log.Log(LogLevel.Info, message);

    public static void Warn(this ILog log, string message) => log.Log(LogLevel.Warn, message);

    public static void Error(this ILog log, string message, Exception? exception = null)
        => log.Log(LogLevel.Error, message, exception);
}

public sealed class ConsoleLog : ILog
{
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();

    public ConsoleLog(LogLevel minimumLevel = LogLevel.Info) => _minimumLevel = minimumLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        lock (_gate)
        {
            var levelText = level.ToString().ToUpperInvariant();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{levelText}] {message}");
            if (exception is not null)
            {
                Console.WriteLine(exception);
            }
        }
    }
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    private NullLog()
    {
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}
