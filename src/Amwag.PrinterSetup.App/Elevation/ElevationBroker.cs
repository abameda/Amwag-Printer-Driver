using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Security;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.App.Elevation;

/// <summary>A started worker process; the seam that lets tests exercise the broker without a UAC prompt.</summary>
internal interface IWorkerProcess : IDisposable
{
    int Id { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs machine-wide steps in an elevated copy of this executable (one UAC prompt) and relays its progress from a one-way
/// named pipe that only the started worker may use; the worker in turn reports only to a pipe server in this process (its
/// --window-pid), and one that ends before it connected changed nothing. Once the worker has started, cancelling never
/// abandons it: the broker closes its end of the pipe (the worker's stop signal), waits for the worker to stop at its next
/// action boundary and returns <see cref="ElevationOutcome.Cancelled"/>, so the flow can re-read a machine that no longer
/// changes. The worker's result is informational only: the flow re-verifies the system from this process.
/// </summary>
internal sealed class ElevationBroker : IElevationBroker
{
    /// <summary>
    /// How long a cancelled run waits for the worker to stop. The worker notices the closed pipe within a heartbeat and gives
    /// up waiting for the apply lock at once, but finishes an action that is already running (a driver installation, say).
    /// </summary>
    internal static readonly TimeSpan StopTimeout = MachineApplyLock.DefaultTimeout + TimeSpan.FromMinutes(1);

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the UAC prompt was declined.

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);
    // After the worker exited, the pipe is read this much longer for what it wrote last (each write completes once read).
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ILog _log;
    private readonly string? _configPath;
    private readonly string? _address;
    private readonly ConfigurationPin _pin;
    private readonly Func<ProcessStartInfo, IWorkerProcess> _start;
    private readonly Func<SafePipeHandle, int?> _clientProcessId;
    private readonly TimeSpan _stopTimeout;

    /// <param name="log">The window's log.</param>
    /// <param name="configPath">
    /// The --config this process was started with. The worker never loads a configuration chosen by its non-elevated caller,
    /// so with one the broker refuses to elevate rather than apply a different configuration than this process assessed.
    /// </param>
    /// <param name="address">The --address this process was started with, so the worker identifies the same printers.</param>
    /// <param name="pin">The configuration this process loaded; the worker refuses to apply anything when it loads other bytes.</param>
    public ElevationBroker(ILog log, string? configPath, string? address, ConfigurationPin pin)
        : this(log, configPath, address, pin, StartElevated)
    {
    }

    /// <param name="clientProcessId">Who opened the pipe; tests whose scripted worker runs in this process replace it.</param>
    /// <param name="stopTimeout">How long a cancelled run waits for the worker (default <see cref="StopTimeout"/>).</param>
    internal ElevationBroker(ILog log, string? configPath, string? address, ConfigurationPin pin, Func<ProcessStartInfo, IWorkerProcess> start,
        Func<SafePipeHandle, int?>? clientProcessId = null, TimeSpan? stopTimeout = null)
    {
        _log = log;
        _configPath = configPath;
        _address = address;
        _pin = pin;
        _start = start;
        _clientProcessId = clientProcessId ?? PipeClient.ProcessId;
        _stopTimeout = stopTimeout ?? StopTimeout;
    }

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public async Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        string? problem = _configPath is null
            ? WorkerArguments.FindProblem(request)
            : "The elevated helper only uses the configuration built into the program, or the one next to it in a folder only " +
              $"administrators can change, not --config '{_configPath}'. To test another configuration, start AMWAG Printer Setup as administrator.";
        if (problem is not null)
        {
            _log.Error("elevation.request.invalid", problem);
            return new ElevatedRunResult(ElevationOutcome.FailedToStart, ExitCodes.InvalidArguments) { Error = problem };
        }

        // Nothing has started yet, so a cancelled run simply ends here.
        cancellationToken.ThrowIfCancellationRequested();
        string pipeName = WorkerArguments.PipeNamePrefix + RandomNumberGenerator.GetHexString(32, lowercase: true);
        string arguments = WorkerArguments.Build(request, pipeName, Environment.ProcessId, _address, _pin);

        using var pipe = new WorkerPipe(pipeName);
        try
        {
            return await RunWorkerAsync(request, pipe, arguments, progress, cancellationToken);
        }
        finally
        {
            await pipe.CloseAsync();
        }
    }

