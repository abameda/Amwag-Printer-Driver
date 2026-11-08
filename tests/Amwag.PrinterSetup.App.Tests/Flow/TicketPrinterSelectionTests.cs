using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>
/// Which printer a run is about: Repair changes and Diagnostics judges only the ticket printer (another supported TA printer
/// may print A4 for the office), a USB printer known only from an earlier connection is left out next to a connected one of
/// the same model, and a printer whose model nobody confirmed is reported as such (silent runs stop for IT with exit code 4).
/// </summary>
public sealed class TicketPrinterSelectionTests
{
    private const string Address = "10.0.0.50";
    private const string OfficeAddress = "10.0.0.60";
    private const string OfficeQueue = "Office A4";
    private const string OldQueue = "Old ticket printer";

    private static readonly string TicketQueue = SampleConfig.DriverName(SampleConfig.P4030);
    private static readonly string TicketKey = PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030));
    private static readonly string OldUnitKey = PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"));

    private readonly UpdateCollector _updates = new();

    // ---- Repair: exactly one printer -------------------------------------------------------------------------------

    [Fact]
    public async Task Repair_with_the_branch_model_changes_only_the_ticket_printer()
    {
        Scenario scenario = TicketAndNetworkOfficePrinter(ticketConfigured: false, new BranchOverride { ModelId = SampleConfig.P4030 });
        DevMode officeBefore = scenario.Spooler.GetStoredDevMode(OfficeQueue, DevModeScope.Global)!;
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction();
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Empty(interaction.Calls);
        Assert.NotEmpty(broker.Requests);
        Assert.All(broker.Requests, r => Assert.Equal(TicketKey, r.Target.Key));
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == OfficeQueue);
        DevMode officeAfter = scenario.Spooler.GetStoredDevMode(OfficeQueue, DevModeScope.Global)!;
        Assert.Equal((officeBefore.FormName, officeBefore.DefaultSource, officeBefore.Duplex), (officeAfter.FormName, officeAfter.DefaultSource, officeAfter.Duplex));
        Assert.Equal(TicketKey, result.Target?.Key);
        Assert.Equal(TicketQueue, result.Verification?.Summary.QueueName);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Info } && f.Data.GetValueOrDefault("queue") == OfficeQueue);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task Repair_does_not_rewrite_the_office_printer_when_the_ticket_printer_has_no_queue_yet()
    {
        // The branch model names the USB printer, which Windows has not set up yet: Repair cannot repair it and must not choose
        // the office printer in its place; Diagnostics judges the USB printer, so its Repair button cannot reach the office queue.
        static Scenario State() => new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithModelDriver(SampleConfig.P5032)
            .WithNetworkQueue(OfficeAddress, SampleConfig.P5032, name: OfficeQueue)
            .WithNetworkPrinter(OfficeAddress, SampleConfig.P5032)
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .Build();
        Scenario repairScenario = State();
        var repair = repairScenario.Environment(SetupMode.Repair);
        Scenario diagnosticsScenario = State();
        var diagnostics = diagnosticsScenario.Environment(SetupMode.Diagnostics);

        FlowResult repaired = await new SetupFlow(repair, new SilentInteraction(CommandLineParser.Parse(["--silent", "--mode", "repair"])), new InProcessBroker(repair))
            .RunAsync(_updates, CancellationToken.None);
        FlowResult diagnosed = await Run(diagnostics, new ScriptedInteraction(), new InProcessBroker(diagnostics));

        repairScenario.AssertNothingChanged();
        Assert.Equal(ExitCodes.NoSupportedPrinter, repaired.ExitCode);
        Assert.Contains(repaired.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound && f.Data.GetValueOrDefault("target") == TicketKey);
        Assert.Equal(TicketKey, diagnosed.Target?.Key);
        Assert.Equal(FlowOutcome.NotReady, diagnosed.Outcome);
    }

    [Fact]
    public async Task Diagnostics_does_not_judge_another_printer_when_nothing_is_at_the_configured_address()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithBranch(new BranchOverride { PrinterAddress = "10.9.9.9" }).Build();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal((FlowOutcome.NotReady, ExitCodes.NotReady), (result.Outcome, result.ExitCode));
        Assert.Null(result.Verification);
        Assert.Single(result.Verifications);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error } && f.Data.GetValueOrDefault("address") == "10.9.9.9");
    }

    [Fact]
    public async Task Diagnostics_reports_a_stopped_spooler_as_blocked()
    {
        Scenario scenario = new ScenarioBuilder().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).WithInstalledUsbPrinter().Build();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal((FlowOutcome.Blocked, ExitCodes.SpoolerUnavailable), (result.Outcome, result.ExitCode));
        Assert.False(scenario.Spooler.ServiceStatus.IsRunning);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public async Task Diagnostics_reports_protected_print_mode_as_blocked_although_the_queue_checks_out()
    {
        Scenario scenario = new ScenarioBuilder().WithPlatform(p => p with { ProtectedPrintModeEnabled = true }).WithInstalledUsbPrinter().Build();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal((FlowOutcome.Blocked, ExitCodes.Blocked), (result.Outcome, result.ExitCode));
        Assert.NotNull(result.Verification);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public async Task Declining_the_model_of_a_printer_picked_from_the_choice_offers_the_others_again()
    {
        // A front-desk queue on a class driver whose printer only probably is a P-4030DN, next to a connected USB P-4030DN.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(Address, name: "Front desk", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(ProbableIdentity())
            .Build();
        var environment = scenario.Environment();
        var offered = new List<IReadOnlyList<PrinterCandidate>>();
        var interaction = new ScriptedInteraction
        {
            Choose = choices =>
            {
                offered.Add(choices);
                return offered.Count == 1 ? choices.First(c => c.NeedsConfirmation) : choices.Single();
            },
            Confirm = _ => false,
        };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(new[] { "choose", "confirm", "choose" }, interaction.Calls);
        Assert.Equal(TicketKey, Assert.Single(offered[1]).Key);
        Assert.Equal(TicketKey, Assert.Single(broker.Requests).Target.Key);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task Repair_picks_the_only_queue_already_set_up_with_the_ticket_paper_without_asking()
    {
        Scenario scenario = TicketAndUsbOfficePrinter();
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction();

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Empty(interaction.Calls);
        Assert.Equal(TicketKey, result.Target?.Key);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == OfficeQueue);
        Assert.Single(result.Verifications);
    }

    [Fact]
    public async Task Silent_repair_with_several_queues_and_no_preference_needs_a_selection_and_changes_nothing()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: false)
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, new SilentInteraction(CommandLineParser.Parse(["--silent", "--mode", "repair"])), broker);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task The_repair_button_after_setup_repairs_only_the_printer_that_was_set_up()
    {
        // Both queues already use the ticket form, so without an explicit target Repair would have to ask.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();
        string chosenQueue = SampleConfig.DriverName(SampleConfig.P4531);
        var setupInteraction = new ScriptedInteraction { Choose = choices => choices.Single(c => c.Model?.Id == SampleConfig.P4531) };
        FlowResult setup = await Run(scenario.Environment(), setupInteraction, new InProcessBroker(scenario.Environment()));
        DevMode drifted = scenario.Spooler.GetStoredDevMode(chosenQueue, DevModeScope.Global)!;
        drifted.Scale = 50;
        scenario.Spooler.SetStoredDevMode(chosenQueue, DevModeScope.Global, drifted);
        int writesBefore = scenario.Spooler.Writes.Count;

        var repairEnvironment = scenario.Environment(SetupMode.Repair) with { RepairTarget = MainForm.RepairTargetOf(setup) };
        var repairInteraction = new ScriptedInteraction();
        var broker = new InProcessBroker(repairEnvironment);
        FlowResult repair = await Run(repairEnvironment, repairInteraction, broker);

        Assert.Empty(repairInteraction.Calls);
        Assert.Equal(setup.Target!.Key, repair.Target?.Key);
        WorkerRequest request = Assert.Single(broker.Requests);
        Assert.Equal(setup.Target.Key, request.Target.Key);
        IEnumerable<SpoolerWrite> repairWrites = scenario.Spooler.Writes.Skip(writesBefore).ToList();
        Assert.Contains(repairWrites, w => w is { Operation: nameof(IPrintSpooler.SetDevMode) } && w.Target == chosenQueue);
        Assert.DoesNotContain(repairWrites, w => w.Target == SampleConfig.DriverName(SampleConfig.P4030));
        Assert.True(repair.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(repair));
    }

    [Fact]
    public async Task Repair_of_a_chosen_printer_that_is_gone_changes_nothing()
    {
        Scenario scenario = TicketAndUsbOfficePrinter();
        var environment = scenario.Environment(SetupMode.Repair) with
        {
            RepairTarget = new TargetSelector(PrinterCandidate.UsbKey(@"USBPRINT\NOT\THERE"), SampleConfig.P4030),
        };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.NoPrinter, result.Outcome);
        Finding finding = Assert.Single(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        Assert.Equal(PrinterCandidate.UsbKey(@"USBPRINT\NOT\THERE"), finding.Data.GetValueOrDefault("target"));
        Assert.Equal("setup", finding.Data.GetValueOrDefault("suggestedMode"));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    // ---- Diagnostics: the outcome is the ticket printer's -----------------------------------------------------------

    [Fact]
    public async Task Diagnostics_follows_the_ticket_printer_and_still_checks_the_others()
    {
        Scenario scenario = TicketAndUsbOfficePrinter();

        FlowResult result = await Run(scenario.Environment(SetupMode.Diagnostics), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
        Assert.True(result.ExitCode is ExitCodes.Ready or ExitCodes.ReadyWithWarnings);
        Assert.Equal(TicketKey, result.Target?.Key);
        Assert.Equal(TicketKey, result.Verification?.TargetKey);
        Assert.Equal(2, result.Verifications.Count);
        Assert.Equal(ReadinessLevel.NotReady, Assert.Single(result.Verifications, v => v.TargetKey != TicketKey).Readiness);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_without_a_known_ticket_printer_warns_instead_of_judging_by_another_printer()
    {
        // Both queues are set up with the ticket paper: nothing says which one prints tickets.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();

        FlowResult result = await Run(scenario.Environment(SetupMode.Diagnostics), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.ReadyWithWarnings, result.Outcome);
        Assert.Equal(ExitCodes.ReadyWithWarnings, result.ExitCode);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.Null(result.Target);
        Assert.Null(result.Verification);
        Assert.Equal(2, result.Verifications.Count);
    }

    [Fact]
    public async Task Diagnostics_without_a_known_ticket_printer_and_nothing_ready_is_not_ready()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: false)
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .Build();

        FlowResult result = await Run(scenario.Environment(SetupMode.Diagnostics), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
    }

    // ---- USB printers known only from an earlier connection --------------------------------------------------------

    [Fact]
    public async Task Diagnostics_leaves_out_a_usb_printer_known_only_from_an_earlier_connection()
    {
        Scenario control = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithUsbPrinter(SampleConfig.P4030, usbPort: null, present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .Build();

        FlowResult expected = await new SetupFlow(control.Environment(SetupMode.Diagnostics), new ScriptedInteraction(), new InProcessBroker(control.Environment()))
            .RunAsync(new UpdateCollector(), CancellationToken.None);
        FlowResult result = await Run(scenario.Environment(SetupMode.Diagnostics), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.True(expected.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(expected));
        Assert.Equal((expected.Outcome, expected.ExitCode), (result.Outcome, result.ExitCode));
        Assert.Equal(TicketKey, Assert.Single(result.Verifications).TargetKey);
        Finding leftOut = Assert.Single(result.Findings, LeftOut);
        Assert.Equal(OldUnitKey, leftOut.Data.GetValueOrDefault("target"));
    }

    [Fact]
    public async Task Diagnostics_leaves_out_an_earlier_usb_unit_that_still_has_a_queue()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: OldQueue)
            .Build();

        FlowResult result = await Run(scenario.Environment(SetupMode.Diagnostics), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
        Assert.Equal(TicketKey, Assert.Single(result.Verifications).TargetKey);
        Finding leftOut = Assert.Single(result.Findings, LeftOut);
        Assert.Equal((OldUnitKey, OldQueue), (leftOut.Data.GetValueOrDefault("target"), leftOut.Data.GetValueOrDefault("queue")));
    }

    [Fact]
    public async Task A_usb_printer_known_only_from_an_earlier_connection_is_still_checked_when_it_is_the_only_one()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: OldQueue)
            .Build();

        FlowResult result = await Run(scenario.Environment(SetupMode.Diagnostics), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.Equal(OldUnitKey, Assert.Single(result.Verifications).TargetKey);
        Assert.Equal(OldUnitKey, result.Target?.Key);
        Assert.DoesNotContain(result.Findings, LeftOut);
    }

    [Fact]
    public async Task Repair_leaves_an_earlier_usb_unit_alone_and_repairs_the_connected_printer()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: OldQueue)
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction();

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Empty(interaction.Calls);
        Assert.Equal(TicketKey, result.Target?.Key);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == OldQueue);
        Assert.Equal(OldUnitKey, Assert.Single(result.Findings, LeftOut).Data.GetValueOrDefault("target"));
    }

    // ---- A model nobody confirmed ----------------------------------------------------------------------------------

    [Fact]
    public async Task Silent_setup_with_only_a_probable_match_stops_for_IT_to_confirm_the_model()
    {
        Scenario scenario = ProbableNetworkPrinter();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address])), broker);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Finding finding = Assert.Single(result.Findings, f => f.Code == FindingCode.ProbableMatchRequiresConfirmation);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Equal((Address, SampleConfig.P4030), (finding.Data.GetValueOrDefault("address"), finding.Data.GetValueOrDefault("model")));
        Assert.Contains($"--address {Address} --model {SampleConfig.P4030} --confirm-model", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Silent_setup_with_only_an_unidentified_printer_stops_for_IT_to_name_the_model()
    {
        Scenario scenario = new ScenarioBuilder().WithNetworkPrinter(Address, modelId: null).Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });

        FlowResult result = await Run(environment, new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address])), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Finding finding = Assert.Single(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Equal(Address, finding.Data.GetValueOrDefault("address"));
        Assert.Contains($"--address {Address} --model <{string.Join("|", SampleConfig.ModelIds)}> --confirm-model", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task An_operators_no_to_a_probable_match_is_recorded_and_ends_without_a_printer()
    {
        Scenario scenario = ProbableNetworkPrinter();
        var interaction = new ScriptedInteraction { Confirm = _ => false, Address = _ => null };
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Equal(FlowOutcome.NoPrinter, result.Outcome);
        Assert.Equal(ExitCodes.NoSupportedPrinter, result.ExitCode);
        Finding answer = Assert.Single(result.Findings, f => f.Code == FindingCode.ProbableMatchRequiresConfirmation);
        Assert.Equal(Severity.Info, answer.Severity);
        Assert.Equal(Address, answer.Data.GetValueOrDefault("address"));
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_with_only_a_probable_match_says_its_model_must_be_confirmed()
    {
        Scenario scenario = ProbableNetworkPrinter();
        var environment = scenario.Environment(SetupMode.Diagnostics, o => o with { PreferredAddress = Address });

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error } && f.Data.GetValueOrDefault("address") == Address);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_does_not_judge_by_another_printer_when_the_printer_IT_named_needs_its_model_confirmed()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithNetworkIdentity(ProbableIdentity())
            .Build();
        var environment = scenario.Environment(SetupMode.Diagnostics, o => o with { PreferredAddress = Address });

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Null(result.Target);
        Assert.Equal(TicketKey, Assert.Single(result.Verifications).TargetKey);
    }

    // ---- Scenarios ---------------------------------------------------------------------------------------------------

    /// <summary>The ticket printer (P-4030DN on USB001) and an office P-5032DN on the network whose queue prints A4.</summary>
    private static Scenario TicketAndNetworkOfficePrinter(bool ticketConfigured, BranchOverride? branch) =>
        new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: ticketConfigured)
            .WithModelDriver(SampleConfig.P5032)
            .WithNetworkQueue(OfficeAddress, SampleConfig.P5032, name: OfficeQueue)
            .WithNetworkPrinter(OfficeAddress, SampleConfig.P5032)
            .WithBranch(branch)
            .Build();

    /// <summary>The ticket printer set up with the ticket paper, and an office P-4531DN on USB002 with its A4 defaults.</summary>
    private static Scenario TicketAndUsbOfficePrinter() =>
        new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4531, "USB002")
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB002", name: OfficeQueue)
            .Build();

    private static NetworkIdentity ProbableIdentity() => new()
    {
        Address = Address,
        RawPortReachable = true,
        SnmpResponded = true,
        SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer",
    };

    private static Scenario ProbableNetworkPrinter() => new ScenarioBuilder().WithNetworkIdentity(ProbableIdentity()).Build();

    /// <summary>The run's own record of a printer it left out (detection may also report the device as not connected).</summary>
    private static bool LeftOut(Finding finding) =>
        finding is { Code: FindingCode.DeviceNotConnected, Severity: Severity.Info } && finding.Data.ContainsKey("target");

    private async Task<FlowResult> Run(FlowEnvironment environment, ISetupInteraction interaction, InProcessBroker broker) =>
        await new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

    private static string Describe(FlowResult result) =>
        $"Outcome {result.Outcome}; findings: " + string.Join("; ", result.Findings.Where(f => f.Severity != Severity.Info).Select(f => $"{f.Code}: {f.Detail}"));
}
