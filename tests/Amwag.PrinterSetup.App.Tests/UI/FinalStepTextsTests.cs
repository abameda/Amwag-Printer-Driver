using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// What the whole step list says once a run in the window has ended: a step the run never reached says so (never "Waiting"), and
/// the driver check of a driver that was to be installed says whether it was, never "Driver is ready" for a missing driver, nor
/// "Driver not installed" when the read-back could not check it (the install action decides, or it is not known). A computer that
/// cannot be set up as it is does not say that no printer was found.
/// </summary>
public sealed class FinalStepTextsTests
{
    public static TheoryData<string, string> EarlyEnds => new()
    {
        { "failedToStart", "en" },
        { "failedToStart", "ar" },
        { "declined", "en" },
        { "declined", "ar" },
        { "planBlocker", "en" },
        { "refusedBlocker", "en" },
        { "refusedBlocker", "ar" },
        { "noPrinter", "en" },
        { "noPrinter", "ar" },
    };

    [Theory]
    [MemberData(nameof(EarlyEnds))]
    public void Steps_a_run_never_reached_say_so_at_the_end(string end, string language) => Sta.Run(() =>
    {
        (Scenario scenario, Func<FlowEnvironment, IElevationBroker> broker) = end switch
        {
            "failedToStart" => (Fresh(), Answer(new ElevatedRunResult(ElevationOutcome.FailedToStart, ExitCodes.ElevationRequired) { Error = "The elevated helper ended before it connected to this window; nothing was changed." })),
            "declined" => (Fresh(), Answer(new ElevatedRunResult(ElevationOutcome.Declined, ExitCodes.ElevationRequired) { Error = "The administrator approval prompt was declined." })),
            "planBlocker" => (new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPackage(p => p with { Approved = false }).Build(), InProcess()),
            "refusedBlocker" => (Fresh(), Answer(new ElevatedRunResult(ElevationOutcome.Completed, ExitCodes.Blocked)
            {
                Result = new ApplyResult { Findings = [new Finding(FindingCode.DriverPackageNotApproved, Severity.Error, "The driver must be installed, but the package is not approved.")] },
            })),
            _ => (new ScenarioBuilder().Build(), InProcess()),
        };
        using MainForm form = WindowRun.Create(scenario, SetupMode.Setup, broker, language);
        Strings t = Strings.For(form.Language);

        Task run = form.RunFlowAsync(SetupMode.Setup);
        WindowRun.PumpUntil(() => run.IsCompleted || form.CurrentView is NetworkAddressView);
        if (!run.IsCompleted)
        {
            // Nothing was found: the employee is asked for the printer's address and cancels.
            WindowRun.Click(form.CurrentView!, t.Text("ui.cancel"));
            WindowRun.PumpUntil(() => run.IsCompleted);
        }
        Assert.IsType<ResultView>(form.CurrentView);
        scenario.AssertNothingChanged();

        // Before: "Verification: Waiting" (and for no printer, three more), and "Checking driver: Driver is ready".
        Assert.DoesNotContain(WindowRun.Steps(form), s => s.EndsWith(": " + t.Text("step.pending"), StringComparison.Ordinal));
        Assert.Equal(t.StepTitle(StepId.Verify) + ": " + t.Text("step.notReached"), form.Steps.Describe(StepId.Verify));
        Assert.Equal(StepState.Skipped, form.Steps.StateOf(StepId.Verify));
        if (end == "noPrinter")
        {
            foreach (StepId step in new[] { StepId.CheckDriver, StepId.InstallDriver, StepId.ConfigureTicket })
                Assert.Equal(t.StepTitle(step) + ": " + t.Text("step.notReached"), form.Steps.Describe(step));
        }
        else if (end != "planBlocker")
        {
            // The driver is missing and nothing installed it.
            Assert.Equal(t.StepTitle(StepId.CheckDriver) + ": " + t.Text("step.CheckDriver.notInstalled"), form.Steps.Describe(StepId.CheckDriver));
            Assert.Equal(StepState.Warning, form.Steps.StateOf(StepId.CheckDriver));
        }

        if (language == "en")
        {
            Assert.Equal("Verification: Not reached", form.Steps.Describe(StepId.Verify));
            if (end is "failedToStart" or "declined" or "refusedBlocker")
                Assert.Equal("Checking driver: Driver not installed", form.Steps.Describe(StepId.CheckDriver));
        }
    });

