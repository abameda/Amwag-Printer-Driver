using System.Diagnostics.CodeAnalysis;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Elevation;

internal sealed record WorkerOutcome(ApplyResult Result, int ExitCode);

/// <summary>
/// The elevated worker (--worker): applies the machine-wide actions for one target and reports over the pipe, only to the window
/// that started it. It re-detects and re-resolves the target itself instead of trusting the window, accepts no commands from the
/// pipe, refuses to apply a configuration other than the one the window loaded, and stops at the next action boundary once the
/// window is gone.
/// </summary>
internal static class WorkerHost
{
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SpoolerStartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs with arguments that <see cref="CommandLineParser"/> already validated (no errors); <paramref name="pin"/> is the
    /// configuration the window loaded.
    /// </summary>
    public static int Run(CommandLineOptions options, ConfigurationPin pin, ILog log)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            log.Error("worker.not-elevated", "The worker must run elevated; nothing was changed.");
            return ExitCodes.ElevationRequired;
        }
        if (options is not { PipeName: { } pipeName, TargetKey: { } targetKey, WindowProcessId: { } windowProcessId })
            return ExitCodes.InvalidArguments;

        // Connect before waiting for the apply lock: the window stops waiting for a connection after 60 seconds,
        // while another run may hold the lock for minutes.
        using var channel = WorkerChannel.Connect(pipeName, windowProcessId, PipeConnectTimeout, log);
        CancellationToken windowGone = channel.WindowGone;
        var selector = new TargetSelector(targetKey, options.ModelId, options.ModelAssertedByUser);
        try
        {
            windowGone.ThrowIfCancellationRequested();
            WorkerOutcome outcome = RunLocked(options, pin, selector, channel, log);
            var data = new Dictionary<string, object?>
            {
                ["exitCode"] = outcome.ExitCode,
                ["succeeded"] = outcome.Result.Succeeded,
                ["rebootRequired"] = outcome.Result.RebootRequired,
            };
            if (outcome.ExitCode == ExitCodes.Cancelled)
                log.Warn("worker.cancelled", "The window was closed; the elevated worker stopped after the step that was running.", data);
            else
                log.Info("worker.done", "The elevated worker finished.", data);
            channel.SendResult(outcome.Result, outcome.ExitCode);
            return outcome.ExitCode;
        }
        catch (OperationCanceledException) when (windowGone.IsCancellationRequested)
        {
            log.Warn("worker.cancelled", "No window is watching this run any more (it was closed, never connected, or the pipe is not the window's); nothing was changed.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            log.Error("worker.failed", "The elevated worker failed.", ex);
            channel.SendError(ex.Message);
            return ExitCodes.InternalError;
        }
    }

    /// <summary>
    /// The worker's work against the given services, without pipe or lock (also used by tests with fakes).
    /// <paramref name="cancellationToken"/> (the worker's <see cref="WorkerChannel.WindowGone"/>) stops detection and assessment
    /// at once and the apply at the next action boundary: finished actions stay, the rest are skipped, and the exit code is
    /// <see cref="ExitCodes.Cancelled"/>.
    /// </summary>
    internal static WorkerOutcome Apply(FlowEnvironment environment, TargetSelector selector, string? address, IProgress<ProgressUpdate> progress,
        CancellationToken cancellationToken = default)
    {
        if (selector.Key == WorkerArguments.SpoolerStartTarget)
            return StartSpooler(environment.Services, progress);

        ILog log = environment.Services.Log;
        var session = new SetupSession(environment.Config, environment.Branch, environment.Options, environment.Services);
        string[] addresses = new[] { selector.NetworkAddress, address, environment.Branch?.PrinterAddress }
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DetectionResult detection = session.Detect(new DetectRequest { NetworkAddresses = addresses }, cancellationToken);

        PrinterCandidate? target = session.Resolve(detection, selector);
        if (target is null)
        {
            log.Warn("worker.target.missing", "The printer was not found again or its identity changed; nothing was changed.", new Dictionary<string, object?> { ["target"] = selector.Key });
            var missing = new Finding(FindingCode.OperationFailed, Severity.Error, "printer not found or its identity changed")
            {
                Data = new Dictionary<string, string> { ["target"] = selector.Key },
            };
            return new WorkerOutcome(new ApplyResult { Findings = [missing] }, ExitCodes.NotReady);
        }

        SetupPlan plan = session.Assess(target, cancellationToken);
        if (!plan.CanProceed)
        {
            log.Warn("worker.blocked", "The plan has blockers; nothing was changed.", new Dictionary<string, object?> { ["blockers"] = plan.Blockers.Select(b => b.Code.ToString()).ToArray() });
            return new WorkerOutcome(new ApplyResult { Findings = plan.Blockers }, SetupFlowExitCodes.For(FlowOutcome.Blocked, plan.Blockers, selectionRequired: false));
        }

        ApplyResult result = session.Apply(plan, ApplyScope.Machine, progress, cancellationToken);
        int exitCode = result.Findings.Any(f => f.Code == FindingCode.Cancelled) ? ExitCodes.Cancelled
            : result.Succeeded ? ExitCodes.Ready
            : ExitCodes.NotReady;
        return new WorkerOutcome(result, exitCode);
    }

    /// <summary>
    /// The worker's environment from the configuration it loads itself (<paramref name="load"/>), or a findings-only refusal
    /// (nothing was changed, exit code 6) when that is not the configuration the window loaded (<paramref name="pin"/>): other
    /// bytes, bytes that no longer load, or bytes without the target's model. The bytes are compared before --model is checked,
    /// so a target model that IT removed or renamed meanwhile is reported as a changed configuration.
    /// </summary>
    internal static bool TryLoadEnvironment(CommandLineOptions options, ConfigurationPin pin, Func<LoadedConfiguration> load, ILog log,
        [NotNullWhen(true)] out FlowEnvironment? environment, [NotNullWhen(false)] out WorkerOutcome? refusal)
    {
        environment = null;
        refusal = null;
        LoadedConfiguration loaded;
        try
        {
            loaded = load();
        }
        catch (ConfigurationException ex)
        {
            // The window loaded a valid configuration, so the file changed (or can no longer be read) since.
            log.Error("worker.config.invalid", "The configuration no longer loads; nothing was changed.", ex, new Dictionary<string, object?> { ["errors"] = ex.Errors });
            refusal = Refusal(new Finding(FindingCode.ConfigurationChanged, Severity.Error,
                $"The configuration changed after the window loaded it and is no longer valid ({ex.Message}); nothing was changed. Close the program and start it again.")
            {
                Data = new Dictionary<string, string> { ["source"] = ex.Source, ["errors"] = string.Join("; ", ex.Errors) },
            });
            return false;
        }

        if (ConfigurationChanged(pin, ConfigurationPin.Of(loaded)) is { } changed)
        {
            log.Error("worker.config.changed", changed.Detail, data: changed.Data.ToDictionary(p => p.Key, p => (object?)p.Value));
            refusal = Refusal(changed);
            return false;
        }

        try
        {
            environment = AppBootstrap.Build(options, loaded, log);
            return true;
        }
        catch (StartupArgumentException ex)
        {
            // The same bytes as the window's: the window asked for a model that its own configuration does not have.
            log.Error("worker.arguments.invalid", ex.Message, ex);
            refusal = Refusal(new Finding(FindingCode.ConfigurationInvalid, Severity.Error, ex.Message + " Nothing was changed."));
            return false;
        }
    }

    /// <summary>
    /// The refusal when this worker loaded other configuration or branch bytes than the window (<paramref name="expected"/>),
    /// for example because IT changed a file in between; null when they are the same.
    /// </summary>
    internal static Finding? ConfigurationChanged(ConfigurationPin expected, ConfigurationPin loaded)
    {
        if (expected == loaded)
            return null;

        return new Finding(FindingCode.ConfigurationChanged, Severity.Error,
            "The configuration changed after the window loaded it; nothing was changed. Close the program and start it again.")
        {
            Data = new Dictionary<string, string>
            {
                ["windowConfig"] = expected.Config,
                ["workerConfig"] = loaded.Config,
                ["windowBranch"] = expected.Branch,
                ["workerBranch"] = loaded.Branch,
            },
        };
    }

    /// <summary>What the window shows while another run holds the apply lock (its code tells it apart from the waits inside actions).</summary>
    internal static ProgressUpdate WaitingForApplyLock(TargetSelector selector) =>
        new(selector.Key == WorkerArguments.SpoolerStartTarget ? StepId.Detect : StepId.InstallDriver, StepState.Running, null, ProgressPhase.Waiting,
            "Waiting for another AMWAG Printer Setup run to finish") { Code = FindingCode.ApplyLockBusy };

    private static WorkerOutcome RunLocked(CommandLineOptions options, ConfigurationPin pin, TargetSelector selector, WorkerChannel channel, ILog log)
    {
        if (!TryLoadEnvironment(options, pin, () => AppBootstrap.Load(options, log), log, out FlowEnvironment? environment, out WorkerOutcome? refusal))
            return refusal;

        using MachineApplyLock? applyLock = MachineApplyLock.Acquire(MachineApplyLock.DefaultTimeout, log, channel.WindowGone, () =>
            channel.Report(WaitingForApplyLock(selector)));
        if (applyLock is null)
            return new WorkerOutcome(new ApplyResult { Findings = [MachineApplyLock.BusyFinding()] }, ExitCodes.NotReady);

        return Apply(environment, selector, options.Address, channel, channel.WindowGone);
    }

    private static WorkerOutcome Refusal(Finding finding) => new(new ApplyResult { Findings = [finding] }, ExitCodes.ConfigurationInvalid);

    private static WorkerOutcome StartSpooler(SetupServices services, IProgress<ProgressUpdate> progress)
    {
        var action = new PlannedAction(ActionKind.StartSpooler, RequiresElevation: true, "Start the Print Spooler service");
        progress.Report(new ProgressUpdate(StepId.Detect, StepState.Running, ActionKind.StartSpooler, ProgressPhase.Started, action.Summary));

        ActionResult actionResult;
        if (services.Spooler.GetServiceStatus().IsRunning)
        {
            actionResult = new ActionResult(action, ActionOutcome.AlreadySatisfied, "The Print Spooler service is already running.");
        }
        else if (services.Spooler.TryStartService(SpoolerStartTimeout, out string? failureReason))
        {
            actionResult = new ActionResult(action, ActionOutcome.Succeeded, "The Print Spooler service was started.");
        }
        else
        {
            actionResult = new ActionResult(action, ActionOutcome.Failed, failureReason ?? "The Print Spooler service did not start.")
            {
                FailureCode = FindingCode.SpoolerUnavailable,
            };
        }

        bool running = actionResult.Outcome != ActionOutcome.Failed;
        services.Log.Info("worker.spooler", actionResult.Detail, new Dictionary<string, object?> { ["outcome"] = actionResult.Outcome.ToString() });
        progress.Report(new ProgressUpdate(StepId.Detect, running ? StepState.Succeeded : StepState.Failed, ActionKind.StartSpooler,
            running ? ProgressPhase.Completed : ProgressPhase.Failed, actionResult.Detail) { Code = running ? null : FindingCode.SpoolerUnavailable });
        return new WorkerOutcome(new ApplyResult { Results = [actionResult] }, running ? ExitCodes.Ready : ExitCodes.NotReady);
    }
}