    private async Task<ElevatedRunResult> RunWorkerAsync(
        WorkerRequest request, WorkerPipe pipe, string arguments, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        IWorkerProcess worker;
        try
        {
            _log.Info("elevation.requested", "Asking for administrator approval to run the machine-wide steps.", new Dictionary<string, object?>
            {
                ["target"] = request.Target.Key,
                ["mode"] = CommandLineParser.ModeName(request.Mode),
                ["arguments"] = arguments,
            });
            // ShellExecuteEx returns only once the UAC prompt is answered; keep the window responsive meanwhile.
            worker = await Task.Run(() => _start(CreateStartInfo(arguments)), CancellationToken.None);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _log.Info("elevation.declined", "The administrator approval prompt was declined; nothing was changed.");
            return new ElevatedRunResult(ElevationOutcome.Declined, ExitCodes.ElevationRequired) { Error = "The administrator approval prompt was declined." };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            _log.Error("elevation.start.failed", "The elevated worker could not be started.", ex);
            return new ElevatedRunResult(ElevationOutcome.FailedToStart, ExitCodes.InternalError) { Error = ex.Message };
        }

        using (worker)
        {
            _log.Info("elevation.started", "The elevated worker started.", new Dictionary<string, object?> { ["pid"] = worker.Id });
            // From here on the worker may change the machine: a cancelled run stops it and waits, it never leaves it behind.
            Task exited = worker.WaitForExitAsync(CancellationToken.None);
            bool? connected = await WaitForWorkerAsync(pipe, worker, exited, cancellationToken);
            if (connected is null)
                return await StopAsync(worker, exited, pipe, new WorkerOutput(null, null, null, null), progress);

            if (connected == false && exited.IsCompleted)
            {
                // The worker changes nothing before it has connected to this window, so nothing was started.
                string error = string.Create(CultureInfo.InvariantCulture,
                    $"The elevated helper ended before it connected to this window ({WhyNotConnected(worker.ExitCode)}, exit code {worker.ExitCode}); nothing was changed.");
                _log.Error("elevation.connect.failed", error, data: new Dictionary<string, object?> { ["exitCode"] = worker.ExitCode });
                return new ElevatedRunResult(ElevationOutcome.FailedToStart, worker.ExitCode) { Error = error };
            }

            if (connected == false)
            {
                string error = pipe.Connection.Exception is { } failure
                    ? "The pipe to the elevated worker failed: " + failure.GetBaseException().Message
                    : $"The elevated worker did not connect within {ConnectTimeout.TotalSeconds:0} seconds.";
                _log.Error("elevation.connect.failed", error);
                // Closing the pipe (on return) also stops a worker that is still starting: it changes nothing without a window.
                return new ElevatedRunResult(ElevationOutcome.WorkerFailed, ExitCodes.InternalError) { Error = error };
            }

            _log.Info("elevation.connected", "The elevated worker connected.");
            WorkerOutput output = await ReadAsync(pipe.Stream, exited, progress, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return await StopAsync(worker, exited, pipe, output, progress);

            // Without a result the worker may still be finishing an action (a broken pipe stops it at the next boundary).
            int exitCode = await ExitCodeAsync(worker, exited, output.ReportedExitCode, output.Result is null ? _stopTimeout : ExitTimeout);
            _log.Info("elevation.finished", "The elevated worker finished.", new Dictionary<string, object?>
            {
                ["exitCode"] = exitCode,
                ["reportedExitCode"] = output.ReportedExitCode,
                ["hasResult"] = output.Result is not null,
                ["succeeded"] = output.Result?.Succeeded,
                ["error"] = output.Error,
            });

            if (output.Result is not null)
                return new ElevatedRunResult(ElevationOutcome.Completed, exitCode) { Result = output.Result, Error = output.Error };

            return new ElevatedRunResult(ElevationOutcome.WorkerFailed, exitCode)
            {
                Error = output.Error ?? $"worker ended without a result (exit code {exitCode})",
            };
        }
    }

    /// <summary>
    /// Waits for the started worker to connect: true when it did, false when it exited first or did not connect in time, null
    /// when the run was cancelled first. Any other process that opened the pipe (the name can be listed) is disconnected, and
    /// the pipe waits for the worker again.
    /// </summary>
    private async Task<bool?> WaitForWorkerAsync(WorkerPipe pipe, IWorkerProcess worker, Task exited, CancellationToken cancellationToken)
    {
        Task timeout = Task.Delay(ConnectTimeout, cancellationToken);
        while (true)
        {
            await Task.WhenAny(pipe.Connection, exited, timeout);
            if (cancellationToken.IsCancellationRequested)
                return null;
            if (!pipe.Connection.IsCompleted && exited.IsCompleted)
            {
                // A worker that connects, reports and exits quickly can finish before the connection is observed here.
                await Task.WhenAny(pipe.Connection, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
                if (cancellationToken.IsCancellationRequested)
                    return null;
            }

            if (!pipe.Connection.IsCompletedSuccessfully)
                return false;

            int? client = _clientProcessId(pipe.Handle);
            if (client == worker.Id)
                return true;

            _log.Warn("elevation.pipe.foreign_client", "A process other than the elevated worker opened the pipe; it was disconnected.",
                new Dictionary<string, object?> { ["clientPid"] = client, ["workerPid"] = worker.Id });
            pipe.Listen();
        }
    }

    /// <summary>
    /// The run was cancelled after the worker started. Closes this end of the pipe, the worker's stop signal (its next write,
    /// at the latest its next heartbeat, fails), then waits for it to exit, bounded by the stop timeout.
    /// </summary>
    private async Task<ElevatedRunResult> StopAsync(IWorkerProcess worker, Task exited, WorkerPipe pipe, WorkerOutput output, IProgress<ProgressUpdate> progress)
    {
        pipe.Close();
        _log.Info("elevation.stopping", "The run was cancelled; waiting for the elevated worker to stop after the step that is running.",
            new Dictionary<string, object?> { ["pid"] = worker.Id, ["timeoutSeconds"] = _stopTimeout.TotalSeconds });
        progress.Report(new ProgressUpdate(output.LastProgress?.Step ?? StepId.InstallDriver, StepState.Running, output.LastProgress?.Action,
            ProgressPhase.Waiting, "Finishing the step that is running with administrator rights"));

        bool stopped = await Task.WhenAny(exited, Task.Delay(_stopTimeout)) == exited;
        int exitCode = stopped ? worker.ExitCode : output.ReportedExitCode ?? ExitCodes.Cancelled;
        if (stopped)
            _log.Info("elevation.cancelled", "The elevated worker stopped.", new Dictionary<string, object?> { ["exitCode"] = exitCode, ["hasResult"] = output.Result is not null });
        else
            _log.Warn("elevation.stop.timeout", "The elevated worker did not stop in time; no longer waiting for it.", new Dictionary<string, object?> { ["timeoutSeconds"] = _stopTimeout.TotalSeconds });

        return new ElevatedRunResult(ElevationOutcome.Cancelled, exitCode) { Result = output.Result, Error = output.Error };
    }

    /// <summary>
    /// Reads the worker's messages until it closes its end, the pipe breaks, the run is cancelled, or the worker has exited and
    /// the drain time is over (another process could still hold the worker's end of the pipe).
    /// </summary>
    private async Task<WorkerOutput> ReadAsync(Stream pipe, Task exited, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        ApplyResult? result = null;
        int? exitCode = null;
        string? error = null;
        ProgressUpdate? last = null;
        using var stopReading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        Task reading = ReadLinesAsync();
        if (await Task.WhenAny(reading, DrainedAsync(exited)) != reading)
        {
            _log.Warn("elevation.pipe.lingering", "The elevated worker exited but its end of the pipe stayed open; stopped reading.");
            stopReading.Cancel();
        }

        await reading;
        return new WorkerOutput(result, exitCode, error, last);

        async Task ReadLinesAsync()
        {
            try
            {
                while (await reader.ReadLineAsync(stopReading.Token) is { } line)
                {
                    if (line.Length == 0)
                        continue;

                    PipeMessage? message = PipeProtocol.Decode(line);
                    switch (message?.Type)
                    {
                        case PipeMessageType.Heartbeat:
                            break;
                        case PipeMessageType.Progress:
                            last = message.Progress;
                            progress.Report(message.Progress!);
                            break;
                        case PipeMessageType.Result:
                            result = message.Result;
                            exitCode = message.ExitCode;
                            break;
                        case PipeMessageType.Error:
                            error = message.Message;
                            _log.Warn("elevation.worker.error", message.Message!);
                            break;
                        default:
                            _log.Warn("elevation.message.invalid", "Ignored an unreadable line from the elevated worker.", new Dictionary<string, object?> { ["length"] = line.Length });
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (stopReading.IsCancellationRequested)
            {
                // The run was cancelled (the caller sees its token) or the worker exited and the drain time is over.
            }
            catch (IOException ex)
            {
                _log.Warn("elevation.pipe.broken", "The connection to the elevated worker broke.", exception: ex);
                error ??= "The connection to the elevated worker broke: " + ex.Message;
            }
        }

        static async Task DrainedAsync(Task exited)
        {
            await exited;
            await Task.Delay(DrainTimeout);
        }
    }

    private async Task<int> ExitCodeAsync(IWorkerProcess worker, Task exited, int? reportedExitCode, TimeSpan timeout)
    {
        await Task.WhenAny(exited, Task.Delay(timeout));
        if (exited.IsCompletedSuccessfully)
            return worker.ExitCode;

        _log.Warn("elevation.exit.timeout", "The elevated worker has not exited yet.", new Dictionary<string, object?> { ["timeoutSeconds"] = timeout.TotalSeconds });
        return reportedExitCode ?? ExitCodes.InternalError;
    }

    /// <summary>Why a worker that exited with <paramref name="exitCode"/> before connecting did not run (WorkerHost.Run, Program.RunWorker).</summary>
    private static string WhyNotConnected(int exitCode) => exitCode switch
    {
        ExitCodes.ElevationRequired => "it did not get administrator rights",
        ExitCodes.InvalidArguments => "it rejected its command line",
        ExitCodes.Cancelled => "it could not reach this window",
        _ => "it failed while starting",
    };

    private static ProcessStartInfo CreateStartInfo(string arguments) =>
        new(Environment.ProcessPath ?? throw new InvalidOperationException("The path of this executable is unknown."))
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = arguments,
            WorkingDirectory = AppBootstrap.ExecutableDirectory,
        };

    private static IWorkerProcess StartElevated(ProcessStartInfo startInfo) =>
        new WorkerProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the elevated worker."));

    private sealed record WorkerOutput(ApplyResult? Result, int? ReportedExitCode, string? Error, ProgressUpdate? LastProgress);

    private sealed class WorkerProcess(Process process) : IWorkerProcess
    {
        public int Id => process.Id;

        public int ExitCode => process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }

    /// <summary>
    /// This process's end of the worker pipe (inbound, one instance). Only the current user, BUILTIN\Administrators and SYSTEM
    /// may open it: the worker runs as the employee under UAC consent, as another administrator with over-the-shoulder
    /// elevation, or under Administrator protection's separate administrator account. Everyone and Anonymous get nothing (the
    /// default DACL would let them read). Which client is the worker is still checked by its process id.
    /// </summary>
    private sealed class WorkerPipe : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly CancellationTokenSource _stopListening = new();
        private bool _closed;

        public WorkerPipe(string name)
        {
            _server = NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
            Connection = _server.WaitForConnectionAsync(_stopListening.Token);
        }

        public Stream Stream => _server;

        public SafePipeHandle Handle => _server.SafePipeHandle;

        /// <summary>The current wait for a client.</summary>
        public Task Connection { get; private set; }

        /// <summary>Drops the connected client and waits for the next one (a failure ends the wait with that exception).</summary>
        public void Listen()
        {
            try
            {
                _server.Disconnect();
                Connection = _server.WaitForConnectionAsync(_stopListening.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Connection = Task.FromException(ex);
            }
        }

        /// <summary>Stops waiting for a client and closes this end: the worker's next write fails.</summary>
        public void Close()
        {
            if (_closed)
                return;
            _closed = true;
            _stopListening.Cancel();
            _server.Dispose();
        }

        /// <summary><see cref="Close"/>, then waits for the last wait for a client to end (it only mattered while the worker could connect).</summary>
        public async Task CloseAsync()
        {
            Close();
            try
            {
                await Connection;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Ended by Close, or failed while nobody waited for it any more.
            }
        }

        public void Dispose()
        {
            Close();
            _stopListening.Dispose();
        }

        private static PipeSecurity CreateSecurity()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            const PipeAccessRights connect = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), connect, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), connect, AccessControlType.Allow));
            return security;
        }
    }
}
