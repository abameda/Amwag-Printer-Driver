using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Security;

namespace Amwag.PrinterSetup.App.Elevation;

/// <summary>
/// The worker's end of the pipe: output only, thread-safe, and never throws. The window's only stop signal is closing its end
/// of the pipe: the next write then fails and <see cref="WindowGone"/> is cancelled, so the worker stops at its next action
/// boundary. A heartbeat every <see cref="HeartbeatInterval"/> notices that even while an action blocks, and a write the window
/// does not read within <see cref="WriteTimeout"/> counts the same. Without the window from the start (no connection, or a
/// pipe server in another process than the window that started this worker) the token is cancelled at once, because nobody
/// could stop, watch or verify the run.
/// </summary>
internal sealed class WorkerChannel : IProgress<ProgressUpdate>, IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    /// <summary>The window reads all the time, so a write it has not read by then means it is hung or is not the window.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly ILog _log;
    private readonly TimeSpan _writeTimeout;
    private readonly CancellationTokenSource _windowGone = new();
    private readonly PeriodicTimer? _heartbeat;
    private Stream? _pipe;

    private WorkerChannel(Stream? pipe, ILog log, TimeSpan heartbeatInterval, TimeSpan writeTimeout)
    {
        _pipe = pipe;
        _log = log;
        _writeTimeout = writeTimeout;
        if (pipe is null)
        {
            _windowGone.Cancel();
            return;
        }

        _heartbeat = new PeriodicTimer(heartbeatInterval);
        _ = BeatAsync(_heartbeat);
    }

    /// <summary>Cancelled once the window is gone: it never connected, it is not the window, it stopped reading, or it closed its end of the pipe.</summary>
    public CancellationToken WindowGone => _windowGone.Token;

    /// <summary>Connects to <paramref name="pipeName"/>, accepting it only from a pipe server in <paramref name="windowProcessId"/>.</summary>
    public static WorkerChannel Connect(string pipeName, int windowProcessId, TimeSpan timeout, ILog log) =>
        Connect(pipeName, windowProcessId, timeout, log, HeartbeatInterval);

    /// <summary><see cref="Connect(string, int, TimeSpan, ILog)"/> with another heartbeat interval and write timeout (tests).</summary>
    internal static WorkerChannel Connect(string pipeName, int windowProcessId, TimeSpan timeout, ILog log, TimeSpan heartbeatInterval, TimeSpan? writeTimeout = null)
    {
        // Identification level: the window's (non-elevated) pipe server must not be able to impersonate this elevated process.
        // Asynchronous, so that a write the window does not read can be given up (see Send).
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            client.Connect(timeout);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            client.Dispose();
            log.Warn("worker.pipe.unavailable", "Could not connect to the window; nothing is changed without it.", exception: ex);
            return new WorkerChannel(null, log, heartbeatInterval, writeTimeout ?? WriteTimeout);
        }

        // The pipe name is free again once the window has closed its end (or given up waiting), so another process could own it
        // now: report only to the window that started this worker.
        int? server = PipeClient.ServerProcessId(client.SafePipeHandle);
        if (server != windowProcessId)
        {
            client.Dispose();
            log.Warn("worker.pipe.foreign_server", "The pipe is not served by the window that started this worker; nothing is changed without the window.",
                new Dictionary<string, object?> { ["serverPid"] = server, ["windowPid"] = windowProcessId });
            return new WorkerChannel(null, log, heartbeatInterval, writeTimeout ?? WriteTimeout);
        }

        log.Info("worker.pipe.connected", "Connected to the window.", new Dictionary<string, object?> { ["windowPid"] = windowProcessId });
        return new WorkerChannel(client, log, heartbeatInterval, writeTimeout ?? WriteTimeout);
    }

    public void Report(ProgressUpdate value) => Send(PipeMessage.ForProgress(value));

    public void SendResult(ApplyResult result, int exitCode) => Send(PipeMessage.ForResult(result, exitCode));

    public void SendError(string message) => Send(PipeMessage.ForError(message));

    public void Dispose()
    {
        lock (_gate)
        {
            _heartbeat?.Dispose();
            _pipe?.Dispose();
            _pipe = null;
        }

        _windowGone.Dispose();
    }

    // One beat at a time: a write completes once the window has read it.
    private async Task BeatAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync())
            Send(PipeMessage.ForHeartbeat());
    }

    private void Send(PipeMessage message)
    {
        byte[] line;
        try
        {
            line = PipeProtocol.Encode(message);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _log.Error("worker.pipe.encode", "A message for the window could not be serialized.", ex);
            return;
        }

        bool broken = false;
        lock (_gate)
        {
            if (_pipe is null)
                return;
            try
            {
                using var timeout = new CancellationTokenSource(_writeTimeout);
                _pipe.WriteAsync(line, timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex)
            {
                _log.Warn("worker.pipe.stalled", "The window did not read from the pipe in time; stopping after the step that is running.",
                    new Dictionary<string, object?> { ["timeoutSeconds"] = _writeTimeout.TotalSeconds }, ex);
                broken = true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Warn("worker.pipe.broken", "The window closed its end of the pipe; stopping after the step that is running.", exception: ex);
                broken = true;
            }

            if (broken)
            {
                _pipe.Dispose();
                _pipe = null;
            }
        }

        if (!broken)
            return;
        try
        {
            // Outside the lock: cancellation callbacks run on this thread.
            _windowGone.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A heartbeat that failed while the channel was being disposed: the worker is finishing anyway.
        }
    }
}
