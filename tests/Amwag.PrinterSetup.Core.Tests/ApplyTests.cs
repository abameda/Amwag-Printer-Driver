using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class ApplyTests
{
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    private static (SetupSession Session, SetupPlan Plan) Plan(Scenario scenario, SetupMode mode = SetupMode.Setup)
    {
        var session = scenario.CreateSession(mode);
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        return (session, session.Assess(target, default));
    }

    [Fact]
    public void Diagnostics_never_calls_a_write_method()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB002", correctDefaults: false)
            .WithPerUserDefaults(SampleConfig.DriverName(SampleConfig.P5032), d => d.Scale = 50)
            .WithSpooler(ServiceRunState.Running)
            .Build();
        var session = scenario.CreateSession(SetupMode.Diagnostics);
        var detection = session.Detect(new DetectRequest { NetworkAddresses = ["10.0.0.5"] }, default);

        foreach (var candidate in detection.Candidates)
        {
            var plan = session.Assess(candidate, default);
            Assert.Empty(plan.Actions);
            var apply = session.Apply(plan, ApplyScope.All, null, default);
            Assert.Empty(apply.Results);
            session.Verify(TargetSelector.For(candidate), default);
        }

        Assert.Equal(2, detection.Candidates.Count);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Empty(scenario.Installer.Calls);
        Assert.Empty(scenario.Packages.PreparedPackageIds);
    }

    [Fact]
    public void Diagnostics_plan_with_actions_is_still_never_applied()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan with { Mode = SetupMode.Diagnostics }, ApplyScope.All, null, default);

        Assert.All(apply.Results, r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Blocked_plan_skips_every_action_and_reports_the_blockers()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).WithTicket(t => t with { DimensionsConfirmed = false }).Build();
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.NotEmpty(apply.Results);
        Assert.All(apply.Results, r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Contains(apply.Findings, f => f.Code == FindingCode.DimensionsUnconfirmed);
        // The blockers are Error findings: nothing failed, but nothing was applied either.
        Assert.False(apply.Succeeded);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Scopes_split_machine_and_user_actions()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).WithPerUserDefaults(Queue4030, d => d.Scale = 80).Build();
        var (session, plan) = Plan(scenario);
        Assert.Equal([ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(plan));

        var machine = session.Apply(plan, ApplyScope.Machine, null, default);
        var user = session.Apply(plan, ApplyScope.User, null, default);

        Assert.Equal([ConfigureGlobalDefaults], machine.Results.Select(r => r.Action.Kind));
        Assert.Equal([ConfigureUserDefaults], user.Results.Select(r => r.Action.Kind));
        Assert.True(machine.Succeeded && user.Succeeded);
    }

    [Fact]
    public void Spooler_failure_reports_code_and_win32_error_and_skips_dependents()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().WithModelDriver().WithUsbQueue().Build();
        scenario.Spooler.FailOperation(nameof(IPrintSpooler.AddForm), FakePrintSpooler.ErrorAccessDenied);
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        var form = Result(apply, CreateForm);
        Assert.Equal(ActionOutcome.Failed, form.Outcome);
        Assert.Equal(FindingCode.FormCreateFailed, form.FailureCode);
        Assert.Equal(5, form.Win32Error);
        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, ConfigureGlobalDefaults));
        Assert.Contains("CreateForm", Result(apply, ConfigureGlobalDefaults).Detail, StringComparison.Ordinal);
        Assert.False(apply.Succeeded);
        var finding = Assert.Single(apply.Findings, f => f.Code == FindingCode.FormCreateFailed);
        Assert.Equal("5", finding.Data["win32Error"]);
        Assert.True(scenario.Log.Contains("apply.action.failed"));
    }

    [Fact]
    public void Spooler_rpc_failure_is_reported_as_spooler_unavailable()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var (session, plan) = Plan(scenario);
        scenario.Spooler.FailOperation(nameof(IPrintSpooler.SetDevMode), FakePrintSpooler.ErrorRpcServerUnavailable);

        var result = Result(session.Apply(plan, ApplyScope.All, null, default), ConfigureGlobalDefaults);

        Assert.Equal(FindingCode.SpoolerUnavailable, result.FailureCode);
        Assert.Equal(1722, result.Win32Error);
    }

    [Fact]
    public void Unexpected_exceptions_become_operation_failed()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var (session, plan) = Plan(scenario);
        scenario.Devices.Failure = new InvalidOperationException("CfgMgr32 exploded");

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        var bind = Result(apply, BindUsbDevice);
        Assert.Equal(ActionOutcome.Failed, bind.Outcome);
        Assert.Equal(FindingCode.OperationFailed, bind.FailureCode);
        Assert.Contains("CfgMgr32 exploded", bind.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_driver_install_skips_the_queue_and_defaults()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        scenario.Installer.InstallFailure = new PrinterSetupException(FindingCode.DriverInstallFailed, "InstallPrinterDriverFromPackage failed.", 87);
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(FindingCode.DriverInstallFailed, Result(apply, InstallDriver).FailureCode);
        Assert.Equal(87, Result(apply, InstallDriver).Win32Error);
        foreach (var kind in new[] { BindUsbDevice, CreateQueue, ConfigureGlobalDefaults, ConfigureUserDefaults })
            Assert.Equal(ActionOutcome.Skipped, Outcome(apply, kind));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, CreateForm));
    }

    [Fact]
    public void Already_installed_driver_needs_no_package_download()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var (session, plan) = Plan(scenario);
        scenario.Spooler.SeedDriver(new PrinterDriverInfo { Name = Queue4030, Environment = SampleConfig.Environment, Version = new Version(8, 4, 0, 0) });
        scenario.Devices.Update(SampleConfig.UsbInstanceId(SampleConfig.P4030), d => d with { ProblemCode = null, DriverDescription = Queue4030 });

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(apply, PrepareDriverPackage));
        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(apply, InstallDriver));
        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(apply, BindUsbDevice));
        Assert.Empty(scenario.Packages.PreparedPackageIds);
        // Nothing was installed in this run, so Plug and Play will not create a queue: no wait.
        Assert.Equal(TimeSpan.Zero, scenario.Clock.TotalSlept);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, CreateQueue));
        Assert.True(apply.Succeeded);
    }

    [Fact]
    public void Bind_without_a_connected_device_is_a_warning_not_a_failure()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var (session, plan) = Plan(scenario);
        scenario.Devices.Update(SampleConfig.UsbInstanceId(SampleConfig.P4030), d => d with { IsPresent = false });

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, BindUsbDevice));
        Assert.Contains(apply.Findings, f => f is { Code: FindingCode.DeviceNotConnected, Severity: Severity.Warning });
        Assert.True(apply.Succeeded);
    }

    [Fact]
    public void Installer_reporting_no_present_device_is_a_warning()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        scenario.Installer.DeviceInstallResultOverride = new DeviceInstallResult(false, false, FakeDriverInstaller.ErrorNoSuchDevinst, "No device.");
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, BindUsbDevice));
        Assert.DoesNotContain(apply.Results, r => r.Action.Kind == BindUsbDevice && r.Outcome == ActionOutcome.Failed);
    }

    [Fact]
    public void Device_install_failure_is_reported_with_its_error()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        scenario.Installer.DeviceInstallResultOverride = new DeviceInstallResult(false, false, FakeDriverInstaller.ErrorNoCompatDrivers, "No compatible driver.");
        var (session, plan) = Plan(scenario);

        var bind = Result(session.Apply(plan, ApplyScope.All, null, default), BindUsbDevice);

        Assert.Equal(FindingCode.DriverInstallFailed, bind.FailureCode);
        Assert.Equal(FakeDriverInstaller.ErrorNoCompatDrivers, bind.Win32Error);
    }

    [Fact]
    public void Reboot_required_by_the_device_install_is_propagated()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        scenario.Installer.DeviceInstallRequiresReboot = true;
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.True(apply.RebootRequired);
        Assert.Contains(apply.Findings, f => f.Code == FindingCode.RebootRequired);
    }

    [Fact]
    public void Binding_that_needs_a_restart_is_verified_as_linked_and_not_installed_again_before_the_restart()
    {
        // The device is on a class driver and its queue on the approved driver. The binding succeeds but Windows asks for a
        // restart; until then the device reports problem code 14 (CM_PROB_NEED_RESTART) with the approved driver.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();
        scenario.Installer.DeviceInstallResultOverride = new DeviceInstallResult(true, true, null, "Installed; restart required.");
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan, ApplyScope.All, null, default);
        scenario.Devices.Update(SampleConfig.UsbInstanceId(SampleConfig.P4030), d => d with { ProblemCode = DriverRules.NeedRestartProblemCode, DriverDescription = Queue4030 });
        var verification = session.Verify(TargetSelector.For(plan.Target), default);

        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, BindUsbDevice));
        Assert.True(apply.RebootRequired);
        Assert.False(Has(verification.Findings, FindingCode.DeviceDriverNotBound));
        Assert.True(Has(verification.Findings, FindingCode.RebootRequired, Severity.Warning));

        // Repair before the restart installs nothing again: no package, no second binding (and no second UAC prompt).
        var again = scenario.Run(SetupMode.Repair);
        Assert.Empty(again.Plan!.Actions);
        Assert.Single(scenario.Installer.Calls, c => c.StartsWith("InstallForDevice:", StringComparison.Ordinal));
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, again.Verification!.Readiness);
    }

    [Fact]
    public void Binding_of_a_device_that_already_waits_for_a_restart_reports_the_restart()
    {
        // Windows installed the approved driver on the device between the plan and the apply, and needs a restart for it.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();
        var (session, plan) = Plan(scenario);
        Assert.Contains(BindUsbDevice, Kinds(plan));
        scenario.Devices.Update(SampleConfig.UsbInstanceId(SampleConfig.P4030), d => d with { ProblemCode = DriverRules.NeedRestartProblemCode, DriverDescription = Queue4030 });

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(apply, BindUsbDevice));
        Assert.Contains("Windows must restart", Result(apply, BindUsbDevice).Detail, StringComparison.Ordinal);
        Assert.True(apply.RebootRequired);
        Assert.Contains(apply.Findings, f => f is { Code: FindingCode.RebootRequired, Severity: Severity.Warning });
        Assert.Empty(scenario.Installer.Calls);
    }

    [Fact]
    public void Cancellation_during_the_plug_and_play_wait_skips_the_rest_without_throwing()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPlugAndPlayQueueDelay(null).Build();
        var (session, plan) = Plan(scenario);
        using var cancellation = new CancellationTokenSource();
        var progress = new SyncProgress(update =>
        {
            if (update.Phase == ProgressPhase.Waiting && update.Fraction > 0.2)
                cancellation.Cancel();
        });

        var apply = session.Apply(plan, ApplyScope.All, progress, cancellation.Token);

        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, CreateQueue));
        Assert.Contains("Cancelled", Result(apply, CreateQueue).Detail, StringComparison.Ordinal);
        Assert.All(apply.Results.SkipWhile(r => r.Action.Kind != CreateQueue), r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Contains(apply.Findings, f => f.Code == FindingCode.Cancelled);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
    }

    [Fact]
    public void Cancellation_before_apply_skips_everything()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var (session, plan) = Plan(scenario);

        var apply = session.Apply(plan, ApplyScope.All, null, new CancellationToken(canceled: true));

        Assert.All(apply.Results, r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Contains(apply.Findings, f => f.Code == FindingCode.Cancelled);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Progress_reports_each_action_on_its_visible_step()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithSpooler(ServiceRunState.Stopped).Build();
        var (session, plan) = Plan(scenario);
        var updates = new List<ProgressUpdate>();

        session.Apply(plan, ApplyScope.All, new SyncProgress(updates.Add), default);

        Assert.Contains(updates, u => u is { Action: StartSpooler, Step: StepId.CheckDriver, Phase: ProgressPhase.Completed });
        Assert.Contains(updates, u => u is { Action: PrepareDriverPackage, Step: StepId.InstallDriver, Phase: ProgressPhase.Downloading });
        Assert.Contains(updates, u => u is { Action: InstallDriver, Phase: ProgressPhase.Installing });
        Assert.Contains(updates, u => u is { Action: CreateQueue, Step: StepId.InstallDriver, Phase: ProgressPhase.Waiting });
        Assert.Contains(updates, u => u is { Action: CreateForm, Step: StepId.ConfigureTicket, Phase: ProgressPhase.Completed, State: StepState.Succeeded });
        Assert.Contains(updates, u => u is { Action: ConfigureGlobalDefaults, Step: StepId.ConfigureTicket, Phase: ProgressPhase.Configuring });
        foreach (var action in plan.Actions)
            Assert.Contains(updates, u => u.Action == action.Kind && u.Phase == ProgressPhase.Started);
    }

    [Fact]
    public void Failed_action_reports_failed_progress_with_its_code()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        scenario.Spooler.RevertDevModeWrites.Add(DevModeScope.Global);
        var (session, plan) = Plan(scenario);
        var updates = new List<ProgressUpdate>();

        session.Apply(plan, ApplyScope.All, new SyncProgress(updates.Add), default);

        Assert.Contains(updates, u => u is { Action: ConfigureGlobalDefaults, State: StepState.Failed, Phase: ProgressPhase.Failed, Code: FindingCode.DefaultsNotPersisted });
    }

    [Fact]
    public void Disabled_spooler_cannot_be_started()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped).Build();
        var (session, plan) = Plan(scenario);
        scenario.Spooler.ServiceStatus = new SpoolerServiceStatus(ServiceRunState.Stopped, ServiceStartMode.Disabled);

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(FindingCode.SpoolerDisabled, Result(apply, StartSpooler).FailureCode);
        Assert.All(apply.Results.Where(r => r.Action.Kind != StartSpooler), r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Empty(scenario.Packages.PreparedPackageIds);
    }

    [Fact]
    public void Spooler_that_will_not_start_is_reported()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped).Build();
        scenario.Spooler.StartServiceSucceeds = false;
        var (session, plan) = Plan(scenario);

        var start = Result(session.Apply(plan, ApplyScope.All, null, default), StartSpooler);

        Assert.Equal(FindingCode.SpoolerNotRunning, start.FailureCode);
        Assert.Contains("did not start", start.Detail, StringComparison.Ordinal);
    }

    // ---- Defaults ------------------------------------------------------------------------------------------------

    [Fact]
    public void Driver_ignoring_orientation_is_a_warning_and_verification_fails_the_orientation()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { IgnoreOrientation = true })
            .Build();
        var global = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        global.Orientation = DmOrientation.Landscape;
        scenario.Spooler.SetStoredDevMode(Queue4030, DevModeScope.Global, global);

        var run = scenario.Run();

        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ConfigureGlobalDefaults));
        var rejected = Assert.Single(run.Apply!.Findings, f => f.Code == FindingCode.DriverRejectedSetting);
        Assert.Equal("orientation", rejected.Data["fields"]);
        Assert.Equal(SampleConfig.FormName, scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!.FormName);
        var verification = run.Verification!;
        Assert.Equal(CheckStatus.Fail, Check(verification, VerificationItem.DefaultOrientation).Status);
        Assert.Equal(ReadinessLevel.NotReady, verification.Readiness);
    }

    [Fact]
    public void Driver_switching_the_source_is_a_warning_when_the_tray_is_only_preferred()
    {
        // Preferred, not required: the default policy, and a model whose cassette could also feed the ticket.
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithModel(SampleConfig.P4030, m => m with { TicketPaperSource = m.TicketPaperSource with { RequiredForTicket = false } })
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { CassetteMinimumWidthTenthsOfMillimeter = 1050, SourceBelowCassetteMinimum = DmBin.Auto })
            .Build();

        var run = scenario.Run();

        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ConfigureGlobalDefaults));
        Assert.Contains(run.Apply!.Findings, f => f is { Code: FindingCode.PaperSourceRejected, Severity: Severity.Warning });
        Assert.Equal(DmBin.Auto, scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!.DefaultSource);
        Assert.Equal(CheckStatus.Warning, Check(run.Verification!, VerificationItem.DefaultPaperSource).Status);
    }

    [Fact]
    public void Driver_switching_the_source_fails_when_the_tray_is_required_and_nothing_is_written()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithConfig(c => c with { PrintPreferences = c.PrintPreferences with { PaperSource = PaperSourcePolicy.RequireMultipurposeTray } })
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { CassetteMinimumWidthTenthsOfMillimeter = 1050, SourceBelowCassetteMinimum = DmBin.Auto })
            .Build();

        var result = Result(scenario.Run().Apply!, ConfigureGlobalDefaults);

        Assert.Equal(FindingCode.PaperSourceRejected, result.FailureCode);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode));
    }

    [Fact]
    public void Driver_changing_the_paper_during_validation_fails_and_writes_nothing()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var (session, plan) = Plan(scenario);
        // The driver lists the form, but validation replaces it (it keeps only papers it really supports).
        var spooler = new PaperReplacingSpooler(scenario.Spooler);
        var services = new SetupServices
        {
            Spooler = spooler,
            Devices = scenario.Devices,
            Platform = scenario.Platform,
            Network = scenario.Network,
            Packages = scenario.Packages,
            Installer = scenario.Installer,
            Log = scenario.Log,
            Clock = scenario.Clock,
        };

        var apply = new SetupSession(scenario.Config, null, scenario.Options, services).Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(FindingCode.DriverRejectedSetting, Result(apply, ConfigureGlobalDefaults).FailureCode);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode));
    }

    [Fact]
    public void Per_user_defaults_without_an_override_are_already_satisfied()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var plan = new SetupPlan
        {
            Target = scenario.CreateSession().SelectAutomatically(scenario.CreateSession().Detect(new DetectRequest(), default)).Selected!,
            Mode = SetupMode.Setup,
            Actions = [new PlannedAction(ConfigureUserDefaults, false, "user defaults")],
        };

        var apply = scenario.CreateSession().Apply(plan, ApplyScope.User, null, default);

        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(apply, ConfigureUserDefaults));
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Form_created_concurrently_with_the_right_size_is_accepted()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().WithModelDriver().WithUsbQueue().Build();
        var (session, plan) = Plan(scenario);
        scenario.Spooler.SeedForm(FormInfo.UserForm(SampleConfig.FormName, SampleConfig.Ticket));

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(apply, CreateForm));
        Assert.Single(scenario.Spooler.Forms, f => f.Name == SampleConfig.FormName);
    }

    [Fact]
    public void Queue_that_appeared_on_the_port_with_another_driver_is_adopted_not_duplicated()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var (session, plan) = Plan(scenario);
        scenario.Spooler.SeedDriver(new PrinterDriverInfo { Name = SampleConfig.MicrosoftPcl6ClassDriver, Environment = SampleConfig.Environment });
        scenario.Spooler.SeedQueue(new PrintQueueInfo { Name = "Class driver queue", DriverName = SampleConfig.MicrosoftPcl6ClassDriver, PortName = "USB001" });

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.True(apply.Succeeded);
        Assert.Contains("instead of adding a second queue", Result(apply, CreateQueue).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        var queue = Assert.Single(scenario.Spooler.Queues, q => q.PortName == "USB001");
        Assert.Equal(Queue4030, queue.DriverName);
        Assert.Equal(ReadinessLevel.Ready, session.Verify(TargetSelector.For(plan.Target), default).Readiness);
    }

    [Fact]
    public void Plug_and_play_queue_with_a_class_driver_stops_the_wait_and_gets_the_approved_driver()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPlugAndPlayQueueDelay(null).Build();
        var (session, plan) = Plan(scenario);
        scenario.Spooler.SeedDriver(new PrinterDriverInfo { Name = "Microsoft IPP Class Driver", Environment = SampleConfig.Environment });
        scenario.Clock.Schedule(TimeSpan.FromSeconds(4), () => scenario.Spooler.SeedQueue(
            new PrintQueueInfo { Name = "TA P-4030DN", DriverName = "Microsoft IPP Class Driver", PortName = "USB001" }));

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.True(apply.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(4), scenario.Clock.TotalSlept);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetQueueDriver), Target: "TA P-4030DN" });
        Assert.Equal(Queue4030, Assert.Single(scenario.Spooler.Queues, q => q.PortName == "USB001").DriverName);
        Assert.Equal(ReadinessLevel.Ready, session.Verify(TargetSelector.For(plan.Target), default).Readiness);
    }

    /// <summary>A synchronous IProgress (Progress&lt;T&gt; would post to a synchronization context).</summary>
    private sealed class SyncProgress : IProgress<ProgressUpdate>
    {
        private readonly Action<ProgressUpdate> _report;

        public SyncProgress(Action<ProgressUpdate> report) => _report = report;

        public void Report(ProgressUpdate value) => _report(value);
    }

    /// <summary>Forwards to the fake but makes validation swap the paper for A4.</summary>
    private sealed class PaperReplacingSpooler : IPrintSpooler
    {
        private readonly FakePrintSpooler _inner;

        public PaperReplacingSpooler(FakePrintSpooler inner) => _inner = inner;

        public DevMode ValidateDevMode(string queueName, DevMode requested)
        {
            var validated = _inner.ValidateDevMode(queueName, requested);
            validated.PaperSize = DmPaper.A4;
            validated.FormName = "A4";
            return validated;
        }

        public SpoolerServiceStatus GetServiceStatus() => _inner.GetServiceStatus();
        public bool TryStartService(TimeSpan timeout, out string? failureReason) => _inner.TryStartService(timeout, out failureReason);
        public IReadOnlyList<PrintQueueInfo> GetQueues() => _inner.GetQueues();
        public IReadOnlyList<PrinterPortInfo> GetPorts() => _inner.GetPorts();
        public IReadOnlyList<PrinterDriverInfo> GetDrivers() => _inner.GetDrivers();
        public IReadOnlyList<FormInfo> GetForms() => _inner.GetForms();
        public void AddForm(FormInfo form) => _inner.AddForm(form);
        public void SetForm(string formName, FormInfo form) => _inner.SetForm(formName, form);
        public void AddStandardTcpIpPort(TcpIpPortRequest request) => _inner.AddStandardTcpIpPort(request);
        public void AddQueue(NewQueueRequest request) => _inner.AddQueue(request);
        public void SetQueueDriver(string queueName, string driverName) => _inner.SetQueueDriver(queueName, driverName);
        public void SetQueuePort(string queueName, string portName) => _inner.SetQueuePort(queueName, portName);
        public DriverCapabilities GetCapabilities(string queueName) => _inner.GetCapabilities(queueName);
        public DevMode? GetDevMode(string queueName, DevModeScope scope) => _inner.GetDevMode(queueName, scope);
        public void SetDevMode(string queueName, DevModeScope scope, DevMode devMode) => _inner.SetDevMode(queueName, scope, devMode);
        public PrintCapabilitiesSummary GetPrintCapabilities(string queueName, DevMode? devMode) => _inner.GetPrintCapabilities(queueName, devMode);
        public PrintTicketMediaInfo DescribePrintTicket(string queueName, DevMode devMode) => _inner.DescribePrintTicket(queueName, devMode);
        public IReadOnlyList<PrintJobInfo> GetJobs(string queueName) => _inner.GetJobs(queueName);
    }
}
