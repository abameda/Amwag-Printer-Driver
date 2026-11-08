using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// What the step list says about the two apply steps once a run in the window has ended: a step that never began says so, a
/// stopped one says it did not finish, one left unfinished by the other step's failure says so, Diagnostics says it changes
/// nothing. Only a step with nothing to do says "Already installed" or "Already correct". Update by update, the other steps get
/// their own texts too: a detection a blocked computer or IT's refused model ended, Diagnostics' last word without a single
/// ticket printer, and a driver still to be installed.
/// </summary>
public sealed class ApplyStepStatusTests
{
    private const string NotStarted = "Not started; nothing was changed";

    [Theory]
    [InlineData(ElevationOutcome.FailedToStart, ExitCodes.ElevationRequired)]
    [InlineData(ElevationOutcome.FailedToStart, ExitCodes.Cancelled)]
    [InlineData(ElevationOutcome.Declined, ExitCodes.ElevationRequired)]
    public void A_helper_that_never_started_leaves_both_apply_steps_not_started(ElevationOutcome outcome, int exitCode) => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        using MainForm form = Run(scenario, SetupMode.Setup, environment => new InProcessBroker(environment)
        {
            Answer = new ElevatedRunResult(outcome, exitCode)
            {
                Error = "The elevated helper ended before it connected to this window; nothing was changed.",
            },
        });

