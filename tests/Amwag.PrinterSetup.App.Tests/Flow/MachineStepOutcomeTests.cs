using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Core.Units;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>
/// How a run ends when the machine-wide steps do not simply complete: the elevated side refused (apply lock busy, printer
/// gone, a blocker), the helper ended without a result, or the operator stopped the run. Nothing is assumed: the per-user
/// step does not run on top of missing machine steps, the steps say what happened, and the state is read back.
/// </summary>
public sealed class MachineStepOutcomeTests
{
    private static readonly string TicketQueue = SampleConfig.DriverName(SampleConfig.P4030);

    private readonly UpdateCollector _updates = new();

    // ---- Refusals ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_busy_worker_changes_nothing_and_the_per_user_step_does_not_run()
    {
        Scenario scenario = FormAndPersonalDefaultsNeedRepair();
        Finding busy = MachineApplyLock.BusyFinding();
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Completed, ExitCodes.NotReady) { Result = new ApplyResult { Findings = [busy] } },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.False(scenario.Log.Contains("flow.apply.user"));
        scenario.AssertNothingChanged();
        Finding top = result.Findings.First(f => f.Severity == Severity.Error);
        Assert.Equal((busy.Code, busy.Detail), (top.Code, top.Detail));
        ProgressUpdate ticket = LastStep(StepId.ConfigureTicket);
        Assert.Equal((StepState.Skipped, busy.Code, "Not started: " + busy.Detail), (ticket.State, ticket.Code, ticket.Detail));
        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.NotNull(result.Verification);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("gone")]
    public async Task A_refused_machine_step_reports_the_steps_it_had_planned_as_not_started_and_is_read_back(string refusal)
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        Finding reason = refusal == "busy"
            ? MachineApplyLock.BusyFinding()
            : new Finding(FindingCode.OperationFailed, Severity.Error, "printer not found or its identity changed");
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Completed, ExitCodes.NotReady) { Result = new ApplyResult { Findings = [reason] } },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        // The elevated side began nothing: the steps say so with the reason's code, whatever the reason.
        ProgressUpdate install = LastStep(StepId.InstallDriver);
        Assert.Equal((StepState.Skipped, reason.Code, "Not started: " + reason.Detail), (install.State, install.Code, install.Detail));
        Assert.NotEqual(StepState.Succeeded, LastStep(StepId.ConfigureTicket).State);
        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.Single(result.Verifications);
        Assert.Single(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_worker_that_found_a_blocker_blocks_the_run()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var blocker = new Finding(FindingCode.ProtectedPrintModeEnabled, Severity.Error, "Windows protected print mode is on.");
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Completed, ExitCodes.Blocked) { Result = new ApplyResult { Findings = [blocker] } },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.Equal(ExitCodes.Blocked, result.ExitCode);
        Assert.Null(result.Verification);
        Assert.Equal(FindingCode.ProtectedPrintModeEnabled, LastStep(StepId.InstallDriver).Code);
        Assert.False(scenario.Log.Contains("flow.apply.user"));
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_refused_step_the_check_cannot_see_still_ends_not_ready()
    {
        // Only the USB binding is missing, which verification does not check: the refusal itself decides.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false).WithModelDriver().WithUsbQueue().WithTicketForm().WithCorrectDefaults(TicketQueue)
            .Build();
        var environment = scenario.Environment();
        var flow = new SetupFlow(environment, new SilentInteraction(CommandLineParser.Parse(["--silent"])), new InProcessBroker(environment) { IsElevated = true })
        {
            AcquireApplyLock = (_, _, _) => null,
        };

        FlowResult result = await flow.RunAsync(_updates, CancellationToken.None);

        Assert.Equal((FlowOutcome.NotReady, ExitCodes.NotReady), (result.Outcome, result.ExitCode));
        Assert.Single(result.Verifications);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_refused_spooler_start_is_a_refusal_not_a_spooler_that_would_not_start()
    {
        Scenario scenario = new ScenarioBuilder().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).WithInstalledUsbPrinter().Build();
        Finding busy = MachineApplyLock.BusyFinding();
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Completed, ExitCodes.NotReady) { Result = new ApplyResult { Findings = [busy] } },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal((FlowOutcome.NotReady, ExitCodes.NotReady), (result.Outcome, result.ExitCode));
        ProgressUpdate detect = LastStep(StepId.Detect);
        Assert.Equal((StepState.Failed, busy.Code), (detect.State, detect.Code));
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.SpoolerUnavailable);
    }

    [Fact]
    public async Task Stopping_during_the_final_check_still_reads_the_applied_changes_back()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var environment = scenario.Environment();
        using var stop = new CancellationTokenSource();
        var progress = new Progress(update =>
        {
            if (update.Progress is { Step: StepId.Verify, Phase: ProgressPhase.Started })
                stop.Cancel();
        });

        FlowResult result = await new SetupFlow(environment, new ScriptedInteraction(), new InProcessBroker(environment)).RunAsync(progress, stop.Token);

        Assert.NotEmpty(scenario.Spooler.Writes);
        Assert.Single(result.Verifications);
        Assert.NotNull(result.Verification);
        Assert.True(scenario.Log.Contains("flow.verified"));
    }

    [Fact]
    public async Task A_stop_before_the_per_user_step_leaves_that_step_not_finished_and_reads_back()
    {
        // Only the employee's own preferences need correcting, so the per-user step is the only change.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithPerUserDefaults(TicketQueue, d => d.Scale = 80)
            .Build();
        var environment = scenario.Environment();
        using var stop = new CancellationTokenSource();
        var progress = new Progress(update =>
        {
            _updates.Report(update);
            if (update.Progress is { Step: StepId.CheckDriver, Phase: ProgressPhase.Completed })
                stop.Cancel();
        });

        FlowResult result = await new SetupFlow(environment, new ScriptedInteraction(), new InProcessBroker(environment)).RunAsync(progress, stop.Token);

        // Before, the skipped per-user action read as "applied with warnings".
        ProgressUpdate ticket = LastStep(StepId.ConfigureTicket);
        Assert.Equal((StepState.Skipped, FindingCode.Cancelled), (ticket.State, ticket.Code));
        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Single(result.Verifications);
    }

    [Fact]
    public async Task A_run_blocked_after_changes_keeps_its_blocker_among_the_current_findings()
    {
        // The installed driver's paper range is only readable once it is installed: the machine steps ran, then the per-user
        // re-assessment found the blocker.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { MinWidthTenthsOfMillimeter = 900 })
            .Build();

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.NotEmpty(scenario.Installer.Calls);
        Finding blocker = Assert.Single(result.Report!.Plans[^1].Blockers);
        Assert.Contains(result.CurrentFindings, f => f.Code == blocker.Code && f.Severity == Severity.Error);
        // What the first plan found before the changes no longer holds.
        Assert.DoesNotContain(result.CurrentFindings, f => f.Code == FindingCode.DriverMissing);
    }

    [Fact]
    public async Task An_elevated_window_waiting_for_the_apply_lock_says_it_waits_for_another_run()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var environment = scenario.Environment();
        var flow = new SetupFlow(environment, new ScriptedInteraction(), new InProcessBroker(environment) { IsElevated = true })
        {
            AcquireApplyLock = (_, _, waiting) =>
            {
                waiting();
                return null;
            },
        };

        await flow.RunAsync(_updates, CancellationToken.None);

        Assert.Contains(_updates.Updates, u => u.Progress is { Step: StepId.InstallDriver, Phase: ProgressPhase.Waiting, Code: FindingCode.ApplyLockBusy });
    }

    [Fact]
    public async Task An_elevated_run_that_cannot_get_the_apply_lock_is_not_ready_instead_of_an_internal_error()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var environment = scenario.Environment();
        var flow = new SetupFlow(environment, new SilentInteraction(CommandLineParser.Parse(["--silent"])), new InProcessBroker(environment) { IsElevated = true })
        {
            AcquireApplyLock = (_, _, _) => null,
        };

        FlowResult result = await flow.RunAsync(_updates, CancellationToken.None);

        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Finding top = result.Findings.First(f => f.Severity == Severity.Error);
        Assert.Equal(MachineApplyLock.BusyFinding().Detail, top.Detail);
        Assert.False(scenario.Log.Contains("flow.failed"));
        Assert.Single(result.Verifications);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Steps_with_planned_actions_that_did_not_run_are_not_reported_as_done()
    {
        // The plan needs UpdateForm and ConfigureGlobalDefaults (machine) and ConfigureUserDefaults (this user); the elevated side
        // reports only UpdateForm.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithTicketForm(new PaperDimensions(82_300, 170_200))
            .Build();
        var updateForm = new PlannedAction(ActionKind.UpdateForm, RequiresElevation: true, "Correct the ticket paper size");
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Completed, ExitCodes.Ready)
            {
                Result = new ApplyResult { Results = [new ActionResult(updateForm, ActionOutcome.Succeeded, "Form updated.")] },
            },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        // Before, one succeeded action of the step (and no failed one) was enough for "Ticket size set".
        ActionResult[] applied = result.Report!.ApplyResults.SelectMany(r => r.Results).ToArray();
        Assert.Contains(applied, r => r is { Action.Kind: ActionKind.UpdateForm, Outcome: ActionOutcome.Succeeded });
        Assert.DoesNotContain(applied, r => r.Action.Kind == ActionKind.ConfigureGlobalDefaults || r.Outcome == ActionOutcome.Failed);
        Assert.Equal(StepState.Warning, LastStep(StepId.ConfigureTicket).State);
    }

    // ---- A helper that gave no result ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_worker_that_ended_without_a_result_fails_but_the_system_is_read_back()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.WorkerFailed, ExitCodes.InternalError) { Error = "worker ended without a result (exit code 10)" },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal(ExitCodes.InternalError, result.ExitCode);
        Assert.NotNull(result.Verification);
        Assert.Single(result.Verifications);
        ProgressUpdate install = LastStep(StepId.InstallDriver);
        Assert.Equal(StepState.Failed, install.State);
        Assert.StartsWith("Not known whether this was applied", install.Detail, StringComparison.Ordinal);
        Assert.False(scenario.Log.Contains("flow.apply.user"));
    }

    // ---- The operator stopped the run ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_stopped_worker_ends_cancelled_after_reading_back_the_system()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        using var stop = new CancellationTokenSource();
        var broker = new StoppingBroker(stop, _ => Task.FromResult(new ElevatedRunResult(ElevationOutcome.Cancelled, ExitCodes.Cancelled)));

        FlowResult result = await new SetupFlow(scenario.Environment(), new ScriptedInteraction(), broker).RunAsync(_updates, stop.Token);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        Assert.Single(result.Verifications);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.Cancelled, Severity: Severity.Warning } && f.Detail.Contains("administrator steps", StringComparison.Ordinal));
        Assert.Equal((StepState.Skipped, FindingCode.Cancelled), (LastStep(StepId.InstallDriver).State, LastStep(StepId.InstallDriver).Code));
        Assert.False(scenario.Log.Contains("flow.apply.user"));
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Stopping_while_the_worker_finishes_skips_the_per_user_step_and_reads_back_what_it_did()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var environment = scenario.Environment();
        using var stop = new CancellationTokenSource();
        // The worker applies everything; the stop comes in meanwhile.
        var worker = new InProcessBroker(environment);
        var broker = new StoppingBroker(stop, request => worker.RunMachineStepsAsync(request, new Progress<ProgressUpdate>(), CancellationToken.None));

        FlowResult result = await new SetupFlow(environment, new ScriptedInteraction(), broker).RunAsync(_updates, stop.Token);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        VerificationReport verification = Assert.Single(result.Verifications);
        Assert.Contains(verification.Checks, c => c is { Item: VerificationItem.FormExists, Status: CheckStatus.Pass });
        Assert.False(scenario.Log.Contains("flow.apply.user"));
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode) && w.Detail == nameof(DevModeScope.PerUser));
        Assert.Equal(StepState.Succeeded, LastStep(StepId.InstallDriver).State);
    }

    [Fact]
    public async Task Stopping_an_elevated_run_before_its_machine_steps_changes_nothing_and_is_read_back()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var environment = scenario.Environment();
        using var stop = new CancellationTokenSource();
        var flow = new SetupFlow(environment, new ScriptedInteraction(), new InProcessBroker(environment) { IsElevated = true })
        {
            // The stop arrives once the lock is held: the runner then starts no action.
            AcquireApplyLock = (_, _, _) =>
            {
                stop.Cancel();
                return new HeldLock();
            },
        };

        FlowResult result = await flow.RunAsync(_updates, stop.Token);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Single(result.Verifications);
        scenario.AssertNothingChanged();
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>An installed printer whose ticket form has the wrong size (machine step) and whose user has own defaults (per-user step).</summary>
    private static Scenario FormAndPersonalDefaultsNeedRepair() =>
        new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithTicketForm(new PaperDimensions(82_300, 170_200))
            .WithPerUserDefaults(TicketQueue, d => d.Scale = 80)
            .Build();

    private ProgressUpdate LastStep(StepId step) =>
        _updates.Updates.Last(u => u.Kind == FlowEventKind.Step && u.Progress?.Step == step).Progress!;

    private async Task<FlowResult> Run(FlowEnvironment environment, ISetupInteraction interaction, IElevationBroker broker) =>
        await new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

    /// <summary>The operator closes the window while the elevated helper runs: the run is cancelled before the helper returns.</summary>
    private sealed class StoppingBroker(CancellationTokenSource stop, Func<WorkerRequest, Task<ElevatedRunResult>> worker) : IElevationBroker
    {
        public bool IsElevated => false;

        public async Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
        {
            await stop.CancelAsync();
            return await worker(request);
        }
    }

    private sealed class HeldLock : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Progress(Action<FlowUpdate> report) : IProgress<FlowUpdate>
    {
        public void Report(FlowUpdate value) => report(value);
    }
}
