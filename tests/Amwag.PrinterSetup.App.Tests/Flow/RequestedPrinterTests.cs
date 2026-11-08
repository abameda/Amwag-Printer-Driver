using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>
/// The printer that answers at the address IT configured (branch.json printerAddress or --address) is the one the flow sets
/// up or checks; another supported printer never stands in for it, also when its model must be confirmed first.
/// </summary>
public sealed class RequestedPrinterTests
{
    private const string Address = "10.20.30.40";
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);
    private readonly UpdateCollector _updates = new();

    [Fact]
    public async Task Silent_asserted_model_configures_the_printer_at_the_address_next_to_a_usb_printer()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false).WithNetworkPrinter(Address, modelId: null).Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4531 });
        var interaction = new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address, "--model", SampleConfig.P4531, "--confirm-model"]));
        var broker = new InProcessBroker(environment);

        FlowResult result = await new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

        WorkerRequest request = Assert.Single(broker.Requests);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), request.Target.Key);
        Assert.True(request.Target.ModelAssertedByUser);
        Assert.Equal(SampleConfig.P4531, request.Target.ModelId);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == Queue4030);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome + ": " + string.Join("; ", result.Findings.Select(f => f.Code + " " + f.Detail)));
    }

    [Fact]
    public async Task Silent_without_confirmation_stops_with_4_and_changes_nothing()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false).WithNetworkPrinter(Address, modelId: null).Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });
        var interaction = new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address]));
        var broker = new InProcessBroker(environment);

        FlowResult result = await new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
    }

    [Fact]
    public async Task Ui_asks_about_the_printer_at_the_branch_address_and_never_offers_the_usb_printer()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false).WithNetworkPrinter(Address, modelId: null)
            .WithBranch(new BranchOverride { PrinterAddress = Address }).Build();
        var environment = scenario.Environment();
        var interaction = new ScriptedInteraction { ChooseModel = (_, _) => null, Address = _ => null };

        FlowResult result = await new SetupFlow(environment, interaction, new InProcessBroker(environment)).RunAsync(_updates, CancellationToken.None);

        Assert.Equal(new[] { "model", "address" }, interaction.Calls);
        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.NoPrinter, result.Outcome);
    }

    [Theory]
    [InlineData("MFG:Hewlett-Packard;MDL:HP LaserJet M404;CMD:PCL;")]
    [InlineData("MFG:UTAX_TA;MDL:P-4532DN;CMD:PCL;")]
    public async Task Silent_setup_changes_no_other_printer_when_the_device_at_the_address_is_not_supported(string deviceId)
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithNetworkIdentity(new Amwag.PrinterSetup.Core.Inventory.NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, Ieee1284DeviceId = deviceId })
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });
        var broker = new InProcessBroker(environment);

        FlowResult result = await new SetupFlow(environment, new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address])), broker)
            .RunAsync(_updates, CancellationToken.None);

        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Assert.Equal(ExitCodes.NoSupportedPrinter, result.ExitCode);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound && f.Detail.Contains(Address, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Silent_setup_with_the_printer_at_the_address_switched_off_changes_no_other_printer_of_another_model()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P4531, correctDefaults: false)
            .WithNetworkPrinter(Address, SampleConfig.P4030, reachable: false, snmp: false).Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4030 });
        var interaction = new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address, "--model", SampleConfig.P4030, "--confirm-model"]));
        var broker = new InProcessBroker(environment);

        FlowResult result = await new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Assert.Equal(ExitCodes.NoSupportedPrinter, result.ExitCode);
    }

    [Fact]
    public async Task Silent_assertion_replaces_the_model_of_an_old_queue_driver_at_the_address()
    {
        // The printer at the address was replaced by a P-4531DN that does not report its model; its queue still has the P-4030DN driver.
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030).WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets")
            .WithNetworkPrinter(Address, modelId: null, snmp: false)
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4531 });
        var interaction = new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address, "--model", SampleConfig.P4531, "--confirm-model"]));
        var broker = new InProcessBroker(environment);

        FlowResult result = await new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

        WorkerRequest request = Assert.Single(broker.Requests);
        Assert.Equal((SampleConfig.P4531, true), (request.Target.ModelId, request.Target.ModelAssertedByUser));
        Assert.Contains(scenario.Spooler.Writes, w => w.Target == "Tickets" && w.Operation == nameof(Amwag.PrinterSetup.Core.Abstractions.IPrintSpooler.SetQueueDriver));
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome + ": " + string.Join("; ", result.Findings.Select(f => f.Code + " " + f.Detail)));
    }

    [Fact]
    public async Task Silent_repair_stops_for_a_probable_printer_with_a_queue_and_repairs_it_once_IT_confirms_the_model()
    {
        static Scenario Probable() => new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(Address, name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(new Amwag.PrinterSetup.Core.Inventory.NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer" })
            .Build();
        Scenario unconfirmed = Probable();
        var unconfirmedEnvironment = unconfirmed.Environment(SetupMode.Repair, edit: o => o with { PreferredAddress = Address });
        Scenario confirmed = Probable();
        var confirmedEnvironment = confirmed.Environment(SetupMode.Repair, edit: o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4030 });
        var broker = new InProcessBroker(confirmedEnvironment);

        FlowResult stopped = await new SetupFlow(unconfirmedEnvironment, new SilentInteraction(CommandLineParser.Parse(["--silent", "--mode", "repair", "--address", Address])), new InProcessBroker(unconfirmedEnvironment))
            .RunAsync(_updates, CancellationToken.None);
        FlowResult repaired = await new SetupFlow(confirmedEnvironment, new SilentInteraction(CommandLineParser.Parse(["--silent", "--mode", "repair", "--address", Address, "--model", SampleConfig.P4030, "--confirm-model"])), broker)
            .RunAsync(_updates, CancellationToken.None);

        Assert.Equal(ExitCodes.SelectionRequired, stopped.ExitCode);
        Assert.Contains(stopped.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        unconfirmed.AssertNothingChanged();
        Assert.True(Assert.Single(broker.Requests).Target.ModelAssertedByUser);
        Assert.True(repaired.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, repaired.Outcome + ": " + string.Join("; ", repaired.Findings.Select(f => f.Code + " " + f.Detail)));
    }

    [Fact]
    public async Task Diagnostics_follows_a_switched_off_ticket_printer_not_the_office_printer_that_is_on()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, present: false).WithModelDriver(SampleConfig.P4030).WithUsbQueue(SampleConfig.P4030).WithTicketForm().WithCorrectDefaults(Queue4030)
            .WithModelDriver(SampleConfig.P5032).WithNetworkQueue(Address, SampleConfig.P5032, name: "Office").WithNetworkPrinter(Address, SampleConfig.P5032)
            .Build();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await new SetupFlow(environment, new ScriptedInteraction(), new InProcessBroker(environment)).RunAsync(_updates, CancellationToken.None);

        Assert.Equal(Queue4030, result.Target?.Queue?.Name);
    }

    [Fact]
    public async Task Diagnostics_with_an_earlier_usb_unit_of_the_connected_model_is_ready()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .Build();
        var environment = scenario.Environment(SetupMode.Diagnostics);

        FlowResult result = await new SetupFlow(environment, new ScriptedInteraction(), new InProcessBroker(environment)).RunAsync(_updates, CancellationToken.None);

        Assert.Equal(ExitCodes.Ready, result.ExitCode);
        Assert.DoesNotContain(result.Findings, f => f is { Code: FindingCode.DeviceNotConnected, Severity: Severity.Warning });
    }
}