        scenario.AssertNothingChanged();
        Assert.Equal("Installing driver: " + NotStarted, form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: " + NotStarted, form.Steps.Describe(StepId.ConfigureTicket));
        Assert.Equal(StepState.Skipped, form.Steps.StateOf(StepId.InstallDriver));
        form.SwitchLanguage();
        Assert.Equal(Strings.Arabic.StepTitle(StepId.InstallDriver) + ": " + Strings.Arabic.Text("step.notStarted"), form.Steps.Describe(StepId.InstallDriver));
    });

    [Fact]
    public void Diagnostics_says_that_its_apply_steps_change_nothing() => Sta.Run(() =>
    {
        // The driver is missing: Setup would install it, Diagnostics only reports it.
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        using MainForm form = Run(scenario, SetupMode.Diagnostics);

        scenario.AssertNothingChanged();
        Assert.Equal("Installing driver: Not changed: Diagnostics only checks", form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: Not changed: Diagnostics only checks", form.Steps.Describe(StepId.ConfigureTicket));
        form.SwitchLanguage();
        Assert.Equal(Strings.Arabic.StepTitle(StepId.ConfigureTicket) + ": " + Strings.Arabic.Text("step.notChanged"), form.Steps.Describe(StepId.ConfigureTicket));
    });

    [Fact]
    public void A_blocker_found_before_any_change_leaves_both_apply_steps_not_started() => Sta.Run(() =>
    {
        // The driver must be installed from a package IT has not approved: the plan is blocked before anything is asked.
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPackage(p => p with { Approved = false }).Build();

        using MainForm form = Run(scenario, SetupMode.Setup);

        scenario.AssertNothingChanged();
        Assert.Equal("Checking driver: Driver check failed", form.Steps.Describe(StepId.CheckDriver));
        Assert.Equal("Installing driver: " + NotStarted, form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: " + NotStarted, form.Steps.Describe(StepId.ConfigureTicket));
    });

    public static TheoryData<FindingCode, string, int> Refusals => new()
    {
        { FindingCode.OperationFailed, "printer not found or its identity changed", ExitCodes.NotReady },
        { FindingCode.DriverPackageNotApproved, "The driver must be installed, but the package is not approved.", ExitCodes.Blocked },
        { FindingCode.ApplyLockBusy, "Another AMWAG Printer Setup run is applying changes.", ExitCodes.NotReady },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_refused_administrator_step_leaves_both_apply_steps_not_started(FindingCode code, string detail, int exitCode) => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        // The helper changed nothing and said why: the printer gone, a blocker of its own, another run applying changes.
        using MainForm form = Run(scenario, SetupMode.Setup, environment => new InProcessBroker(environment)
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Completed, exitCode) { Result = new ApplyResult { Findings = [new Finding(code, Severity.Error, detail)] } },
        });

        scenario.AssertNothingChanged();
        Assert.Equal("Installing driver: " + NotStarted, form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: " + NotStarted, form.Steps.Describe(StepId.ConfigureTicket));
    });

    [Fact]
    public void A_helper_that_gave_no_result_does_not_claim_that_nothing_was_changed() => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        // It started, so it may have changed something before its connection broke: the flow does not know.
        using MainForm form = Run(scenario, SetupMode.Setup, environment => new InProcessBroker(environment)
        {
            Answer = new ElevatedRunResult(ElevationOutcome.WorkerFailed, ExitCodes.InternalError) { Error = "The connection to the elevated worker broke." },
        });

        Assert.Equal("Installing driver: Installation failed", form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: Could not set the ticket size", form.Steps.Describe(StepId.ConfigureTicket));
    });

    [Fact]
    public void A_run_stopped_during_the_administrator_step_says_that_its_steps_did_not_finish() => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        // The helper finished the step that was running and stopped; part of the install may be done.
        using MainForm form = Run(scenario, SetupMode.Setup, environment => new InProcessBroker(environment)
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Cancelled, ExitCodes.Cancelled),
        });

        Assert.Equal("Installing driver: Not finished: the setup was stopped", form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: Not finished: the setup was stopped", form.Steps.Describe(StepId.ConfigureTicket));
        form.SwitchLanguage();
        Assert.Equal(Strings.Arabic.StepTitle(StepId.InstallDriver) + ": " + Strings.Arabic.Text("step.stopped"), form.Steps.Describe(StepId.InstallDriver));
    });

    [Fact]
    public void A_step_whose_actions_were_all_skipped_after_a_failure_did_not_start() => Sta.Run(() =>
    {
        // The driver install fails: the queue and its defaults depend on it and are skipped, the form already exists.
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithTicketForm().Build();
        scenario.Installer.InstallFailure = new PrinterSetupException(FindingCode.DriverInstallFailed, "InstallPrinterDriverFromPackage failed (Win32 error 87).");

        using MainForm form = Run(scenario, SetupMode.Setup);

        Assert.Equal("Installing driver: Installation failed", form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: " + NotStarted, form.Steps.Describe(StepId.ConfigureTicket));
    });

    [Theory]
    [InlineData("packageUnavailable")]
    [InlineData("installFailed")]
    public void A_ticket_step_that_only_created_the_form_before_the_driver_failed_did_not_finish(string failure) => Sta.Run(() =>
    {
        // A fresh install: the ticket paper size is created, but the driver install fails, so the queue and its ticket defaults
        // are never written.
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        if (failure == "packageUnavailable")
            scenario.Packages.Failure = new PrinterSetupException(FindingCode.DriverPackageUnavailable, "No approved archive in 'drivers' and the download failed (HTTP 404).");
        else
            scenario.Installer.InstallFailure = new PrinterSetupException(FindingCode.DriverInstallFailed, "InstallPrinterDriverFromPackage failed (Win32 error 87).");

        using MainForm form = Run(scenario, SetupMode.Setup);

        Assert.Contains(scenario.Spooler.Writes, w => w.Operation == "AddForm");
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == "SetDevMode");
        // Before: "Set, with warnings", although no ticket size was set on any printer.
        Assert.Equal("Configuring ticket size: Not finished: another step failed", form.Steps.Describe(StepId.ConfigureTicket));
        Assert.Equal(StepState.Warning, form.Steps.StateOf(StepId.ConfigureTicket));
        form.SwitchLanguage();
        Assert.Equal(Strings.Arabic.StepTitle(StepId.ConfigureTicket) + ": " + Strings.Arabic.Text("step.afterFailure"), form.Steps.Describe(StepId.ConfigureTicket));
    });

    [Fact]
    public void Steps_with_nothing_to_do_still_say_already_installed_and_already_correct() => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();

        using MainForm form = Run(scenario, SetupMode.Repair);

        scenario.AssertNothingChanged();
        Assert.Equal("Installing driver: Already installed", form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal("Configuring ticket size: Already correct", form.Steps.Describe(StepId.ConfigureTicket));
    });

    [Theory]
    [InlineData(FlowOutcome.Cancelled, "Installing driver: Not finished: the setup was stopped")]
    [InlineData(FlowOutcome.ElevationDeclined, "Installing driver: " + NotStarted)]
    [InlineData(FlowOutcome.NotReady, "Installing driver: Installation failed")]
    public void An_apply_step_still_running_when_the_run_ends_gets_a_text_that_matches_the_outcome(FlowOutcome outcome, string expected) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        form.Steps.SetState(StepId.InstallDriver, StepState.Running);

        form.ShowResult(new FlowResult(outcome) { Report = UiSamples.Report(SetupMode.Setup) });

        Assert.Equal(expected, form.Steps.Describe(StepId.InstallDriver));
    });

    public static TheoryData<StepId, StepState, ActionKind?, FindingCode?, SetupMode, bool, string?> Updates => new()
    {
        // A step summary skipped without a reason had nothing to do.
        { StepId.InstallDriver, StepState.Skipped, null, null, SetupMode.Setup, false, null },
        { StepId.ConfigureTicket, StepState.Skipped, null, FindingCode.ElevationFailed, SetupMode.Setup, false, "step.notStarted" },
        { StepId.InstallDriver, StepState.Skipped, null, FindingCode.DriverPackageNotApproved, SetupMode.Repair, false, "step.notStarted" },
        { StepId.InstallDriver, StepState.Skipped, null, FindingCode.Cancelled, SetupMode.Setup, true, "step.stopped" },
        { StepId.ConfigureTicket, StepState.Skipped, null, null, SetupMode.Diagnostics, false, "step.notChanged" },
        // Incomplete: "with warnings" once one of its actions began, otherwise it did not start.
        { StepId.ConfigureTicket, StepState.Warning, null, null, SetupMode.Setup, false, "step.notStarted" },
        { StepId.InstallDriver, StepState.Warning, null, null, SetupMode.Setup, true, null },
        // Incomplete because the other apply step failed (the update carries that failure's code): not finished, unless nothing began.
        { StepId.ConfigureTicket, StepState.Warning, null, FindingCode.DriverInstallFailed, SetupMode.Setup, true, "step.afterFailure" },
        { StepId.ConfigureTicket, StepState.Warning, null, FindingCode.DriverPackageUnavailable, SetupMode.Repair, false, "step.notStarted" },
        // The driver check of Setup and Repair: a missing or outdated driver is still to be installed, not ready.
        { StepId.CheckDriver, StepState.Succeeded, null, FindingCode.DriverMissing, SetupMode.Setup, false, "step.CheckDriver.willInstall" },
        { StepId.CheckDriver, StepState.Succeeded, null, FindingCode.DriverOutdated, SetupMode.Repair, false, "step.CheckDriver.willUpdate" },
        { StepId.CheckDriver, StepState.Succeeded, null, FindingCode.DriverReady, SetupMode.Setup, false, null },
        { StepId.CheckDriver, StepState.Failed, null, FindingCode.DriverMissing, SetupMode.Diagnostics, false, null },
        { StepId.CheckDriver, StepState.Succeeded, ActionKind.StartSpooler, null, SetupMode.Setup, true, null },
        // The engine's update for one skipped action passes; the step's final state follows.
        { StepId.InstallDriver, StepState.Skipped, ActionKind.BindUsbDevice, null, SetupMode.Setup, false, null },
        { StepId.InstallDriver, StepState.Failed, null, FindingCode.ApplyLockBusy, SetupMode.Setup, false, "step.notStarted" },
        { StepId.Detect, StepState.Failed, null, FindingCode.ConfigurationChanged, SetupMode.Setup, false, "step.notStarted" },
        // A failed step keeps "failed", also when the window heard of none of its actions (the helper's connection broke).
        { StepId.InstallDriver, StepState.Failed, null, FindingCode.OperationFailed, SetupMode.Setup, false, null },
        { StepId.InstallDriver, StepState.Failed, null, FindingCode.DriverInstallFailed, SetupMode.Setup, true, null },
        { StepId.Detect, StepState.Skipped, null, FindingCode.MultipleSupportedPrinters, SetupMode.Setup, false, null },
        // A detection the computer stopped (an environment blocker) found no fault with the printer: not "No printer found".
        { StepId.Detect, StepState.Failed, null, FindingCode.ProtectedPrintModeEnabled, SetupMode.Setup, false, "step.Detect.blocked" },
        { StepId.Detect, StepState.Failed, null, FindingCode.SpoolerDisabled, SetupMode.Diagnostics, false, "step.Detect.blocked" },
        { StepId.Detect, StepState.Failed, null, FindingCode.SpoolerUnavailable, SetupMode.Repair, false, "step.Detect.blocked" },
        { StepId.Detect, StepState.Failed, null, FindingCode.UnsupportedOperatingSystem, SetupMode.Setup, false, "step.Detect.blocked" },
        { StepId.Detect, StepState.Failed, null, FindingCode.UnsupportedArchitecture, SetupMode.Setup, false, "step.Detect.blocked" },
        { StepId.Detect, StepState.Failed, null, FindingCode.ConfigurationInvalid, SetupMode.Repair, false, "step.Detect.blocked" },
        { StepId.Detect, StepState.Failed, null, FindingCode.DimensionsUnconfirmed, SetupMode.Setup, false, "step.Detect.blocked" },
        { StepId.Detect, StepState.Failed, null, FindingCode.NoSupportedPrinterFound, SetupMode.Setup, false, null },
        // IT's model that the printer contradicts: the printer was found, the run stopped before anything else.
        { StepId.Detect, StepState.Skipped, null, FindingCode.NetworkIdentityMismatch, SetupMode.Setup, false, "step.Detect.modelRefused" },
        // Diagnostics' last word on the driver check and the verification when no single ticket printer is known.
        { StepId.CheckDriver, StepState.Warning, null, FindingCode.MultipleSupportedPrinters, SetupMode.Diagnostics, false, "step.ticketUnknown" },
        { StepId.Verify, StepState.Warning, null, FindingCode.MultipleSupportedPrinters, SetupMode.Diagnostics, false, "step.ticketUnknown" },
        { StepId.Verify, StepState.Skipped, null, FindingCode.ProbableMatchRequiresConfirmation, SetupMode.Diagnostics, false, "step.notChecked" },
        { StepId.CheckDriver, StepState.Skipped, null, FindingCode.NoSupportedPrinterFound, SetupMode.Diagnostics, false, "step.notChecked" },
        { StepId.Verify, StepState.Warning, null, FindingCode.PrinterOffline, SetupMode.Diagnostics, false, null },
        { StepId.Verify, StepState.Warning, null, FindingCode.MultipleSupportedPrinters, SetupMode.Setup, false, null },
    };

    [Theory]
    [MemberData(nameof(Updates))]
    public void Each_update_gives_its_step_the_text_that_matches_what_happened(StepId step, StepState state, ActionKind? action, FindingCode? code, SetupMode mode, bool actionsBegun, string? expected)
    {
        var update = new ProgressUpdate(step, state, action, state == StepState.Failed ? ProgressPhase.Failed : ProgressPhase.Skipped, "") { Code = code };

        Assert.Equal(expected, MainForm.StatusKeyFor(update, mode, actionsBegun));
    }

    /// <summary>Runs the flow from the window over the scenario's fakes; returns the window once it shows the result.</summary>
    private static MainForm Run(Scenario scenario, SetupMode mode, Func<FlowEnvironment, IElevationBroker>? broker = null)
    {
        FlowEnvironment environment = scenario.Environment(mode);
        var runtime = new AppRuntime(environment, broker?.Invoke(environment) ?? new InProcessBroker(environment),
            new CommandLineOptions { Language = "en", Mode = mode }, new MemoryLog(), "1.0.0", AppContext.BaseDirectory);
        var form = new MainForm(runtime);
        _ = form.Handle;

        Task run = form.RunFlowAsync(mode);
        PumpUntil(() => run.IsCompleted);
        Assert.IsType<ResultView>(form.CurrentView);
        return form;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the UI thread.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
