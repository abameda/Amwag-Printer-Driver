using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// The Repair buttons (result and test print) repair the printer the result is about by its key, so Repair works with what the
/// printer reports when it runs; over the in-memory fakes, with one network printer.
/// </summary>
public sealed class RepairButtonTargetTests
{
    private const string Address = "10.0.0.80";

    [Fact]
    public async Task The_repair_button_repairs_a_printer_that_now_reports_another_model_than_its_queue_driver()
    {
        // Setup ran while the printer was asleep (SNMP silent), so its existing queue's P-4030DN driver identified it.
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue(Address, SampleConfig.P4030)
            .WithNetworkPrinter(Address, modelId: null)
            .WithTicketForm()
            .Build();
        FlowEnvironment setupEnvironment = scenario.Environment();
        FlowResult setup = await new SetupFlow(setupEnvironment, new ScriptedInteraction(), new InProcessBroker(setupEnvironment)).RunAsync(new UpdateCollector(), CancellationToken.None);
        Assert.True(setup.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, setup.Outcome.ToString());
        Assert.Equal(MatchConfidence.DriverOnly, setup.Target!.Confidence);

        // The test print woke it up: it reports a P-4531DN, so the queue has the wrong driver. The employee presses Repair.
        scenario.Network.SetIdentity(SampleConfig.NetworkIdentity(Address, SampleConfig.P4531));
        TargetSelector target = MainForm.RepairTargetOf(setup)!;
        FlowEnvironment repairEnvironment = scenario.Environment(SetupMode.Repair) with { RepairTarget = target };
        var broker = new InProcessBroker(repairEnvironment);
        FlowResult repair = await new SetupFlow(repairEnvironment, new ScriptedInteraction(), broker).RunAsync(new UpdateCollector(), CancellationToken.None);

        // Before: the button pinned P-4030DN, Repair refused ("has no queue of a supported printer to repair") and changed nothing.
        Assert.Equal(new TargetSelector(PrinterCandidate.NetworkKey(Address)), target);
        Assert.DoesNotContain(repair.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        WorkerRequest request = Assert.Single(broker.Requests);
        Assert.Equal((PrinterCandidate.NetworkKey(Address), SampleConfig.P4531), (request.Target.Key, request.Target.ModelId));
        Assert.Equal(SampleConfig.P4531, repair.Target?.Model?.Id);
        Assert.Contains(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetQueueDriver) && w.Detail == SampleConfig.DriverName(SampleConfig.P4531));
    }
}
