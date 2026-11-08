using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Elevation;

/// <summary>
/// The worker's end of the pipe against a real, local, short-lived pipe that the test plays the window on: the window's only
/// stop signal is closing its end, which the worker must notice even when it is not reporting anything; the worker talks only
/// to a pipe server in the window's process, and only while the window reads.
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class WorkerChannelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly MemoryLog _log = new();

    [Fact]
    public void Without_a_window_the_worker_is_stopped_from_the_start()
    {
        using var channel = WorkerChannel.Connect(NewPipeName(), Environment.ProcessId, TimeSpan.FromMilliseconds(200), _log);

        Assert.True(channel.WindowGone.IsCancellationRequested);
        Assert.True(_log.Contains("worker.pipe.unavailable"));
        channel.Report(PipeProtocolTests.SampleProgress()); // still accepted, never throws
    }

    [Fact]
    public async Task The_window_that_started_the_worker_is_reported_to()
    {
        string name = NewPipeName();
        using var window = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = window.WaitForConnectionAsync();
        using var channel = WorkerChannel.Connect(name, Environment.ProcessId, Wait, _log, heartbeatInterval: TimeSpan.FromHours(1));
        await connected;

        Task<string?> line = ReadLineAsync(window);
        channel.Report(PipeProtocolTests.SampleProgress());

        Assert.Equal(PipeMessageType.Progress, PipeProtocol.Decode((await line.WaitAsync(Wait))!)?.Type);
        Assert.False(channel.WindowGone.IsCancellationRequested);
        Assert.Equal(Environment.ProcessId, Assert.Single(_log.Find("worker.pipe.connected")).Data!["windowPid"]);
    }

    [Fact]
    public async Task A_pipe_server_in_another_process_than_the_window_gets_nothing_and_stops_the_worker()
    {
        // The worker was started by another window process: this process now owns the pipe name, for example because that
        // window freed it (stopped, or gave up waiting) and another program took it.
        string name = NewPipeName();
        int otherWindow = Environment.ProcessId + 4;
        using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = server.WaitForConnectionAsync();

        using var channel = WorkerChannel.Connect(name, otherWindow, Wait, _log, heartbeatInterval: TimeSpan.FromHours(1));
        await connected.WaitAsync(Wait);
        channel.Report(PipeProtocolTests.SampleProgress());
        channel.SendResult(new ApplyResult(), ExitCodes.Ready);

        Assert.True(channel.WindowGone.IsCancellationRequested, "a foreign pipe server must stop the worker before it changes anything");
        LogEntry foreign = Assert.Single(_log.Find("worker.pipe.foreign_server"));
        Assert.Equal(Environment.ProcessId, foreign.Data!["serverPid"]);
        Assert.Equal(otherWindow, foreign.Data["windowPid"]);
        Assert.False(_log.Contains("worker.pipe.connected"));
        Assert.Null(await ReadLineAsync(server).WaitAsync(Wait)); // the worker hung up without writing a byte
    }

    [Fact]
    public void A_pipe_server_owned_by_another_running_process_is_not_the_window()
    {
        string name = NewPipeName();
        using var other = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-Command",
                $"$s = New-Object System.IO.Pipes.NamedPipeServerStream('{name}', [System.IO.Pipes.PipeDirection]::In); $s.WaitForConnection(); Start-Sleep -Seconds 60",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            // This test process plays the window that started the worker; the pipe belongs to the PowerShell process.
            using var channel = WorkerChannel.Connect(name, Environment.ProcessId, TimeSpan.FromSeconds(60), _log, heartbeatInterval: TimeSpan.FromHours(1));

            Assert.True(channel.WindowGone.IsCancellationRequested);
            LogEntry foreign = Assert.Single(_log.Find("worker.pipe.foreign_server"));
            Assert.Equal(other.Id, foreign.Data!["serverPid"]);
            Assert.Equal(Environment.ProcessId, foreign.Data["windowPid"]);
        }
        finally
        {
            try
            {
                other.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }
    }

    [Fact]
    public async Task A_window_that_stops_reading_stops_the_worker_within_the_write_timeout()
    {
        string name = NewPipeName();
        // The window is hung: it holds its end of the pipe but never reads, so each write would wait forever.
        using var window = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = window.WaitForConnectionAsync();
        using var channel = WorkerChannel.Connect(name, Environment.ProcessId, Wait, _log, heartbeatInterval: TimeSpan.FromHours(1), writeTimeout: TimeSpan.FromMilliseconds(300));
        await connected;

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100_000 && !channel.WindowGone.IsCancellationRequested; i++)
            channel.Report(PipeProtocolTests.SampleProgress()); // fills whatever the pipe buffers, then one write times out

        Assert.True(channel.WindowGone.IsCancellationRequested, "a window that does not read must stop the worker");
        Assert.True(watch.Elapsed < Wait, $"stopping took {watch.Elapsed}");
        Assert.Equal(0.3, Assert.Single(_log.Find("worker.pipe.stalled")).Data!["timeoutSeconds"]);
        channel.SendResult(new ApplyResult(), ExitCodes.Ready); // the channel is closed now: accepted, not written, never throws
    }

    [Fact]
    public async Task A_write_after_the_window_closed_its_end_stops_the_worker()
    {
        string name = NewPipeName();
        var window = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = window.WaitForConnectionAsync();
        using var channel = WorkerChannel.Connect(name, Environment.ProcessId, Wait, _log, heartbeatInterval: TimeSpan.FromHours(1));
        await connected;
        Assert.False(channel.WindowGone.IsCancellationRequested);

        window.Dispose();
        channel.Report(PipeProtocolTests.SampleProgress());

        Assert.True(channel.WindowGone.IsCancellationRequested);
        Assert.True(_log.Contains("worker.pipe.broken"));
    }

    [Fact]
    public async Task The_heartbeat_notices_a_closed_window_while_the_worker_reports_nothing()
    {
        string name = NewPipeName();
        var window = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = window.WaitForConnectionAsync();
        using var channel = WorkerChannel.Connect(name, Environment.ProcessId, Wait, _log, heartbeatInterval: TimeSpan.FromMilliseconds(50));
        await connected;

        string? line = await ReadLineAsync(window).WaitAsync(Wait);
        Assert.Equal(PipeMessageType.Heartbeat, PipeProtocol.Decode(line!)?.Type);

        window.Dispose();

        Assert.True(channel.WindowGone.WaitHandle.WaitOne(Wait), "WindowGone was not cancelled after the window closed its end.");
    }

    [Fact]
    public void Heartbeats_round_trip_as_one_short_line()
    {
        string line = Encoding.UTF8.GetString(PipeProtocol.Encode(PipeMessage.ForHeartbeat()));

        Assert.Equal("{\"type\":\"heartbeat\"}\n", line);
        Assert.Equal(PipeMessageType.Heartbeat, PipeProtocol.Decode(line.TrimEnd('\n'))?.Type);
    }

    private static async Task<string?> ReadLineAsync(Stream pipe)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadLineAsync();
    }

    private static string NewPipeName() => WorkerArguments.PipeNamePrefix + RandomNumberGenerator.GetHexString(32, lowercase: true);
}
