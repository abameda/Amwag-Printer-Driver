namespace Amwag.PrinterSetup.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>Structured log for IT troubleshooting. Implementations must be thread-safe and must never throw.</summary>
public interface ILog
{
    string SessionId { get; }

    /// <summary>File the entries are written to, when there is one.</summary>
    string? FilePath { get; }

    void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null) =>
        log.Write(LogLevel.Debug, eventName, message, data);

    public static void Info(this ILog log, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null) =>
        log.Write(LogLevel.Info, eventName, message, data);

    public static void Warn(this ILog log, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) =>
        log.Write(LogLevel.Warning, eventName, message, data, exception);

    public static void Error(this ILog log, string eventName, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? data = null) =>
        log.Write(LogLevel.Error, eventName, message, data, exception);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public string SessionId => "none";
    public string? FilePath => null;

    public void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null)
    {
    }
}
