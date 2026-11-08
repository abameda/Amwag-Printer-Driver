using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Tests.Hosting;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using Amwag.PrinterSetup.Windows.Security;

namespace Amwag.PrinterSetup.App.Tests.Elevation;

/// <summary>
/// The broker with a scripted worker in place of the elevated process, so no UAC prompt is ever shown. The script talks to the
/// broker over a real, local, short-lived named pipe (usually through the worker's own <see cref="WorkerChannel"/>).
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class ElevationBrokerTests
{
    private static readonly WorkerRequest Request =
        new(SetupMode.Setup, new TargetSelector("queue:AMWAG Ticket Printer", "P-4030DN"), AllowUnconfirmedDimensions: false, "0123456789abcdef");

    private static readonly ConfigurationPin Pin = new(new string('c', 64), ConfigurationPin.NoBranch);

    private readonly MemoryLog _log = new();
    private readonly ProgressCollector _progress = new();

    [Fact]
    public async Task A_declined_UAC_prompt_is_reported_as_declined()
    {
        var broker = Broker(_ => throw new Win32Exception(1223));

        ElevatedRunResult result = await broker.RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.Declined, result.Outcome);
        Assert.Null(result.Result);
        Assert.True(_log.Contains("elevation.declined"));
    }

    [Fact]
    public async Task Other_start_failures_are_reported_as_failed_to_start()
    {
        var broker = Broker(_ => throw new Win32Exception(2, "The system cannot find the file specified."));

        ElevatedRunResult result = await broker.RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.FailedToStart, result.Outcome);
        Assert.Contains("cannot find the file", result.Error);
    }

    [Fact]
    public async Task An_invalid_request_never_starts_the_worker()
    {
        bool started = false;
        var broker = Broker(_ =>
        {
            started = true;
            throw new InvalidOperationException();
        });

        ElevatedRunResult result = await broker.RunMachineStepsAsync(Request with { Mode = SetupMode.Diagnostics }, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.FailedToStart, result.Outcome);
        Assert.False(started);
    }

    [Fact]
    public async Task The_worker_is_started_elevated_with_arguments_it_accepts()
    {
        ProcessStartInfo? startInfo = null;
        var broker = new ElevationBroker(_log, configPath: null, "10.0.0.9", Pin, info =>
        {
            startInfo = info;
            throw new Win32Exception(1223);
        });

        await broker.RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.NotNull(startInfo);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        var options = CommandLineParser.Parse(WindowsCommandLine.Split(startInfo.Arguments), out ConfigurationPin? pin);
        Assert.Empty(options.Errors);
        Assert.Equal(AppRole.Worker, options.Role);
        Assert.StartsWith(WorkerArguments.PipeNamePrefix, options.PipeName);
        Assert.Equal(Environment.ProcessId, options.WindowProcessId);
        Assert.Equal(Request.SessionId, options.SessionId);
        Assert.Equal(Request.Target.Key, options.TargetKey);
        Assert.Equal("10.0.0.9", options.Address);
        Assert.Equal(Pin, pin);
    }

    [Fact]
    public async Task A_configuration_chosen_by_the_caller_is_never_handed_to_the_elevated_worker()
    {
        bool started = false;
        var broker = new ElevationBroker(_log, @"C:\Users\Public\other-config.json", null, Pin, _ =>
        {
            started = true;
            throw new Win32Exception(1223);
        });

        ElevatedRunResult result = await broker.RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.False(started);
        Assert.Equal(ElevationOutcome.FailedToStart, result.Outcome);
        Assert.Contains("as administrator", result.Error);
    }

    [Fact]
    public async Task Progress_and_result_are_relayed_from_the_worker()
    {
        ApplyResult expected = PipeProtocolTests.SampleResult();
        var worker = new ScriptedWorker(pipe =>
        {
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(10), NullLog.Instance);
            channel.Report(PipeProtocolTests.SampleProgress());
            channel.Report(PipeProtocolTests.SampleProgress() with { State = StepState.Succeeded, Phase = ProgressPhase.Completed });
            channel.SendResult(expected, ExitCodes.NotReady);
        }, ExitCodes.NotReady);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.Completed, result.Outcome);
        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.NotNull(result.Result);
        Assert.Equal(expected.Results.Count, result.Result.Results.Count);
        Assert.True(result.Result.RebootRequired);
        Assert.Equal(new[] { StepState.Running, StepState.Succeeded }, _progress.Updates.Select(u => u.State));
    }

    [Fact]
    public async Task Unreadable_lines_are_ignored()
    {
        var worker = new ScriptedWorker(pipe =>
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            client.Connect(10_000);
            client.Write("garbage\n{\"type\":\"command\",\"message\":\"format c:\"}\n"u8);
            client.Write(PipeProtocol.Encode(PipeMessage.ForResult(new ApplyResult(), ExitCodes.Ready)));
        }, ExitCodes.Ready);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.Completed, result.Outcome);
        Assert.Equal(2, _log.Find("elevation.message.invalid").Count);
    }

    [Fact]
    public async Task A_worker_that_ends_without_a_result_failed()
    {
        var worker = new ScriptedWorker(pipe =>
        {
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(10), NullLog.Instance);
            channel.Report(PipeProtocolTests.SampleProgress());
        }, ExitCodes.InternalError);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.WorkerFailed, result.Outcome);
        Assert.Equal("worker ended without a result (exit code 10)", result.Error);
        Assert.Single(_progress.Updates);
    }

    [Fact]
    public async Task A_worker_error_message_is_passed_on()
    {
        var worker = new ScriptedWorker(pipe =>
        {
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(10), NullLog.Instance);
            channel.SendError("The configuration is not valid.");
        }, ExitCodes.ConfigurationInvalid);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.WorkerFailed, result.Outcome);
        Assert.Equal(ExitCodes.ConfigurationInvalid, result.ExitCode);
        Assert.Equal("The configuration is not valid.", result.Error);
    }

    [Theory]
    [InlineData(ExitCodes.ElevationRequired, "it did not get administrator rights")]
    [InlineData(ExitCodes.InvalidArguments, "it rejected its command line")]
    [InlineData(ExitCodes.Cancelled, "it could not reach this window")]
    [InlineData(ExitCodes.InternalError, "it failed while starting")]
    public async Task A_worker_that_exits_without_connecting_changed_nothing_and_did_not_start(int exitCode, string why)
    {
        var process = new FakeWorkerProcess();
        process.Exit(exitCode);

        ElevatedRunResult result = await Broker(_ => process).RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        // Not WorkerFailed: the flow would report "not known whether this was applied" for a helper that never ran a step.
        Assert.Equal(ElevationOutcome.FailedToStart, result.Outcome);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Null(result.Result);
        Assert.Equal($"The elevated helper ended before it connected to this window ({why}, exit code {exitCode}); nothing was changed.", result.Error);
        Assert.True(_log.Contains("elevation.connect.failed"));
    }

    [Fact]
    public async Task Cancelling_after_the_worker_started_closes_the_pipe_and_waits_for_the_worker_to_stop()
    {
        using var cancellation = new CancellationTokenSource();
        bool windowGoneSeen = false;
        var worker = new ScriptedWorker(pipe =>
        {
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(10), NullLog.Instance, TimeSpan.FromMilliseconds(50));
            channel.Report(PipeProtocolTests.SampleProgress());
            cancellation.Cancel(); // The employee closes the window while the worker runs.
            windowGoneSeen = channel.WindowGone.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }, ExitCodes.Cancelled);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, cancellation.Token);

        Assert.True(windowGoneSeen, "The worker noticed the closed pipe.");
        Assert.True(worker.Process.HasExited, "The broker returned only after the worker had exited.");
        Assert.Equal(ElevationOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.True(_log.Contains("elevation.stopping"));
        Assert.True(_log.Contains("elevation.cancelled"));
        Assert.Equal(ProgressPhase.Waiting, _progress.Updates[^1].Phase);
    }

    [Fact]
    public async Task Cancelling_before_the_worker_connected_closes_the_pipe_so_the_worker_stops_without_changes()
    {
        using var cancellation = new CancellationTokenSource();
        bool windowGoneSeen = false;
        var worker = new ScriptedWorker(pipe =>
        {
            cancellation.Cancel(); // The employee closes the window while the worker is still starting.
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(1), NullLog.Instance, TimeSpan.FromMilliseconds(50));
            windowGoneSeen = channel.WindowGone.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }, ExitCodes.Cancelled);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, cancellation.Token);

        Assert.True(windowGoneSeen, "The worker found no window to report to.");
        Assert.True(worker.Process.HasExited);
        Assert.Equal(ElevationOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task A_worker_that_does_not_stop_is_waited_for_only_until_the_stop_timeout()
    {
        var process = new FakeWorkerProcess();
        using var cancellation = new CancellationTokenSource();
        var broker = new ElevationBroker(_log, null, null, Pin, _ =>
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            return process;
        }, stopTimeout: TimeSpan.FromMilliseconds(300));

        ElevatedRunResult result = await broker.RunMachineStepsAsync(Request, _progress, cancellation.Token);

        Assert.Equal(ElevationOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.False(process.HasExited);
        Assert.True(_log.Contains("elevation.stop.timeout"));
    }

    [Fact]
    public async Task A_result_read_before_the_run_was_cancelled_is_kept()
    {
        using var cancellation = new CancellationTokenSource();
        var worker = new ScriptedWorker(pipe =>
        {
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(10), NullLog.Instance, TimeSpan.FromMilliseconds(50));
            channel.SendResult(PipeProtocolTests.SampleResult(), ExitCodes.NotReady);
            cancellation.Cancel();
            channel.WindowGone.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }, ExitCodes.NotReady);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, cancellation.Token);

        Assert.Equal(ElevationOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.NotNull(result.Result);
        Assert.Equal(2, result.Result.Results.Count);
    }

    [Fact]
    public async Task A_client_other_than_the_worker_is_disconnected_and_the_worker_is_still_heard()
    {
        NamedPipeClientStream? intruder = null;
        int checks = 0;
        var worker = new ScriptedWorker(pipe =>
        {
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(10), NullLog.Instance);
            channel.SendResult(new ApplyResult(), ExitCodes.Ready);
        }, ExitCodes.Ready);
        var broker = new ElevationBroker(_log, null, null, Pin, info =>
        {
            // Another process opens the pipe first (pipe names can be listed); in this test it is this process again, so the
            // first client is reported as some other process.
            intruder = new NamedPipeClientStream(".", PipeName(info), PipeDirection.Out);
            intruder.Connect(10_000);
            return worker.Start(info);
        }, handle => Interlocked.Increment(ref checks) == 1 ? 4 : PipeClient.ProcessId(handle));

        ElevatedRunResult result = await broker.RunMachineStepsAsync(Request, _progress, CancellationToken.None);
        intruder?.Dispose();

        Assert.Equal(ElevationOutcome.Completed, result.Outcome);
        Assert.Equal(ExitCodes.Ready, result.ExitCode);
        Assert.Equal(2, checks);
        LogEntry foreign = Assert.Single(_log.Find("elevation.pipe.foreign_client"));
        Assert.Equal(4, foreign.Data!["clientPid"]);
    }

    [Fact]
    public async Task A_pipe_that_stays_open_after_the_worker_exited_does_not_hang_the_window()
    {
        NamedPipeClientStream? leaked = null;
        var worker = new ScriptedWorker(pipe =>
        {
            // The worker's end of the pipe outlives it (held by another process, for example).
            leaked = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            leaked.Connect(10_000);
            leaked.Write(PipeProtocol.Encode(PipeMessage.ForResult(new ApplyResult(), ExitCodes.Ready)));
        }, ExitCodes.Ready);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        leaked?.Dispose();

        Assert.Equal(ElevationOutcome.Completed, result.Outcome);
        Assert.True(_log.Contains("elevation.pipe.lingering"));
    }

    [Fact]
    public async Task Only_the_user_administrators_and_system_may_open_the_pipe()
    {
        AuthorizationRuleCollection? rules = null;
        var worker = new ScriptedWorker(pipe =>
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            client.Connect(10_000);
            rules = client.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            client.Write(PipeProtocol.Encode(PipeMessage.ForResult(new ApplyResult(), ExitCodes.Ready)));
        }, ExitCodes.Ready);

        await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.NotNull(rules);
        var grants = rules.Cast<PipeAccessRule>().ToDictionary(r => (SecurityIdentifier)r.IdentityReference, r => (r.AccessControlType, r.PipeAccessRights));
        using var identity = WindowsIdentity.GetCurrent();
        const PipeAccessRights connect = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        Assert.Equal(3, grants.Count);
        Assert.Equal((AccessControlType.Allow, PipeAccessRights.FullControl), grants[identity.User!]);
        Assert.Equal((AccessControlType.Allow, connect), grants[new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)]);
        Assert.Equal((AccessControlType.Allow, connect), grants[new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)]);
    }

    [Fact]
    public async Task Heartbeats_are_ignored_silently()
    {
        var worker = new ScriptedWorker(pipe =>
        {
            using var channel = WorkerChannel.Connect(pipe, Environment.ProcessId, TimeSpan.FromSeconds(10), NullLog.Instance, TimeSpan.FromMilliseconds(20));
            Thread.Sleep(300);
            channel.Report(PipeProtocolTests.SampleProgress());
            channel.SendResult(new ApplyResult(), ExitCodes.Ready);
        }, ExitCodes.Ready);

        ElevatedRunResult result = await Broker(worker.Start).RunMachineStepsAsync(Request, _progress, CancellationToken.None);

        Assert.Equal(ElevationOutcome.Completed, result.Outcome);
        Assert.Single(_progress.Updates);
        Assert.False(_log.Contains("elevation.message.invalid"));
    }

    [Fact]
    public void Without_a_window_to_talk_to_the_worker_channel_still_accepts_messages()
    {
        using var channel = WorkerChannel.Connect(WorkerArguments.PipeNamePrefix + new string('0', 32), Environment.ProcessId, TimeSpan.FromMilliseconds(200), _log);

        channel.Report(PipeProtocolTests.SampleProgress());
        channel.SendResult(new ApplyResult(), ExitCodes.Ready);

        Assert.True(_log.Contains("worker.pipe.unavailable"));
    }

    private ElevationBroker Broker(Func<ProcessStartInfo, IWorkerProcess> start) => new(_log, null, null, Pin, start);

    private static string PipeName(ProcessStartInfo startInfo) => CommandLineParser.Parse(WindowsCommandLine.Split(startInfo.Arguments)).PipeName!;

    private sealed class ProgressCollector : IProgress<ProgressUpdate>
    {
        private readonly List<ProgressUpdate> _updates = [];

        public IReadOnlyList<ProgressUpdate> Updates
        {
            get
            {
                lock (_updates)
                    return [.. _updates];
            }
        }

        public void Report(ProgressUpdate value)
        {
            lock (_updates)
                _updates.Add(value);
        }
    }

    private sealed class FakeWorkerProcess : IWorkerProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Scripted workers connect from this process, so the broker's real client check finds this id.
        public int Id => Environment.ProcessId;

        public bool HasExited => _exit.Task.IsCompleted;

        public int ExitCode => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : throw new InvalidOperationException("Still running.");

        public void Exit(int exitCode) => _exit.TrySetResult(exitCode);

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Plays the worker: reads the pipe name from the arguments, runs the script against it, then exits. It runs in this process,
    /// which is also the window that owns the pipe (the --window-pid the scripts pass to <see cref="WorkerChannel.Connect(string, int, TimeSpan, ILog)"/>).
    /// </summary>
    private sealed class ScriptedWorker(Action<string> script, int exitCode)
    {
        public FakeWorkerProcess Process { get; } = new();

        public IWorkerProcess Start(ProcessStartInfo startInfo)
        {
            var options = CommandLineParser.Parse(WindowsCommandLine.Split(startInfo.Arguments));
            Assert.Empty(options.Errors);
            Assert.Equal(Environment.ProcessId, options.WindowProcessId);
            _ = Task.Run(() =>
            {
                try
                {
                    script(options.PipeName!);
                }
                finally
                {
                    Process.Exit(exitCode);
                }
            });
            return Process;
        }
    }
}
