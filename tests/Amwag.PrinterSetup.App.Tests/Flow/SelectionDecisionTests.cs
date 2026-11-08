using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>
/// Decisions nobody may take silently. Several printers whose model must be confirmed are a choice first. IT's model over an
/// old queue driver applies in Repair as in Setup, and a model the printer contradicts stops the run for IT. Repair confirms a
/// probable printer that has the only queue, and offers the switched-off ticket unit again. An operator who declines every
/// printer offered may enter the ticket printer's address. The configured model picks no printer while another printer may be
/// of that model. Diagnostics follows the same choices.
/// </summary>
public sealed class SelectionDecisionTests
{
    private const string Address = "10.20.30.40";
    private const string OldQueue = "Old ticket printer";

    private readonly UpdateCollector _updates = new();

    private static NetworkIdentity Probable(string address, string model) =>
        new() { Address = address, RawPortReachable = true, SnmpResponded = true, SysDescr = $"TA Triumph-Adler {model} Series Network Printer" };

    private static SilentInteraction Silent(params string[] args) => new(CommandLineParser.Parse(["--silent", .. args]));

    private Task<FlowResult> Run(FlowEnvironment environment, ISetupInteraction interaction, InProcessBroker broker) =>
        new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

    // ---- Several printers whose model must be confirmed ----------------------------------------------------------------

    /// <summary>An office and a ticket P-4030DN, both on class-driver queues, both reporting only a descriptive (probable) identity.</summary>
    private static Scenario TwoProbablePrintersOfOneModel() => new ScenarioBuilder()
        .WithModelDriver(SampleConfig.P4030)
        .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
        .WithNetworkQueue("10.0.0.40", name: "A4 office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
        .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
        .WithNetworkIdentity(Probable("10.0.0.40", SampleConfig.P4030))
        .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
        .Build();

    [Theory]
    [InlineData(SetupMode.Setup)]
    [InlineData(SetupMode.Repair)]
    public async Task Several_printers_whose_model_must_be_confirmed_are_a_choice_and_only_the_chosen_one_is_confirmed(SetupMode mode)
    {
        Scenario scenario = TwoProbablePrintersOfOneModel();
        var environment = scenario.Environment(mode);
        var interaction = new ScriptedInteraction
        {
            Choose = choices => choices.Single(c => c.Queue?.Name == "Tickets"),
            Confirm = _ => true,
        };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(["choose", "confirm"], interaction.Calls);
        Assert.Equal(PrinterCandidate.NetworkKey("10.0.0.50"), Assert.Single(broker.Requests).Target.Key);
        Assert.Contains(scenario.Spooler.Writes, w => w is { Target: "Tickets", Operation: nameof(IPrintSpooler.SetDevMode) });
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == "A4 office");
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
    }

    [Fact]
    public async Task Silent_confirm_model_confirms_neither_of_two_probable_queue_hosts_at_the_address()
    {
        const string Ip = "10.12.0.50";
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("printer12.branch.local", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("printer12", name: "A4 office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable(Ip, SampleConfig.P4030))
            .WithNetworkAlias("printer12.branch.local", Ip)
            .WithNetworkAlias("printer12", Ip)
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Ip, PreferredModelId = SampleConfig.P4030 });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--address", Ip, "--model", SampleConfig.P4030, "--confirm-model"), broker);

        // IT's address names the printer, not one of its two queues: a decision nobody made.
        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    // ---- IT's model (--address, --model, --confirm-model) ----------------------------------------------------------------

    [Fact]
    public async Task Silent_repair_applies_IT_s_model_over_the_old_queue_driver_at_the_address()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets")
            .WithNetworkPrinter(Address, modelId: null, snmp: false)
            .WithTicketForm()
            .Build();
        var environment = scenario.Environment(SetupMode.Repair, o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4531 });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--mode", "repair", "--address", Address, "--model", SampleConfig.P4531, "--confirm-model"), broker);

