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
/// Decisions about one printer's queues and about printers whose model must be confirmed, which no rule may take alone. At the
/// configured address, a queue host whose model is not known next to an eligible one is a choice (Repair: the one on the
/// ticket paper), never a reason to configure the other. A silent run that stops for a choice says how IT decides it. IT's
/// model that every printer offered contradicts is refused, also in Repair when the printer has no queue yet.
/// </summary>
public sealed class QueueHostDecisionTests
{
    private const string Ip = "10.12.0.50";
    private const string Address = "10.20.30.40";

    private readonly UpdateCollector _updates = new();

    private static NetworkIdentity Probable(string address, string model) =>
        new() { Address = address, RawPortReachable = true, SnmpResponded = true, SysDescr = $"TA Triumph-Adler {model} Series Network Printer" };

    private static SilentInteraction Silent(params string[] args) => new(CommandLineParser.Parse(["--silent", .. args]));

    private Task<FlowResult> Run(FlowEnvironment environment, ISetupInteraction interaction, InProcessBroker broker) =>
        new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

    // ---- One printer, a TA-driver queue and a class-driver queue on two of its spellings ---------------------------------

    /// <summary>
    /// One P-4030DN reached as printer12 and as printer12.branch.local (both lead to <see cref="Ip"/>): 'A4 office' on printer12
    /// with the TA driver, and 'Tickets' on printer12.branch.local with the class driver and the ticket defaults. The printer
    /// reports a descriptive identity only ("probable"), does not answer SNMP ("off"), or does not answer at all because it is
    /// switched off ("switchedOff"). The branch names <see cref="Ip"/>.
    /// </summary>
    private static Scenario MixedQueueHosts(string identity, bool branchAddress = true)
    {
        if (identity == "switchedOff")
        {
            ScenarioBuilder off = new ScenarioBuilder()
                .WithModelDriver(SampleConfig.P4030)
                .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
                .WithNetworkQueue("printer12", SampleConfig.P4030, name: "A4 office")
                .WithNetworkQueue("printer12.branch.local", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
                .WithTicketForm()
                .WithCorrectDefaults("Tickets")
                .WithNetworkPrinter(Ip, modelId: null, reachable: false, snmp: false)
                .WithNetworkAlias("printer12", Ip)
                .WithNetworkAlias("printer12.branch.local", Ip);
            return (branchAddress ? off.WithBranch(new BranchOverride { PrinterAddress = Ip }) : off).Build();
        }

        ScenarioBuilder builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("printer12", SampleConfig.P4030, name: "A4 office")
            .WithNetworkQueue("printer12.branch.local", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithNetworkAlias("printer12", Ip)
            .WithNetworkAlias("printer12.branch.local", Ip);
        builder = identity == "probable" ? builder.WithNetworkIdentity(Probable(Ip, SampleConfig.P4030)) : builder.WithNetworkPrinter(Ip, modelId: null, snmp: false);
        return (branchAddress ? builder.WithBranch(new BranchOverride { PrinterAddress = Ip }) : builder).Build();
    }

    [Theory]
    [InlineData("probable", "setup")]
    [InlineData("probable", "repair")]
    [InlineData("off", "setup")]
    [InlineData("off", "repair")]
    [InlineData("switchedOff", "setup")]
    [InlineData("switchedOff", "repair")]
    public async Task A_silent_run_at_the_address_changes_neither_queue_of_the_printer_and_says_how_to_decide(string identity, string mode)
    {
        Scenario scenario = MixedQueueHosts(identity);
        var environment = scenario.Environment(mode == "repair" ? SetupMode.Repair : SetupMode.Setup);
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--mode", mode), broker);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        // The queue on the ticket paper needs its model confirmed: the finding gives the switches for its own host.
        Finding unconfirmed = Assert.Single(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Contains("--address printer12.branch.local --model ", unconfirmed.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("probable")]
    [InlineData("off")]
    public async Task Setup_in_the_window_offers_both_queues_of_the_printer_at_the_address(string identity)
    {
        Scenario scenario = MixedQueueHosts(identity);
        var environment = scenario.Environment();
        IReadOnlyList<PrinterCandidate>? offered = null;
        var interaction = new ScriptedInteraction
        {
            Choose = choices =>
            {
                offered = choices;
                return choices.Single(c => c.Queue?.Name == "Tickets");
            },
            Confirm = _ => true,
            ChooseModel = (_, models) => models.Single(m => m.Id == SampleConfig.P4030),
        };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal("choose", interaction.Calls[0]);
        Assert.Equal(["A4 office", "Tickets"], offered!.Select(c => c.Queue?.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PrinterCandidate.NetworkKey("printer12.branch.local"), Assert.Single(broker.Requests).Target.Key);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == "A4 office");
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("repair")]
    public async Task With_the_printer_switched_off_and_no_address_the_office_queue_is_not_configured_in_its_place(string mode)
    {
        Scenario scenario = MixedQueueHosts("switchedOff", branchAddress: false);
        var environment = scenario.Environment(mode == "repair" ? SetupMode.Repair : SetupMode.Setup);

        FlowResult result = await Run(environment, Silent("--mode", mode), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Finding unconfirmed = Assert.Single(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Contains("printer12.branch.local does not answer", unconfirmed.Detail, StringComparison.Ordinal);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Repair_in_the_window_asks_only_about_the_queue_on_the_ticket_paper()
    {
        Scenario scenario = MixedQueueHosts("probable");
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction { Confirm = _ => true };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(["confirm"], interaction.Calls);
        Assert.Equal(PrinterCandidate.NetworkKey("printer12.branch.local"), Assert.Single(broker.Requests).Target.Key);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == "A4 office");
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
    }

    [Fact]
    public async Task IT_s_model_for_the_address_confirms_neither_queue_and_the_finding_names_the_hosts_to_choose_from()
    {
        Scenario scenario = MixedQueueHosts("probable");
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Ip, PreferredModelId = SampleConfig.P4030 });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--address", Ip, "--model", SampleConfig.P4030, "--confirm-model"), broker);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Finding declined = Assert.Single(result.Findings, f => f.Code == FindingCode.Cancelled);
        Assert.Contains("'A4 office' on printer12", declined.Detail, StringComparison.Ordinal);
        Assert.Contains("'Tickets' on printer12.branch.local", declined.Detail, StringComparison.Ordinal);
        Assert.Contains("--address set to the host of the ticket queue", declined.Detail, StringComparison.Ordinal);
        // IT gave the model: no question about it is left open.
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.ProbableMatchRequiresConfirmation);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_at_the_address_asks_to_confirm_the_queue_on_the_ticket_paper_instead_of_judging_the_other()
    {
        Scenario scenario = MixedQueueHosts("probable");
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await Run(environment, Silent("--mode", "diagnostics"), new InProcessBroker(environment));

        Assert.Equal((FlowOutcome.NotReady, ExitCodes.NotReady), (result.Outcome, result.ExitCode));
        Finding unconfirmed = Assert.Single(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Contains("printer12.branch.local", unconfirmed.Detail, StringComparison.Ordinal);
        // 'A4 office' is checked for the report only: its read-back is not what the result says about the ticket printer.
        VerificationReport office = Assert.Single(result.Verifications, v => v.TargetKey == PrinterCandidate.NetworkKey("printer12"));
        Assert.All(office.Findings.Where(f => f.Severity != Severity.Info), f => Assert.DoesNotContain(f, result.CurrentFindings));
        Assert.Contains(unconfirmed, result.CurrentFindings);
    }

    [Fact]
    public async Task The_printer_s_own_spelling_still_names_its_queue_without_a_question()
    {
        Scenario scenario = MixedQueueHosts("probable", branchAddress: false);
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = "printer12" });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--address", "printer12"), broker);

        Assert.Equal(PrinterCandidate.NetworkKey("printer12"), Assert.Single(broker.Requests).Target.Key);
        Assert.True(result.ExitCode is ExitCodes.Ready or ExitCodes.ReadyWithWarnings, result.ExitCode.ToString());
    }

    // ---- Silent runs that stop for a choice ------------------------------------------------------------------------------

    /// <summary>An office and a ticket P-4030DN, both on class-driver queues, both reporting only a descriptive (probable) identity.</summary>
    private static Scenario TwoProbablePrinters(bool ticketPaperOnTickets = false)
    {
        ScenarioBuilder builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.40", name: "A4 office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.40", SampleConfig.P4030))
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030));
        return (ticketPaperOnTickets ? builder.WithTicketForm().WithCorrectDefaults("Tickets") : builder).Build();
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("repair")]
    public async Task A_silent_run_that_stops_for_several_probable_printers_gives_each_one_s_switches(string mode)
    {
        Scenario scenario = TwoProbablePrinters();
        var environment = scenario.Environment(mode == "repair" ? SetupMode.Repair : SetupMode.Setup);

        FlowResult result = await Run(environment, Silent("--mode", mode), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        var unconfirmed = result.Findings.Where(f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error }).ToList();
        Assert.Equal(2, unconfirmed.Count);
        Assert.Contains(unconfirmed, f => f.Detail.Contains($"--address 10.0.0.40 --model {SampleConfig.P4030} --confirm-model", StringComparison.Ordinal));
        Assert.Contains(unconfirmed, f => f.Detail.Contains($"--address 10.0.0.50 --model {SampleConfig.P4030} --confirm-model", StringComparison.Ordinal));
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Repair_of_several_probable_printers_asks_only_about_the_one_on_the_ticket_paper()
    {
        Scenario scenario = TwoProbablePrinters(ticketPaperOnTickets: true);
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction { Confirm = _ => true };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(["confirm"], interaction.Calls);
        Assert.Equal(PrinterCandidate.NetworkKey("10.0.0.50"), Assert.Single(broker.Requests).Target.Key);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == "A4 office");
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
    }

    [Fact]
    public async Task IT_s_model_that_both_queue_hosts_of_an_exact_printer_contradict_is_refused_with_both_models()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue("printer12", SampleConfig.P4030, name: "A4 office")
            .WithNetworkQueue("printer12.branch.local", SampleConfig.P4030, name: "Tickets")
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias("printer12", Ip)
            .WithNetworkAlias("printer12.branch.local", Ip)
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Ip, PreferredModelId = SampleConfig.P4531 });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--address", Ip, "--model", SampleConfig.P4531, "--confirm-model"), broker);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Finding refused = Assert.Single(result.Findings, f => f is { Code: FindingCode.NetworkIdentityMismatch, Severity: Severity.Error });
        Assert.Contains($"The model {SampleConfig.P4531} was given for the printer at {Ip}", refused.Detail, StringComparison.Ordinal);
        Assert.Contains("reports " + SampleConfig.P4030, refused.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.Cancelled);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_refused_model_names_the_address_IT_gave_and_the_queue_s_host()
    {
        // 'Tickets' is on printer12, another spelling of the exact P-4030DN at 10.12.0.50.
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue("printer12", SampleConfig.P4030, name: "Tickets")
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias("printer12", Ip)
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Ip, PreferredModelId = SampleConfig.P4531 });

        FlowResult result = await Run(environment, Silent("--address", Ip, "--model", SampleConfig.P4531, "--confirm-model"), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Finding refused = Assert.Single(result.Findings, f => f is { Code: FindingCode.NetworkIdentityMismatch, Severity: Severity.Error });
        Assert.Contains($"given for the printer at {Ip} (its queue's host printer12)", refused.Detail, StringComparison.Ordinal);
        Assert.Equal(Ip, refused.Data["address"]);
    }

    [Fact]
    public async Task Silent_repair_refuses_IT_s_model_that_a_printer_without_a_queue_contradicts()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkPrinter(Address, SampleConfig.P4030)
            .Build();
        var environment = scenario.Environment(SetupMode.Repair, o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4531 });

        FlowResult result = await Run(environment, Silent("--mode", "repair", "--address", Address, "--model", SampleConfig.P4531, "--confirm-model"), new InProcessBroker(environment));

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Finding refused = Assert.Single(result.Findings, f => f is { Code: FindingCode.NetworkIdentityMismatch, Severity: Severity.Error });
        Assert.Contains("reports " + SampleConfig.P4030, refused.Detail, StringComparison.Ordinal);
        Assert.Equal(SampleConfig.P4531, refused.Data["assertedModel"]);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Silent_repair_with_IT_s_matching_model_and_no_queue_yet_still_says_to_run_setup()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkPrinter(Address, SampleConfig.P4030)
            .Build();
        var environment = scenario.Environment(SetupMode.Repair, o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4030 });

        FlowResult result = await Run(environment, Silent("--mode", "repair", "--address", Address, "--model", SampleConfig.P4030, "--confirm-model"), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.NoSupportedPrinter, result.ExitCode);
        Finding none = Assert.Single(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        Assert.Equal("setup", none.Data["suggestedMode"]);
    }

    // ---- One queue pooled over two connected identical units ---------------------------------------------------------------

    [Fact]
    public async Task Repair_of_one_queue_pooled_over_two_connected_units_repairs_that_queue_without_a_question()
    {
        const string Pooled = "Tickets";
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4030, "USB002", instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "SECONDUNIT02"))
            .WithModelDriver(SampleConfig.P4030)
            .WithQueue(Pooled, SampleConfig.DriverName(SampleConfig.P4030), "USB001,USB002")
            .WithTicketForm()
            .WithCorrectDefaults(Pooled)
            .WithPerUserDefaults(Pooled, d => d.Scale = 80)
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction();

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Empty(interaction.Calls);
        Assert.Contains(scenario.Spooler.Writes, w => w.Target == Pooled);
        // The other unit's candidate shares the repaired queue: it is not reported as a printer Repair left unchanged.
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.MultipleSupportedPrinters && f.Detail.Contains($"Queue '{Pooled}'", StringComparison.Ordinal));
    }

    // ---- A queue whose host the probe limit left out ------------------------------------------------------------------------

    private const string NotCheckedNote = " Queue(s) 'Tickets' on printer12.branch.local were not checked because the limit of network probes was reached, and one "
        + "of them may be the ticket printer's: name the host of the ticket queue with --address or printerAddress, or raise connection.maxNetworkProbes in the "
        + "configuration so that they are checked (see the NetworkIdentityUnavailable finding).";

    /// <summary>
    /// The shipped probe limit (8) with one printer (SNMP off) reached as printer12 and printer12.branch.local, both leading to
    /// <see cref="Ip"/>: the TA driver's 'A4 office' on printer12, class-driver queues to six other printers that do not
    /// answer, and last the class driver's 'Tickets' on the ticket paper on printer12.branch.local, whose host the limit
    /// leaves out. The branch names <see cref="Ip"/>, the host of neither queue.
    /// </summary>
    internal static Scenario TicketQueueNotChecked()
    {
        ScenarioBuilder builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("printer12", SampleConfig.P4030, name: "A4 office");
        foreach (int n in Enumerable.Range(71, 6))
            builder.WithNetworkQueue($"10.0.0.{n}", name: $"Office {n}", driverName: SampleConfig.MicrosoftPcl6ClassDriver);
        return builder
            .WithNetworkQueue("printer12.branch.local", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithNetworkPrinter(Ip, modelId: null, snmp: false)
            .WithNetworkAlias("printer12", Ip)
            .WithNetworkAlias("printer12.branch.local", Ip)
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("repair")]
    public async Task A_silent_run_next_to_a_queue_the_probe_limit_left_out_changes_nothing_and_says_how_to_decide(string mode)
    {
        Scenario scenario = TicketQueueNotChecked();
        var environment = scenario.Environment(mode == "repair" ? SetupMode.Repair : SetupMode.Setup);
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--mode", mode), broker);

        // Before: 'A4 office' was set up as the ticket printer, and the queue on the ticket paper was not looked at.
        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.NetworkIdentityUnavailable, Severity: Severity.Warning } && f.Data.GetValueOrDefault("queue") == "Tickets");
        Finding stop = Assert.Single(result.Findings, f => f.Code == FindingCode.Cancelled);
        // The host of the queue that was found sets up that queue: the advice says so, and names the host of the one not checked.
        Assert.Equal("1 supported printer was found and it was not chosen. If 'A4 office' on printer12 is the ticket queue, name that host with "
            + "printerAddress printer12 in branch.json or --address printer12, or choose it in Setup." + NotCheckedNote, stop.Detail);
    }

    [Fact]
    public async Task Setup_in_the_window_next_to_a_queue_the_probe_limit_left_out_asks_before_it_sets_up_the_printer()
    {
        Scenario scenario = TicketQueueNotChecked();
        var environment = scenario.Environment();
        IReadOnlyList<PrinterCandidate>? offered = null;
        var interaction = new ScriptedInteraction
        {
            Choose = choices =>
            {
                offered = choices;
                return null;
            },
            // Choosing none offers to enter the ticket printer's address; the employee enters none.
            Address = _ => null,
        };
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal("choose", interaction.Calls[0]);
        Assert.Equal(["A4 office"], offered!.Select(c => c.Queue?.Name));
        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_next_to_a_queue_the_probe_limit_left_out_does_not_judge_the_ticket_printer_by_the_other_queue()
    {
        Scenario scenario = TicketQueueNotChecked();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Null(result.Target);
        Finding unknown = Assert.Single(result.Findings, f => f.Code == FindingCode.MultipleSupportedPrinters);
        Assert.Equal($"The printer at {Ip} has queue 'A4 office' on printer12, but other queues were not checked, so it is not known to be the ticket queue; "
            + "it was checked. Choose it in Setup, or name the host of the ticket queue with --address (printerAddress in branch.json)." + NotCheckedNote, unknown.Detail);
        ProgressUpdate verify = _updates.OfKind(FlowEventKind.Step).Select(u => u.Progress!).Last(p => p.Step == StepId.Verify);
        Assert.Equal((StepState.Warning, FindingCode.MultipleSupportedPrinters), (verify.State, verify.Code));
        Assert.Equal("1 printer was checked; it is not known to be the ticket printer", verify.Detail);
        scenario.AssertNothingChanged();
    }
}