    [Fact]
    public void A_driver_installed_by_the_run_is_ready_at_the_end() => Sta.Run(() =>
    {
        Scenario scenario = Fresh();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Setup);

        FlowResult result = WindowRun.Run(form, SetupMode.Setup);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        Assert.NotEmpty(scenario.Installer.Calls);
        Assert.Equal("Checking driver: Driver is ready", form.Steps.Describe(StepId.CheckDriver));
        Assert.DoesNotContain(WindowRun.Steps(form), s => s.EndsWith(": Waiting", StringComparison.Ordinal) || s.EndsWith(": Not reached", StringComparison.Ordinal));
    });

    [Theory]
    [InlineData("step.CheckDriver.willInstall", "Checking driver: Driver not installed")]
    [InlineData("step.CheckDriver.willUpdate", "Checking driver: Driver not updated")]
    public void A_driver_the_run_was_to_install_says_at_the_end_that_it_was_not(string planned, string expected) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        form.Steps.SetState(StepId.Detect, StepState.Succeeded);
        form.Steps.SetState(StepId.CheckDriver, StepState.Succeeded, planned);

        // Permission was declined: nothing was read back, and no install action ran.
        form.ShowResult(UiSamples.ElevationDeclined());

        Assert.Equal(expected, form.Steps.Describe(StepId.CheckDriver));
        form.SwitchLanguage();
        Assert.Equal(Strings.Arabic.StepTitle(StepId.CheckDriver) + ": " + Strings.Arabic.Text(planned == "step.CheckDriver.willInstall" ? "step.CheckDriver.notInstalled" : "step.CheckDriver.notUpdated"),
            form.Steps.Describe(StepId.CheckDriver));
    });

    [Fact]
    public void A_read_back_that_finds_the_driver_installed_makes_the_driver_check_ready() => Sta.Run(() =>
    {
        // The helper's connection broke after it had installed the driver: the read-back decides, not what the window heard.
        using var form = new MainForm(TestRuntime.Create("en"));
        form.Steps.SetState(StepId.CheckDriver, StepState.Succeeded, "step.CheckDriver.willInstall");
        var verification = UiSamples.Ready().Verification! with { Readiness = ReadinessLevel.NotReady };

        form.ShowResult(new FlowResult(FlowOutcome.Failed) { Target = UiSamples.UsbPrinter, Verification = verification, Report = UiSamples.Report(SetupMode.Setup) });

        Assert.Equal("Checking driver: Driver is ready", form.Steps.Describe(StepId.CheckDriver));
        Assert.Equal(StepState.Succeeded, form.Steps.StateOf(StepId.CheckDriver));
    });

    public static TheoryData<ActionOutcome?, string, string> ReadBacksWithoutADriverCheck => new()
    {
        // The install action of the run says what happened.
        { ActionOutcome.Succeeded, "step.CheckDriver.Succeeded", "en" },
        { ActionOutcome.Succeeded, "step.CheckDriver.Succeeded", "ar" },
        { ActionOutcome.Failed, "step.CheckDriver.notInstalled", "en" },
        // Neither the read-back nor an install action says it: not known.
        { null, "step.CheckDriver.notConfirmed", "en" },
        { null, "step.CheckDriver.notConfirmed", "ar" },
    };

    [Theory]
    [MemberData(nameof(ReadBacksWithoutADriverCheck))]
    public void A_read_back_that_did_not_find_the_printer_again_leaves_the_driver_to_the_install_action(ActionOutcome? install, string expected, string language) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create(language));
        Strings t = Strings.For(form.Language);
        // During the run: the driver was missing and was to be installed.
        form.Steps.SetState(StepId.Detect, StepState.Succeeded);
        form.Steps.SetState(StepId.CheckDriver, StepState.Succeeded, "step.CheckDriver.willInstall");
        // The read-back did not find the printer again (the USB printer was unplugged): it holds no driver check at all.
        var notFound = new VerificationReport
        {
            TargetKey = UiSamples.UsbPrinter.Key,
            Readiness = ReadinessLevel.NotReady,
            Checks =
            [
                new(VerificationItem.SpoolerRunning, CheckStatus.Pass, "Running", "Running", ""),
                new(VerificationItem.QueueExists, CheckStatus.Fail, "Printer " + UiSamples.UsbPrinter.Key, "not found", "The printer was not found again."),
            ],
        };
        var results = new List<ActionResult> { new(new PlannedAction(ActionKind.PrepareDriverPackage, true, "Prepare"), ActionOutcome.Succeeded, "Done") };
        if (install is { } outcome)
            results.Add(new ActionResult(new PlannedAction(ActionKind.InstallDriver, true, "Install"), outcome, "Done"));

        form.ShowResult(new FlowResult(FlowOutcome.NotReady)
        {
            Target = UiSamples.UsbPrinter,
            Verification = notFound,
            Findings = [new Finding(FindingCode.OperationFailed, Severity.Error, "The printer was not found again after the machine-wide steps.")],
            Report = UiSamples.Report(SetupMode.Setup) with { ApplyResults = [new ApplyResult { Results = results }] },
        });

        // Before: "Driver not installed" whenever a read-back existed, also next to an install that succeeded.
        string status = expected == "step.CheckDriver.Succeeded" ? t.StepStatus(StepId.CheckDriver, StepState.Succeeded) : t.Text(expected);
        Assert.Equal(t.StepTitle(StepId.CheckDriver) + ": " + status, form.Steps.Describe(StepId.CheckDriver));
        Assert.Equal(install == ActionOutcome.Succeeded ? StepState.Succeeded : StepState.Warning, form.Steps.StateOf(StepId.CheckDriver));
        if (language == "en" && install is null)
            Assert.Equal("Checking driver: Driver state not confirmed", form.Steps.Describe(StepId.CheckDriver));
    });

    [Fact]
    public void A_usb_printer_unplugged_after_the_machine_steps_keeps_the_installed_driver_ready() => Sta.Run(() =>
    {
        // A fresh install: the helper installs the driver, then the printer disappears before the read-back finds it again.
        Scenario scenario = Fresh();
        string instance = scenario.Devices.Devices.Single().InstanceId;
        using MainForm form = WindowRun.Create(scenario, SetupMode.Setup, e => new VanishingBroker(e, () => scenario.Devices.Remove(instance)));

        FlowResult result = WindowRun.Run(form, SetupMode.Setup);

        Assert.NotEmpty(scenario.Installer.Calls);
        Assert.DoesNotContain(result.Verification!.Checks, c => c.Item == VerificationItem.DriverInstalled);
        Assert.Contains(result.Report!.ApplyResults.SelectMany(r => r.Results), a => a is { Action.Kind: ActionKind.InstallDriver, Outcome: ActionOutcome.Succeeded });
        // Before: "Checking driver: Driver not installed" next to "Installing driver: Driver installed".
        Assert.Equal("Checking driver: Driver is ready", form.Steps.Describe(StepId.CheckDriver));
        Assert.Equal("Installing driver: Driver installed", form.Steps.Describe(StepId.InstallDriver));
    });

    public static TheoryData<SetupMode, string> BlockedComputers => new()
    {
        { SetupMode.Setup, "en" },
        { SetupMode.Setup, "ar" },
        { SetupMode.Repair, "en" },
    };

    [Theory]
    [MemberData(nameof(BlockedComputers))]
    public void A_computer_that_cannot_be_set_up_does_not_say_that_no_printer_was_found(SetupMode mode, string language) => Sta.Run(() =>
    {
        // Protected print mode is on next to a connected, set-up USB P-4030DN.
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithPlatform(p => p with { ProtectedPrintModeEnabled = true }).Build();
        using MainForm form = WindowRun.Create(scenario, mode, language: language);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, mode);

        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.NotEmpty(result.Report!.Candidates);
        // Before: "Detecting printer: No printer found".
        Assert.Equal(t.StepTitle(StepId.Detect) + ": " + t.Text("step.Detect.blocked"), form.Steps.Describe(StepId.Detect));
        Assert.Equal(StepState.Failed, form.Steps.StateOf(StepId.Detect));
        if (language == "en")
            Assert.Equal("Detecting printer: This computer cannot be set up as it is", form.Steps.Describe(StepId.Detect));
        else
            Assert.Equal("البحث عن الطابعة: لا يمكن إعداد هذا الجهاز بوضعه الحالي", form.Steps.Describe(StepId.Detect));
    });

    /// <summary>Runs the machine steps in this process, then lets the test change the system before the window reads it back.</summary>
    private sealed class VanishingBroker(FlowEnvironment environment, Action afterMachineSteps) : IElevationBroker
    {
        private readonly InProcessBroker _inner = new(environment);

        public bool IsElevated => false;

        public async Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
        {
            ElevatedRunResult result = await _inner.RunMachineStepsAsync(request, progress, cancellationToken);
            afterMachineSteps();
            return result;
        }
    }

    private static Scenario Fresh() => new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

    private static Func<FlowEnvironment, IElevationBroker> Answer(ElevatedRunResult answer) => e => new InProcessBroker(e) { Answer = answer };

    private static Func<FlowEnvironment, IElevationBroker> InProcess() => e => new InProcessBroker(e);
}