        WorkerRequest request = Assert.Single(broker.Requests);
        Assert.Equal((SampleConfig.P4531, true), (request.Target.ModelId, request.Target.ModelAssertedByUser));
        Assert.Contains(scenario.Spooler.Writes, w => w is { Target: "Tickets", Operation: nameof(IPrintSpooler.SetQueueDriver) });
        Assert.True(result.ExitCode is ExitCodes.Ready or ExitCodes.ReadyWithWarnings, result.ExitCode.ToString());
    }

    [Theory]
    [InlineData("exact", "setup")]
    [InlineData("exact", "repair")]
    [InlineData("probes", "setup")]
    [InlineData("probes", "repair")]
    public async Task A_model_the_printer_contradicts_stops_the_run_for_IT_and_names_both_models(string identity, string mode)
    {
        // The queue has the P-4030DN driver; the printer reports P-4030DN exactly, or its description names P-4030DN.
        ScenarioBuilder builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets")
            .WithTicketForm();
        Scenario scenario = (identity == "exact" ? builder.WithNetworkPrinter(Address, SampleConfig.P4030) : builder.WithNetworkIdentity(Probable(Address, SampleConfig.P4030))).Build();
        var environment = scenario.Environment(mode == "repair" ? SetupMode.Repair : SetupMode.Setup, o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4531 });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--mode", mode, "--address", Address, "--model", SampleConfig.P4531, "--confirm-model"), broker);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Finding refused = Assert.Single(result.Findings, f => f is { Code: FindingCode.NetworkIdentityMismatch, Severity: Severity.Error });
        Assert.Contains(SampleConfig.P4531, refused.Detail, StringComparison.Ordinal);
        Assert.Contains("reports " + SampleConfig.P4030, refused.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_probable_printer_of_another_model_than_IT_s_stops_the_run_and_names_both_models()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkIdentity(Probable(Address, SampleConfig.P4030))
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4531 });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--address", Address, "--model", SampleConfig.P4531, "--confirm-model"), broker);

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Finding refused = Assert.Single(result.Findings, f => f is { Code: FindingCode.NetworkIdentityMismatch, Severity: Severity.Error });
        Assert.Contains("reports " + SampleConfig.P4030, refused.Detail, StringComparison.Ordinal);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task In_the_window_a_refused_model_from_IT_is_not_followed_by_address_prompts()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets")
            .WithNetworkIdentity(Probable(Address, SampleConfig.P4030))
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });
        var interaction = new ScriptedInteraction
        {
            Asserted = (candidate, models) => candidate.IsAtAddress(Address) ? models.Single(m => m.Id == SampleConfig.P4531) : null,
            Address = _ => Address,
        };

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Empty(interaction.Calls);
        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.NetworkIdentityMismatch, Severity: Severity.Error });
        scenario.AssertNothingChanged();
    }

    // ---- The operator declines the printers offered ----------------------------------------------------------------------

    [Fact]
    public async Task An_operator_who_declines_the_printers_offered_may_enter_the_ticket_printer_s_address()
    {
        // The only printer Windows knows is an office P-4531DN; the ticket printer is a network P-4030DN never set up here.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4531, correctDefaults: false)
            .WithNetworkPrinter("10.0.0.50", SampleConfig.P4030)
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .Build();
        var environment = scenario.Environment();
        var interaction = new ScriptedInteraction { Choose = _ => null, Address = _ => "10.0.0.50" };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(["choose", "address"], interaction.Calls);
        Assert.True(Assert.Single(interaction.Prompts).OfferedPrintersDeclined);
        Assert.Equal(PrinterCandidate.NetworkKey("10.0.0.50"), Assert.Single(broker.Requests).Target.Key);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        // The declined choice is history once the operator named the printer.
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.Cancelled);
    }

    // ---- The configured model ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_configured_model_picks_no_printer_while_a_probable_printer_may_be_of_that_model()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Front desk", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .Build();
        var environment = scenario.Environment();
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent(), broker);

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    /// <summary>
    /// 'Tickets' has the driver of <paramref name="queueModel"/>, but the device on its host reports an unsupported replacement,
    /// so the queue is rejected; an office USB P-4030DN is connected and the branch names P-4030DN.
    /// </summary>
    private static Scenario RejectedQueueNextToTheConfiguredModel(string queueModel) => new ScenarioBuilder()
        .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
        .WithModelDriver(queueModel)
        .WithNetworkQueue("10.0.0.50", queueModel, name: "Tickets")
        .WithNetworkIdentity(new NetworkIdentity { Address = "10.0.0.50", RawPortReachable = true, SnmpResponded = true, Ieee1284DeviceId = "MFG:UTAX_TA;MDL:P-4532DN;CMD:PCL;" })
        .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
        .Build();

    [Fact]
    public async Task The_configured_model_picks_no_printer_while_a_rejected_queue_has_that_model_s_driver()
    {
        Scenario scenario = RejectedQueueNextToTheConfiguredModel(SampleConfig.P4030);
        var session = scenario.CreateSession();
        DetectionResult detection = session.Detect(new DetectRequest(), default);
        var environment = scenario.Environment();
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent(), broker);

        Assert.Equal(["Tickets"], detection.RejectedQueues);
        Assert.Equal(SelectionReason.MultipleCandidates, session.SelectAutomatically(detection).Reason);
        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public void The_configured_model_still_picks_its_printer_next_to_a_rejected_queue_of_another_model()
    {
        Scenario scenario = RejectedQueueNextToTheConfiguredModel(SampleConfig.P4531);
        var session = scenario.CreateSession();
        DetectionResult detection = session.Detect(new DetectRequest(), default);

        CandidateSelection selection = session.SelectAutomatically(detection);

        Assert.Equal(["Tickets"], detection.RejectedQueues);
        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.StartsWith("usb:", selection.Selected?.Key, StringComparison.Ordinal);
    }

    // ---- Repair ------------------------------------------------------------------------------------------------------------

    /// <summary>A connected USB P-4531DN without a queue, and class-driver queue 'Tickets' whose printer is probably a P-4030DN.</summary>
    private static Scenario UsbWithoutQueueAndProbableTicketQueue() => new ScenarioBuilder()
        .WithUsbPrinter(SampleConfig.P4531, driverBound: false)
        .WithModelDriver(SampleConfig.P4030)
        .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
        .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
        .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
        .Build();

    [Fact]
    public async Task Silent_repair_stops_for_IT_when_the_only_queue_belongs_to_a_printer_whose_model_must_be_confirmed()
    {
        Scenario scenario = UsbWithoutQueueAndProbableTicketQueue();
        var environment = scenario.Environment(SetupMode.Repair);

        FlowResult result = await Run(environment, Silent("--mode", "repair"), new InProcessBroker(environment));

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Finding unconfirmed = Assert.Single(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Contains($"--address 10.0.0.50 --model {SampleConfig.P4030} --confirm-model", unconfirmed.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Repair_in_the_window_confirms_the_printer_with_the_only_queue_and_repairs_it()
    {
        Scenario scenario = UsbWithoutQueueAndProbableTicketQueue();
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction { Confirm = _ => true };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(["confirm"], interaction.Calls);
        Assert.Equal(PrinterCandidate.NetworkKey("10.0.0.50"), Assert.Single(broker.Requests).Target.Key);
        Assert.Contains(scenario.Spooler.Writes, w => w is { Target: "Tickets", Operation: nameof(IPrintSpooler.SetDevMode) });
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
    }

    [Fact]
    public async Task Repair_names_setup_in_the_switches_for_a_printer_without_a_queue()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkIdentity(Probable(Address, SampleConfig.P4030))
            .WithBranch(new BranchOverride { PrinterAddress = Address })
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);

        FlowResult result = await Run(environment, Silent("--mode", "repair"), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Finding unconfirmed = Assert.Single(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Contains($"--mode setup --address {Address} --model {SampleConfig.P4030} --confirm-model", unconfirmed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_offers_the_switched_off_ticket_unit_when_the_connected_identical_unit_has_no_queue()
    {
        Scenario scenario = new ScenarioBuilder()
            // An identical office printer, connected; Windows has not set it up (no driver bound, no queue).
            .WithUsbPrinter(SampleConfig.P4030, "USB001", driverBound: false)
            // The ticket printer: switched off, its queue set up with the ticket paper.
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: OldQueue)
            .WithTicketForm()
            .WithCorrectDefaults(OldQueue)
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);
        IReadOnlyList<PrinterCandidate>? offered = null;
        var interaction = new ScriptedInteraction
        {
            Choose = choices =>
            {
                offered = choices;
                return null;
            },
        };

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Equal(["choose"], interaction.Calls);
        Assert.Equal(OldQueue, Assert.Single(offered!).Queue?.Name);
        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        // The operator saw the choice and chose none: the stop leads, the note about the earlier unit stays for the details.
        Assert.Equal(FindingCode.Cancelled, result.Findings.First(f => f.Severity != Severity.Info).Code);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Info } && f.Detail.Contains($"Queue '{OldQueue}'", StringComparison.Ordinal));
        AssertNoLeftOutNote(result);
        scenario.AssertNothingChanged();
    }

    /// <summary>The ticket printer was replaced by a unit of the same model whose Plug and Play queue has A4; Setup has not run yet.</summary>
    private static Scenario ReplacedUnit() => new ScenarioBuilder()
        .WithUsbPrinter(SampleConfig.P4030, "USB001", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
        .WithModelDriver(SampleConfig.P4030)
        .WithUsbQueue(SampleConfig.P4030, "USB001", name: OldQueue)
        .WithTicketForm()
        .WithCorrectDefaults(OldQueue)
        .WithUsbPrinter(SampleConfig.P4030, "USB002")
        .WithUsbQueue(SampleConfig.P4030, "USB002", name: "TA P-4030DN KX (Copy 1)")
        .Build();

    [Fact]
    public async Task Silent_repair_after_a_replaced_unit_stops_and_says_how_to_resolve_it()
    {
        Scenario scenario = ReplacedUnit();
        var environment = scenario.Environment(SetupMode.Repair);

        FlowResult result = await Run(environment, Silent("--mode", "repair"), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning }
            && f.Detail.Contains($"Queue '{OldQueue}'", StringComparison.Ordinal) && f.Detail.Contains("Run Setup", StringComparison.Ordinal));
        AssertNoLeftOutNote(result);
        scenario.AssertNothingChanged();
    }

    // ---- Diagnostics -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Diagnostics_after_a_replaced_unit_checks_both_units_and_does_not_suggest_address_switches()
    {
        Scenario scenario = ReplacedUnit();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal(2, result.Verifications.Count);
        Finding why = Assert.Single(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.Contains($"Queue '{OldQueue}'", why.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("--address", why.Detail, StringComparison.Ordinal);
        AssertNoLeftOutNote(result);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_of_a_printer_with_two_ready_queues_at_the_configured_address_is_ready_with_a_warning()
    {
        const string Ip = "10.12.0.50";
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue("printer12.branch.local", SampleConfig.P4030, name: "Tickets")
            .WithNetworkQueue("printer12", SampleConfig.P4030, name: "Tickets 2")
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithCorrectDefaults("Tickets 2")
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias("printer12.branch.local", Ip)
            .WithNetworkAlias("printer12", Ip)
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal((FlowOutcome.ReadyWithWarnings, ExitCodes.ReadyWithWarnings), (result.Outcome, result.ExitCode));
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        Finding why = Assert.Single(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.Contains(Ip, why.Detail, StringComparison.Ordinal);
        Assert.Contains("'Tickets'", why.Detail, StringComparison.Ordinal);
        Assert.Contains("'Tickets 2'", why.Detail, StringComparison.Ordinal);
        scenario.AssertNothingChanged();
    }

    /// <summary>Detection's note that the earlier unit "is left out" does not stand once it is offered (or checked) again.</summary>
    private static void AssertNoLeftOutNote(FlowResult result)
    {
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.DeviceNotConnected && f.Detail.Contains("left out", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Report!.Findings, f => f.Code == FindingCode.DeviceNotConnected && f.Detail.Contains("left out", StringComparison.Ordinal));
    }
}
