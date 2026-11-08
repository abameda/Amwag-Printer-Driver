using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>
/// Structured log for IT: one JSON object per line ({ts, level, session, role, pid, event, message, data, exception}) in
/// %ProgramData%\AMWAG\PrinterSetup\Logs\&lt;yyyyMMdd-HHmmss&gt;-&lt;role&gt;-&lt;session&gt;-&lt;pid&gt;.jsonl, falling back to
/// %LOCALAPPDATA%\AMWAG\PrinterSetup\Logs. Thread-safe, written through on every entry, never throws.
/// </summary>
internal sealed partial class JsonLinesLog : ILog, IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(60);
    private static readonly byte[] LineEnd = "\r\n"u8.ToArray();

    private readonly Lock _gate = new();
    private readonly string _role;
    private readonly int _processId = Environment.ProcessId;
    private FileStream? _stream;

    private JsonLinesLog(string role, string sessionId, IEnumerable<(string BaseDirectory, string Directory)> locations)
    {
        _role = role;
        SessionId = sessionId;
        foreach (var (baseDirectory, directory) in locations)
        {
            if (TryOpen(baseDirectory, directory))
                return;
        }
    }

    /// <summary>Writes into exactly <paramref name="directory"/> (tests).</summary>
    internal JsonLinesLog(string directory, string role, string sessionId)
        : this(role, sessionId, [(Path.GetDirectoryName(Path.GetFullPath(directory)) ?? directory, directory)])
    {
    }

    public string SessionId { get; }

    public string? FilePath { get; private set; }

    /// <summary>Opens the log for this process; without any writable location it still works, writing nothing.</summary>
    public static JsonLinesLog Open(string role, string sessionId) => new(role, sessionId, AppPaths.LogLocations);

    public void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null)
    {
        try
        {
            byte[] line = Format(DateTimeOffset.Now, level, eventName, message, data, exception);
            lock (_gate)
            {
                if (_stream is null)
                    return;
                _stream.Write(line);
                _stream.Flush();
            }
        }
        catch (Exception)
        {
            // Logging must never take the application down (disk full, file deleted, unserializable data ...).
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Dispose();
            }
            catch (Exception)
            {
                // Best effort, like every other log operation.
            }

            _stream = null;
        }
    }

    private bool TryOpen(string baseDirectory, string directory)
    {
        if (!AppPaths.TryPrepareDirectory(baseDirectory, directory))
            return false;

        DeleteExpiredLogs(directory);
        string name = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyyMMdd-HHmmss}-{_role}-{SessionId}-{_processId}.jsonl");
        string path = Path.Combine(directory, name);
        try
        {
            // CreateNew never follows or overwrites an existing entry; BufferSize 0 writes every entry straight through.
            _stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 0,
            });
            FilePath = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteExpiredLogs(string directory)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - Retention;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jsonl"))
            {
                if (!OwnLogName().IsMatch(file.Name) || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LastWriteTimeUtc >= cutoff)
                    continue;
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another account's log or a file in use: leave it for a later run.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort.
        }
    }

    private byte[] Format(DateTimeOffset timestamp, LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data, Exception? exception)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("ts", timestamp.ToString("o", CultureInfo.InvariantCulture));
            json.WriteString("level", LevelName(level));
            json.WriteString("session", SessionId);
            json.WriteString("role", _role);
            json.WriteNumber("pid", _processId);
            json.WriteString("event", eventName);
            json.WriteString("message", message);
            if (data is { Count: > 0 })
            {
                json.WriteStartObject("data");
                foreach (var (key, value) in data)
                {
                    json.WritePropertyName(key);
                    WriteValue(json, value);
                }
                json.WriteEndObject();
            }
            if (exception is not null)
            {
                json.WritePropertyName("exception");
                WriteException(json, exception);
            }
            json.WriteEndObject();
        }

        buffer.Write(LineEnd);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                return;
            case string text:
                json.WriteStringValue(text);
                return;
        }

        JsonElement element;
        try
        {
            // Serialized to an element first so a failure (cycles, unsupported types) cannot leave a half-written entry.
            element = JsonSerializer.SerializeToElement(value, value.GetType(), ConfigJson.Options);
        }
        catch (Exception)
        {
            json.WriteStringValue(SafeToString(value));
            return;
        }

        element.WriteTo(json);
    }

    private static void WriteException(Utf8JsonWriter json, Exception exception)
    {
        json.WriteStartObject();
        json.WriteString("type", exception.GetType().FullName);
        json.WriteString("message", exception.Message);
        json.WriteNumber("hresult", exception.HResult);
        switch (exception)
        {
            case PrinterSetupException setup:
                json.WritePropertyName("code");
                WriteValue(json, setup.Code);
                if (setup.Win32Error is int setupError)
                    json.WriteNumber("win32Error", setupError);
                break;
            case Win32Exception win32:
                json.WriteNumber("win32Error", win32.NativeErrorCode);
                break;
        }
        json.WriteString("detail", SafeToString(exception));
        json.WriteEndObject();
    }

    private static string SafeToString(object value)
    {
        try
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        catch (Exception)
        {
            return value.GetType().FullName ?? "?";
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        _ => level.ToString().ToLowerInvariant(),
    };

    [GeneratedRegex(@"^\d{8}-\d{6}-(?:interactive|silent|worker)-[0-9a-f]{12,32}-\d{1,10}\.jsonl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnLogName();
}
