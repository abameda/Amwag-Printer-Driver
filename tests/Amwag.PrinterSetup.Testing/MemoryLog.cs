using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.Testing;

public sealed record LogEntry(LogLevel Level, string EventName, string Message, IReadOnlyDictionary<string, object?>? Data, Exception? Exception);

/// <summary>Thread-safe in-memory <see cref="ILog"/> for assertions on structured events.</summary>
public sealed class MemoryLog : ILog
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];

    public MemoryLog(string sessionId = "test-session") => SessionId = sessionId;

    public string SessionId { get; }

    public string? FilePath => null;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null)
    {
        lock (_gate)
            _entries.Add(new LogEntry(level, eventName, message, data, exception));
    }

    public bool Contains(string eventName) => Entries.Any(e => e.EventName == eventName);

    public IReadOnlyList<LogEntry> Find(string eventName) => Entries.Where(e => e.EventName == eventName).ToList();
}
