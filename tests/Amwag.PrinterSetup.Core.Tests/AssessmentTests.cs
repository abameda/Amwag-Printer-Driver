using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Core.Units;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class AssessmentTests
{
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    private static SetupPlan Assess(Scenario scenario, SetupMode mode = SetupMode.Setup, params string[] addresses)
    {
        var session = scenario.CreateSession(mode);
        var detection = session.Detect(new DetectRequest { NetworkAddresses = addresses }, default);
        var target = session.SelectAutomatically(detection).Selected ?? throw new InvalidOperationException("No printer was selected.");
        return session.Assess(target, default);
    }

    // ---- Environment blockers ------------------------------------------------------------------------------------

    [Fact]
    public void Stopped_spooler_plans_start_spooler_first_and_the_rest_conservatively()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().WithSpooler(ServiceRunState.Stopped).Build();

        var plan = Assess(scenario);

        Assert.True(plan.CanProceed);
        Assert.Equal(StartSpooler, plan.Actions[0].Kind);
        Assert.True(plan.Actions[0].RequiresElevation);
        Assert.Equal([StartSpooler, PrepareDriverPackage, InstallDriver, CreateQueue, CreateForm, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(plan));
    }

    [Fact]
    public void Stopped_spooler_is_started_and_everything_is_rechecked_at_execution()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped).Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;

        var apply = session.Apply(session.Assess(target, default), ApplyScope.All, null, default);

        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, StartSpooler));
        Assert.All(apply.Results.Where(r => r.Action.Kind != StartSpooler), r => Assert.Equal(ActionOutcome.AlreadySatisfied, r.Outcome));
        Assert.True(scenario.Spooler.ServiceStatus.IsRunning);
        Assert.Equal(ReadinessLevel.Ready, session.Verify(TargetSelector.For(target), default).Readiness);
    }

    [Fact]
    public void Disabled_spooler_is_a_blocker()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Disabled).Build();

        var plan = Assess(scenario);

        Assert.False(plan.CanProceed);
        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.SpoolerDisabled);
        Assert.DoesNotContain(StartSpooler, Kinds(plan));
    }

    [Fact]
    public void Protected_print_mode_is_a_blocker()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false)
            .WithPlatform(p => p with { ProtectedPrintModeEnabled = true, ProtectedPrintModeDetail = "Policy: Configure Windows protected print = Enabled" })
            .Build();

        var plan = Assess(scenario);

        Assert.False(plan.CanProceed);
        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.ProtectedPrintModeEnabled);
    }

    [Fact]
    public void Unsupported_architecture_and_os_are_blockers()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().WithPlatform(p => p with { Architecture = "arm64", Build = 9200 }).Build();

        var plan = Assess(scenario);

        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.UnsupportedArchitecture);
        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.UnsupportedOperatingSystem);
    }

    [Fact]
    public void Unconfirmed_dimensions_block_setup_unless_allowed()
    {
        var builder = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).WithTicket(t => t with { DimensionsConfirmed = false });
        var scenario = builder.Build();

        var blocked = Assess(scenario);
        Assert.Contains(blocked.Blockers, b => b.Code == FindingCode.DimensionsUnconfirmed);

        var session = scenario.CreateSession(o => o with { AllowUnconfirmedDimensions = true });
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        var allowed = session.Assess(target, default);
        Assert.True(allowed.CanProceed);
        Assert.True(Has(allowed.Findings, FindingCode.DimensionsUnconfirmed, Severity.Warning));
        Assert.Equal([ConfigureGlobalDefaults], Kinds(allowed));
    }

    [Fact]
    public void Unapproved_package_blocks_only_when_the_driver_must_be_installed()
    {
        var needsDriver = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPackage(p => p with { Approved = false }).Build();
        Assert.Contains(Assess(needsDriver).Blockers, b => b.Code == FindingCode.DriverPackageNotApproved);

        var hasDriver = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).WithPackage(p => p with { Approved = false }).Build();
        var plan = Assess(hasDriver);
        Assert.True(plan.CanProceed);
        Assert.Equal([ConfigureGlobalDefaults], Kinds(plan));
    }

    // ---- Driver, device and queue --------------------------------------------------------------------------------

    [Fact]
    public void Outdated_driver_is_treated_as_missing()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver(version: new Version(7, 9, 0, 0))
            .WithUsbQueue()
            .WithTicketForm()
            .Build();

        var plan = Assess(scenario);

        Assert.True(Has(plan.Findings, FindingCode.DriverOutdated, Severity.Warning));
        Assert.Equal([PrepareDriverPackage, InstallDriver, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(plan));
    }

    [Fact]
    public void Device_bound_to_a_class_driver_gets_the_approved_driver()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();

        var plan = Assess(scenario);

        Assert.True(Has(plan.Findings, FindingCode.DeviceDriverNotBound, Severity.Warning));
        Assert.Equal([PrepareDriverPackage, BindUsbDevice], Kinds(plan));
        Assert.Equal(SampleConfig.UsbHardwareId(SampleConfig.P4030), plan.Actions[1].Parameters["hardwareId"]);
    }

    [Theory]
    [InlineData(DriverRules.NeedRestartProblemCode, false)] // linked; the accepted driver takes effect once Windows restarts
    [InlineData(10, true)] // CM_PROB_FAILED_START: the device does not work with its driver
    public void Device_on_an_accepted_driver_waiting_for_a_restart_is_not_bound_again(int problemCode, bool bindAgain)
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();
        scenario.Devices.Update(SampleConfig.UsbInstanceId(SampleConfig.P4030), d => d with { ProblemCode = problemCode });

        var plan = Assess(scenario);

        if (bindAgain)
            Assert.Equal([PrepareDriverPackage, BindUsbDevice], Kinds(plan));
        else
            Assert.Empty(plan.Actions);
        Assert.Equal(bindAgain, Has(plan.Findings, FindingCode.DeviceDriverNotBound));
    }

    [Fact]
    public void New_queue_name_avoids_a_name_used_by_another_printer()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithModelDriver()
            .WithQueue(Queue4030, Queue4030, "FILE:")
            .Build();

        var plan = Assess(scenario);

        Assert.Equal("TA P-4030DN KX (P-4030DN)", plan.Actions.Single(a => a.Kind == CreateQueue).Parameters["queueName"]);
    }

    [Fact]
    public void Queue_name_override_and_branch_name_are_used_for_new_queues()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkPrinter("10.20.30.40")
            .WithBranch(new BranchOverride { QueueName = "Branch Tickets" })
            .Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest { NetworkAddresses = ["10.20.30.40"] }, default)).Selected!;

        Assert.Equal("Branch Tickets", session.Assess(target, default).Actions.Single(a => a.Kind == CreateQueue).Parameters["queueName"]);

        var overridden = scenario.CreateSession(o => o with { QueueNameOverride = "IT Override" }).Assess(target, default);
        Assert.Equal("IT Override", overridden.Actions.Single(a => a.Kind == CreateQueue).Parameters["queueName"]);
    }

    [Fact]
    public void Queue_status_problems_are_warnings_never_blockers()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue(status: PrinterStatusFlags.Paused | PrinterStatusFlags.PaperOut | PrinterStatusFlags.Offline)
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();

        var plan = Assess(scenario);

        Assert.True(plan.CanProceed);
        Assert.True(Has(plan.Findings, FindingCode.PrinterOffline, Severity.Warning));
        Assert.True(Has(plan.Findings, FindingCode.PrinterError, Severity.Warning));
        Assert.True(Has(plan.Findings, FindingCode.PrinterPaused, Severity.Warning));
        Assert.True(plan.NothingToDo);
    }

    [Fact]
    public void Repair_never_creates_a_queue()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        var run = scenario.Run(SetupMode.Repair);

        Assert.True(Has(run.Plan!.Findings, FindingCode.QueueMissing, Severity.Error));
        Assert.True(run.Plan.NothingToDo);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Empty(scenario.Installer.Calls);
        Assert.DoesNotContain(scenario.Spooler.Queues, q => q.DriverName.StartsWith("TA ", StringComparison.Ordinal));
    }

    [Fact]
    public void Repair_fixes_an_existing_queue()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();

        var run = scenario.Run(SetupMode.Repair);

        Assert.Equal([ConfigureGlobalDefaults], Kinds(run.Plan!));
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    // ---- Form and media ------------------------------------------------------------------------------------------

    [Fact]
    public void User_form_with_the_wrong_size_is_updated()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithTicketForm(new PaperDimensions(82_300, 170_200))
            .Build();

        var plan = Assess(scenario);

        Assert.True(Has(plan.Findings, FindingCode.FormSizeMismatch, Severity.Warning));
        Assert.Equal([UpdateForm, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(plan));
    }

    [Fact]
    public void User_form_with_a_reduced_imageable_area_is_updated()
    {
        var size = SampleConfig.Ticket;
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithForm(new FormInfo(SampleConfig.FormName, FormKind.User, size.WidthMicrometers, size.HeightMicrometers, 4000, 4000, size.WidthMicrometers - 4000, size.HeightMicrometers - 4000))
            .Build();

        Assert.Contains(UpdateForm, Kinds(Assess(scenario)));
    }

    [Fact]
    public void Builtin_or_printer_form_with_the_ticket_name_and_another_size_is_a_blocker()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithTicketForm(new PaperDimensions(80_000, 160_000), FormKind.Printer)
            .Build();

        var plan = Assess(scenario);

        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.FormSizeMismatch);
    }

    [Fact]
    public void Other_forms_with_the_ticket_size_are_reported()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithForm(FormInfo.UserForm("Old ticket", SampleConfig.Ticket))
            .Build();

        var finding = Assert.Single(Assess(scenario).Findings, f => f.Code == FindingCode.FormSimilarExists);

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("Old ticket", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Ticket_outside_the_driver_range_is_a_blocker_before_the_form_exists()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { MinWidthTenthsOfMillimeter = 900 })
            .Build();

        var plan = Assess(scenario);

        var blocker = Assert.Single(plan.Blockers);
        Assert.Equal(FindingCode.TicketSizeOutsideDriverRange, blocker.Code);
        Assert.Equal("900", blocker.Data["minWidth"]);
        Assert.Equal("printCapabilities", blocker.Data["rangeSource"]);
    }

    [Fact]
    public void Driver_range_comes_from_the_print_capabilities_not_the_extents_of_the_listed_papers()
    {
        // The TA KXv4 driver: DC_MINEXTENT is the narrowest listed paper (an envelope, 92 mm) while its custom range starts at 70 mm.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { ExtentsFromListedPapers = true, BuiltInPapers = [new(38, "6 3/4 Envelope", 920, 1651), .. p.BuiltInPapers] })
            .Build();
        Assert.Equal(920, scenario.Spooler.GetCapabilities(Queue4030).MinWidthTenthsOfMillimeter);

        var run = scenario.Run();

        Assert.DoesNotContain(run.Plan!.Blockers, b => b.Code == FindingCode.TicketSizeOutsideDriverRange);
        Assert.Contains(run.Plan.Actions, a => a.Kind == CreateForm);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Without_print_capabilities_the_driver_range_falls_back_to_the_extents()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with
            {
                ExtentsFromListedPapers = true,
                PrintCapabilitiesAvailable = false,
                BuiltInPapers = [new(38, "6 3/4 Envelope", 920, 1651), .. p.BuiltInPapers],
            })
            .Build();

        var blocker = Assert.Single(Assess(scenario).Blockers);

        Assert.Equal(FindingCode.TicketSizeOutsideDriverRange, blocker.Code);
        Assert.Equal("920", blocker.Data["minWidth"]);
        Assert.Equal("deviceCapabilities", blocker.Data["rangeSource"]);
    }

    [Fact]
    public void Ticket_outside_the_multipurpose_tray_range_is_a_blocker()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithModel(SampleConfig.P4030, m => m with { MultipurposeTrayRange = new PaperRangeMm { MinWidth = 90, MinHeight = 148, MaxWidth = 216, MaxHeight = 356 } })
            .Build();

        Assert.Contains(Assess(scenario).Blockers, b => b.Code == FindingCode.TicketSizeOutsideTrayRange);
    }

    [Fact]
    public void Ticket_outside_the_cassette_range_is_information_when_the_mp_tray_is_required()
    {
        var plan = Assess(new ScenarioBuilder().WithInstalledUsbPrinter().Build());

        var finding = Assert.Single(plan.Findings, f => f.Code == FindingCode.TicketSizeOutsideTrayRange);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("cassette", finding.Data["tray"]);

        var notRequired = Assess(new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithModel(SampleConfig.P4030, m => m with { TicketPaperSource = m.TicketPaperSource with { RequiredForTicket = false } })
            .Build());
        Assert.Equal(Severity.Warning, Assert.Single(notRequired.Findings, f => f.Code == FindingCode.TicketSizeOutsideTrayRange).Severity);
    }

    [Fact]
    public void Missing_mp_tray_blocks_when_required_and_warns_otherwise()
    {
        static ScenarioBuilder Builder() => new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { Bins = [new BinCapability(DmBin.Auto, "Auto"), new BinCapability(257, "Cassette 1")] });

        Assert.Contains(Assess(Builder().Build()).Blockers, b => b.Code == FindingCode.PaperSourceNotFound);

        var optional = Assess(Builder().WithModel(SampleConfig.P4030, m => m with { TicketPaperSource = m.TicketPaperSource with { RequiredForTicket = false } }).Build());
        Assert.True(optional.CanProceed);
        Assert.True(Has(optional.Findings, FindingCode.PaperSourceNotFound, Severity.Warning));

        var driverDefault = Assess(Builder().WithConfig(c => c with { PrintPreferences = c.PrintPreferences with { PaperSource = PaperSourcePolicy.DriverDefault } }).Build());
        Assert.True(driverDefault.CanProceed);
        Assert.False(Has(driverDefault.Findings, FindingCode.PaperSourceNotFound));
    }

    [Fact]
    public void Mp_tray_found_by_name_when_the_id_differs()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { Bins = [new BinCapability(DmBin.Auto, "Auto"), new BinCapability(270, " mp TRAY ")] })
            .Build();

        var run = scenario.Run();

        Assert.True(run.Plan!.CanProceed);
        Assert.Equal(270, scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!.DefaultSource);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    // ---- Defaults ------------------------------------------------------------------------------------------------

    [Fact]
    public void Wrong_global_defaults_list_the_differing_fields()
    {
        var plan = Assess(new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build());

        var finding = Assert.Single(plan.Findings, f => f.Code == FindingCode.GlobalDefaultsIncorrect);
        Assert.Equal("paper,duplex,paperSource", finding.Data["fields"]);
    }

    [Fact]
    public void Duplex_is_not_managed_when_the_driver_has_no_duplex_unit()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { SupportsDuplex = false })
            .Build();
        var global = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        global.Duplex = DmDuplex.Vertical;
        scenario.Spooler.SetStoredDevMode(Queue4030, DevModeScope.Global, global);

        var run = scenario.Run();

        Assert.True(run.Plan!.NothingToDo);
        var verification = run.Verification!;
        Assert.Equal(CheckStatus.NotApplicable, Check(verification, VerificationItem.DefaultDuplex).Status);
        Assert.Equal(ReadinessLevel.Ready, verification.Readiness);
    }

    [Fact]
    public void Defaults_are_planned_unconditionally_when_the_queue_will_be_created()
    {
        var plan = Assess(new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithTicketForm().Build());

        Assert.Contains(ConfigureGlobalDefaults, Kinds(plan));
        Assert.Contains(ConfigureUserDefaults, Kinds(plan));
        Assert.False(Has(plan.Findings, FindingCode.GlobalDefaultsIncorrect));
    }

    // ---- Modes and invariants ------------------------------------------------------------------------------------

    [Fact]
    public void Diagnostics_plans_nothing_but_reports_everything()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).WithPerUserDefaults(Queue4030, d => d.Scale = 90).Build();

        var plan = Assess(scenario, SetupMode.Diagnostics);

        Assert.Empty(plan.Actions);
        Assert.Equal(SetupMode.Diagnostics, plan.Mode);
        Assert.True(Has(plan.Findings, FindingCode.GlobalDefaultsIncorrect, Severity.Warning));
        Assert.True(Has(plan.Findings, FindingCode.UserDefaultsIncorrect, Severity.Warning));
        Assert.True(Has(plan.Findings, FindingCode.DriverListsForm, Severity.Info));
    }

    [Fact]
    public void Diagnostics_reports_what_setup_would_need_and_what_would_block_it()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPackage(p => p with { Approved = false }).Build();

        var plan = Assess(scenario, SetupMode.Diagnostics);

        Assert.Empty(plan.Actions);
        Assert.True(Has(plan.Findings, FindingCode.QueueMissing, Severity.Warning));
        Assert.True(Has(plan.Findings, FindingCode.DriverMissing, Severity.Warning));
        Assert.True(Has(plan.Findings, FindingCode.DeviceDriverNotBound, Severity.Warning));
        Assert.True(Has(plan.Findings, FindingCode.FormMissing, Severity.Warning));
        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.DriverPackageNotApproved);
    }

    [Fact]
    public void Application_override_notice_is_added_exactly_once()
    {
        var plan = Assess(new ScenarioBuilder().WithInstalledUsbPrinter().Build());

        Assert.Single(plan.Findings, f => f.Code == FindingCode.ApplicationMayOverrideDefaults);
    }

    [Fact]
    public void Probable_target_is_blocked_until_its_model_is_confirmed()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(new NetworkIdentity { Address = "10.20.30.40", RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-4030DN" })
            .Build();
        var session = scenario.CreateSession();
        var probable = Assert.Single(session.Detect(new DetectRequest { NetworkAddresses = ["10.20.30.40"] }, default).Candidates);

        var plan = session.Assess(probable, default);

        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.ProbableMatchRequiresConfirmation);
        Assert.DoesNotContain(session.Apply(plan, ApplyScope.All, null, default).Results, r => r.Outcome != ActionOutcome.Skipped);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Assessment_never_writes()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithInstalledUsbPrinter(SampleConfig.P5032, "USB002", correctDefaults: false).Build();
        var session = scenario.CreateSession();

        foreach (var candidate in session.Detect(new DetectRequest(), default).Candidates)
            session.Assess(candidate, default);

        Assert.Empty(scenario.Spooler.Writes);
        Assert.Empty(scenario.Installer.Calls);
        Assert.Empty(scenario.Packages.PreparedPackageIds);
    }
}
