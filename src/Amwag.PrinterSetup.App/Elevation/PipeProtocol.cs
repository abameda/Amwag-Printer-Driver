using System.Buffers;
using System.Text.Json;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Elevation;

internal enum PipeMessageType { Progress, Result, Error, Heartbeat }

/// <summary>
/// One line the elevated worker sends to the window (the pipe is one-way: the worker accepts no commands):
/// {"type":"progress","progress":...}, {"type":"result","result":...,"exitCode":n}, {"type":"error","message":"..."} or
/// {"type":"heartbeat"}. Heartbeats carry nothing: the worker writes one every few seconds so that it notices a closed window
/// (the write fails) even while an action blocks; the window ignores them.
/// </summary>
internal sealed record PipeMessage(PipeMessageType Type)
{
    public ProgressUpdate? Progress { get; init; }
    public ApplyResult? Result { get; init; }
    public int? ExitCode { get; init; }
    public string? Message { get; init; }

    public static PipeMessage ForProgress(ProgressUpdate progress) => new(PipeMessageType.Progress) { Progress = progress };

    public static PipeMessage ForResult(ApplyResult result, int exitCode) => new(PipeMessageType.Result) { Result = result, ExitCode = exitCode };

    public static PipeMessage ForError(string message) => new(PipeMessageType.Error) { Message = message };

    public static PipeMessage ForHeartbeat() => new(PipeMessageType.Heartbeat);
}

/// <summary>JSON Lines over the worker pipe, using <see cref="ConfigJson.Options"/>.</summary>
internal static class PipeProtocol
{
    private static readonly byte[] LineEnd = "\n"u8.ToArray();

    /// <summary>The message as one UTF-8 line terminated by '\n'.</summary>
    public static byte[] Encode(PipeMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        // The writer's own (non-indented) options win over ConfigJson's WriteIndented, which keeps each message on one line.
        using (var writer = new Utf8JsonWriter(buffer))
            JsonSerializer.Serialize(writer, message, ConfigJson.Options);
        buffer.Write(LineEnd);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The message on <paramref name="line"/>, or null when it is malformed or incomplete.</summary>
    public static PipeMessage? Decode(string line)
    {
        PipeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<PipeMessage>(line, ConfigJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }

        return message?.Type switch
        {
            PipeMessageType.Progress when message.Progress is not null => message,
            PipeMessageType.Result when message.Result is not null => message,
            PipeMessageType.Error when message.Message is not null => message,
            PipeMessageType.Heartbeat => message,
            _ => null,
        };
    }
}
