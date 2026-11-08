using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>
/// Diagnostics follows the printers Setup and Repair would offer or ask about: the printer whose model Repair asks to confirm,
/// and printers whose model must be decided next to eligible ones. Its advice names only switches that can decide, and the
/// result's current findings are the ticket printer's own, not every printer's it checked.
/// </summary>
public sealed class TicketPrinterDiagnosticsTests
{
    private readonly UpdateCollector _updates = new();

    private static NetworkIdentity Probable(string address, string model) =>
        new() { Address = address, RawPortReachable = true, SnmpResponded = true, SysDescr = $"TA Triumph-Adler {model} Series Network Printer" };

    private static SilentInteraction Silent(params string[] args) => new(CommandLineParser.Parse(["--silent", .. args]));

    private Task<FlowResult> Diagnose(Scenario scenario)
    {
        var environment = scenario.Environment(SetupMode.Diagnostics);
        return new SetupFlow(environment, Silent("--mode", "diagnostics"), new InProcessBroker(environment)).RunAsync(_updates, CancellationToken.None);
    }

    /// <summary>A connected USB P-4531DN without a queue, and class-driver queue 'Tickets' whose printer is probably a P-4030DN.</summary>
    private static Scenario UsbWithoutQueueAndProbableQueue(bool ticketPaper)
    {
        ScenarioBuilder builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4531, driverBound: false)
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030));
        return (ticketPaper ? builder.WithTicketForm().WithCorrectDefaults("Tickets") : builder).Build();
    }

    [Fact]
    public async Task The_printer_whose_model_repair_asks_to_confirm_decides_when_its_queue_has_the_ticket_paper()
    {
        FlowResult result = await Diagnose(UsbWithoutQueueAndProbableQueue(ticketPaper: true));

        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Finding unconfirmed = Assert.Single(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Assert.Contains($"--mode setup --address 10.0.0.50 --model {SampleConfig.P4030} --confirm-model", unconfirmed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_ticket_paper_both_printers_setup_offers_are_named()
    {
        FlowResult result = await Diagnose(UsbWithoutQueueAndProbableQueue(ticketPaper: false));

        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.DoesNotContain(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        Finding undecided = Assert.Single(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.StartsWith("2 supported printers were found", undecided.Detail, StringComparison.Ordinal);
        Assert.Contains(SampleConfig.P4531, undecided.Detail, StringComparison.Ordinal);
        Assert.Contains($"--mode setup --address 10.0.0.50 --model {SampleConfig.P4030} --confirm-model", undecided.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SampleConfig.P4030, "printerAddress in branch.json or --address (modelId does not tell them apart)")]
    [InlineData(SampleConfig.P4531, "printerAddress or modelId in branch.json, or with --address/--model")]
    public async Task The_advice_names_modelId_only_where_it_decides(string secondModel, string advice)
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(secondModel)
            .WithNetworkQueue("10.0.0.40", SampleConfig.P4030, name: "A4 office")
            .WithNetworkPrinter("10.0.0.40", SampleConfig.P4030)
            .WithNetworkQueue("10.0.0.50", secondModel, name: "Tickets")
            .WithNetworkPrinter("10.0.0.50", secondModel)
            .Build();
        var environment = scenario.Environment();

        FlowResult setup = await new SetupFlow(environment, Silent(), new InProcessBroker(environment)).RunAsync(_updates, CancellationToken.None);
        FlowResult diagnostics = await Diagnose(scenario);

        Assert.Equal(ExitCodes.SelectionRequired, setup.ExitCode);
        Assert.Contains(advice, Assert.Single(setup.Findings, f => f.Code == FindingCode.Cancelled).Detail, StringComparison.Ordinal);
        Assert.Contains(advice, Assert.Single(diagnostics.Findings, f => f.Code == FindingCode.MultipleSupportedPrinters).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Next_to_a_rejected_queue_a_printer_whose_model_must_be_confirmed_is_still_named()
    {
        // An office USB P-4030DN, a probable P-4030DN 'Front desk' at 10.0.0.50, and 'Old tickets' whose device reports an
        // unsupported replacement.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Front desk", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
            .WithNetworkQueue("10.0.0.60", SampleConfig.P4030, name: "Old tickets")
            .WithNetworkIdentity(new NetworkIdentity { Address = "10.0.0.60", RawPortReachable = true, SnmpResponded = true, Ieee1284DeviceId = "MFG:UTAX_TA;MDL:P-4532DN;CMD:PCL;" })
            .Build();

        FlowResult result = await Diagnose(scenario);

        Finding undecided = Assert.Single(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.Contains("10.0.0.50", undecided.Detail, StringComparison.Ordinal);
        Assert.Contains($"--address 10.0.0.50 --model {SampleConfig.P4030} --confirm-model", undecided.Detail, StringComparison.Ordinal);
        Assert.Contains("'Old tickets'", undecided.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_step_list_ends_on_the_ticket_printer_not_on_the_printer_checked_last()
    {
        // The ticket printer at the branch address is ready; an office USB printer, checked after it, is not.
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue("10.0.0.5", SampleConfig.P4030, name: "Tickets")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithInstalledUsbPrinter(SampleConfig.P4531, correctDefaults: false)
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" })
            .Build();

        FlowResult result = await Diagnose(scenario);

        Assert.Equal(FlowOutcome.Ready, result.Outcome);
        Assert.Equal(2, result.Verifications.Count);
        Assert.Equal(StepState.Succeeded, _updates.FinalStates()[StepId.Verify]);
    }

    [Fact]
    public async Task Without_a_known_ticket_printer_the_steps_say_that_several_printers_were_checked()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue("10.0.0.40", SampleConfig.P4030, name: "A4 office")
            .WithNetworkPrinter("10.0.0.40", SampleConfig.P4030)
            .WithNetworkQueue("10.0.0.50", SampleConfig.P4531, name: "Tickets")
            .WithNetworkPrinter("10.0.0.50", SampleConfig.P4531)
            .Build();

        await Diagnose(scenario);

        ProgressUpdate verify = _updates.OfKind(FlowEventKind.Step).Select(u => u.Progress!).Last(p => p.Step == StepId.Verify);
        Assert.Equal((StepState.Warning, FindingCode.MultipleSupportedPrinters), (verify.State, verify.Code));
    }

    [Fact]
    public async Task A_probable_printer_next_to_an_eligible_one_is_counted_and_named()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4531, correctDefaults: false)
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
            .Build();

        FlowResult result = await Diagnose(scenario);

        Finding undecided = Assert.Single(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.StartsWith("2 supported printers were found and none is known to be the ticket printer", undecided.Detail, StringComparison.Ordinal);
        Assert.Contains($"the printer at 10.0.0.50 (probably a {SampleConfig.P4030}, queue \"Tickets\")", undecided.Detail, StringComparison.Ordinal);
        Assert.Contains("those of a known model were checked", undecided.Detail, StringComparison.Ordinal);
        Assert.Contains($"--mode setup --address 10.0.0.50 --model {SampleConfig.P4030} --confirm-model", undecided.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_advice_does_not_name_a_model_that_is_already_set_and_does_not_decide()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Front desk", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .Build();

        FlowResult result = await Diagnose(scenario);

        Finding undecided = Assert.Single(result.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.Contains($"modelId {SampleConfig.P4030} is set but does not tell them apart", undecided.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("printerAddress or modelId", undecided.Detail, StringComparison.Ordinal);
        Assert.Contains("10.0.0.50", undecided.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 'Tickets' has the P-4030DN driver, but the device on its host reports an unsupported replacement, so the queue is rejected;
    /// an office USB P-4030DN is connected and the branch names P-4030DN.
    /// </summary>
    private static Scenario RejectedQueueOfTheConfiguredModel() => new ScenarioBuilder()
        .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
        .WithNetworkQueue("10.0.0.50", SampleConfig.P4030, name: "Tickets")
        .WithNetworkIdentity(new NetworkIdentity { Address = "10.0.0.50", RawPortReachable = true, SnmpResponded = true, Ieee1284DeviceId = "MFG:UTAX_TA;MDL:P-4532DN;CMD:PCL;" })
        .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
        .Build();

    [Fact]
    public async Task Next_to_a_rejected_queue_of_the_same_model_no_switch_is_advised()
    {
        FlowResult diagnostics = await Diagnose(RejectedQueueOfTheConfiguredModel());
        Scenario scenario = RejectedQueueOfTheConfiguredModel();
        var environment = scenario.Environment();
        FlowResult setup = await new SetupFlow(environment, Silent(), new InProcessBroker(environment)).RunAsync(_updates, CancellationToken.None);

        Finding note = Assert.Single(diagnostics.Findings, f => f is { Code: FindingCode.MultipleSupportedPrinters, Severity: Severity.Warning });
        Assert.Contains("could not be matched to their printer", note.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("reports another identity", note.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("--model", note.Detail, StringComparison.Ordinal);
        Assert.Contains("No switch tells them apart", note.Detail, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.SelectionRequired, setup.ExitCode);
        Finding declined = Assert.Single(setup.Findings, f => f.Code == FindingCode.Cancelled);
        Assert.Contains("No switch tells them apart", declined.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_current_findings_are_the_ticket_printer_s_own_and_the_report_keeps_every_printer_s()
    {
        // The branch names the USB P-4030DN as the ticket printer: only its own Printing Preferences are wrong. An office P-4531DN
        // at 10.0.0.60 keeps its A4 defaults.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030)
            .WithPerUserDefaults(SampleConfig.DriverName(SampleConfig.P4030), d => d.Scale = 80)
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue("10.0.0.60", SampleConfig.P4531, name: "Office")
            .WithNetworkPrinter("10.0.0.60", SampleConfig.P4531)
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .Build();

        FlowResult result = await Diagnose(scenario);

        Assert.StartsWith("usb:", result.Target?.Key, StringComparison.Ordinal);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.GlobalDefaultsIncorrect);
        Assert.DoesNotContain(result.CurrentFindings, f => f.Code == FindingCode.GlobalDefaultsIncorrect);
        Assert.Contains(result.CurrentFindings, f => f.Code == FindingCode.UserDefaultsIncorrect);
    }
}
