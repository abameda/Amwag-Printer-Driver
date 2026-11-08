using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// Windows keeps a USB printer node for every unit ever connected. A node that is not present while a USB printer of its
/// model is connected is an earlier connection (a replaced or moved unit): reported as information, not as a warning. Next to
/// any other printer it stays a warning, because a switched-off ticket printer is not connected either.
/// </summary>
public sealed class StaleUsbNodeTests
{
    private static readonly string StaleInstance = SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01");

    private static IReadOnlyList<Finding> NotConnected(DetectionResult detection, string instanceId) =>
        [.. detection.Findings.Where(f => f.Code == FindingCode.DeviceNotConnected && f.Data.TryGetValue("device", out string? device) && device == instanceId)];

    [Fact]
    public void Stale_node_next_to_a_connected_printer_of_its_model_is_information()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithUsbPrinter(present: false, usbPort: null, instanceId: StaleInstance)
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        Assert.Equal(2, detection.Candidates.Count);
        var finding = Assert.Single(NotConnected(detection, StaleInstance));
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(SampleConfig.P4030, finding.Data["model"]);
        Assert.Equal(PrinterCandidate.UsbKey(StaleInstance), finding.Data["target"]);
        Assert.DoesNotContain(detection.Findings, f => f is { Code: FindingCode.DeviceNotConnected, Severity: Severity.Warning });
    }

    [Fact]
    public void Stale_node_with_a_queue_next_to_a_connected_printer_of_its_model_names_the_queue()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: StaleInstance)
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: "Old ticket printer")
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        var finding = Assert.Single(NotConnected(detection, StaleInstance));
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Old ticket printer", finding.Data["queue"]);
        Assert.Contains("Old ticket printer", finding.Detail);
    }

    [Fact]
    public void Stale_node_whose_port_is_pooled_with_the_connected_printers_port_is_left_out_without_that_queue()
    {
        // The unit's earlier node on USB001 and the unit now on USB002 share the pooled 'Tickets', which the connected unit uses.
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", present: false, instanceId: StaleInstance)
            .WithUsbPrinter(SampleConfig.P4030, "USB002")
            .WithModelDriver()
            .WithTicketForm();
        builder.WithQueue("Tickets", SampleConfig.DriverName(SampleConfig.P4030), "USB001,USB002");
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Null(Assert.Single(run.Detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(StaleInstance)).Queue);
        var finding = Assert.Single(NotConnected(run.Detection, StaleInstance));
        Assert.Equal((Severity.Info, "Tickets"), (finding.Severity, finding.Data["keptQueue"]));
        Assert.False(finding.Data.ContainsKey("queue"));
        Assert.Contains("only that device node is left out", finding.Detail, StringComparison.Ordinal);
        Assert.Contains($"Queue 'Tickets', which also prints to its port USB001, is the queue of {SampleConfig.P4030} ({PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030))}) and is not left out",
            finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("its queue 'Tickets' are left out", finding.Detail, StringComparison.Ordinal);
        Assert.Equal("Tickets", run.Selection.Selected!.Queue!.Name);
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Target: "Tickets" });
    }

    [Fact]
    public void Stale_node_sharing_its_pooled_queue_with_a_printer_that_is_not_left_out_is_left_out_alone()
    {
        // The earlier P-4030DN unit (USB001) and a switched-off P-4531DN (USB002) share 'Tickets'; a P-4030DN is connected on USB003.
        string switchedOff = SampleConfig.UsbInstanceId(SampleConfig.P4531, "SERIAL4531");
        var builder = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB003")
            .WithUsbPrinter(SampleConfig.P4030, "USB001", present: false, instanceId: StaleInstance)
            .WithUsbPrinter(SampleConfig.P4531, "USB002", present: false, instanceId: switchedOff)
            .WithModelDriver(SampleConfig.P4531);
        builder.WithQueue("Tickets", SampleConfig.DriverName(SampleConfig.P4531), "USB001,USB002");
        var scenario = builder.Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        var finding = Assert.Single(NotConnected(detection, StaleInstance));
        Assert.Equal((Severity.Info, "Tickets"), (finding.Severity, finding.Data["keptQueue"]));
        Assert.Contains($"is the queue of {SampleConfig.P4531} ({PrinterCandidate.UsbKey(switchedOff)}) and is not left out", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(Severity.Warning, Assert.Single(NotConnected(detection, switchedOff)).Severity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the pool seeded before the node's own queue
    public void Stale_node_with_its_own_queue_beside_a_pool_over_the_connected_units_port_is_left_out_with_its_queue(bool poolFirst)
    {
        // The earlier unit's node on USB001 keeps its Plug and Play queue; 'Tickets' is pooled over USB001 and the port of the
        // unit now connected on USB002, so it is the connected unit's queue and is not left out.
        const string oldQueue = "TA P-4030DN KX (old unit)";
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB002")
            .WithUsbPrinter(SampleConfig.P4030, "USB001", present: false, instanceId: StaleInstance)
            .WithModelDriver()
            .WithTicketForm();
        if (poolFirst)
            builder.WithQueue("Tickets", SampleConfig.DriverName(SampleConfig.P4030), "USB001,USB002").WithUsbQueue(SampleConfig.P4030, "USB001", name: oldQueue);
        else
            builder.WithUsbQueue(SampleConfig.P4030, "USB001", name: oldQueue).WithQueue("Tickets", SampleConfig.DriverName(SampleConfig.P4030), "USB001,USB002");
        var scenario = builder.Build();
        var session = scenario.CreateSession();

        var detection = session.Detect(new DetectRequest(), default);
        var selection = session.SelectAutomatically(detection);

        var old = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(StaleInstance));
        Assert.Equal(oldQueue, old.Queue?.Name);
        Assert.Contains(old, selection.Superseded);
        var finding = Assert.Single(NotConnected(detection, StaleInstance));
        Assert.Equal((Severity.Info, oldQueue, "Tickets"), (finding.Severity, finding.Data["queue"], finding.Data["keptQueue"]));
        Assert.Contains($"it and its queue '{oldQueue}' are left out because a USB printer of the same model is connected.", finding.Detail, StringComparison.Ordinal);
        Assert.Contains($"Queue 'Tickets', which also prints to its port USB001, is the queue of {SampleConfig.P4030} ({PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030))}) and is not left out.",
            finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("only that device node is left out", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_node_next_to_a_reachable_network_printer_of_its_model_stays_a_warning()
    {
        // The USB printer may be the switched-off ticket printer; the network printer of the same model is another printer.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(present: false, usbPort: null, instanceId: StaleInstance)
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = ["10.0.0.5"] }, default);

        Assert.Equal(Severity.Warning, Assert.Single(NotConnected(detection, StaleInstance)).Severity);
    }

    [Fact]
    public void Stale_node_of_another_model_than_the_connected_printer_stays_a_warning()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P5032)
            .WithUsbPrinter(present: false, usbPort: null, instanceId: StaleInstance)
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        Assert.Equal(Severity.Warning, Assert.Single(NotConnected(detection, StaleInstance)).Severity);
    }

    [Fact]
    public void Stale_node_alone_stays_a_warning()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(present: false, usbPort: null, instanceId: StaleInstance).Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        Assert.Equal(Severity.Warning, Assert.Single(NotConnected(detection, StaleInstance)).Severity);
        Assert.Single(detection.Candidates);
    }

    [Fact]
    public void Stale_node_next_to_an_unreachable_network_printer_of_its_model_stays_a_warning()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(present: false, usbPort: null, instanceId: StaleInstance)
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030, reachable: false)
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = ["10.0.0.5"] }, default);

        Assert.Equal(Severity.Warning, Assert.Single(NotConnected(detection, StaleInstance)).Severity);
    }

    // ---- A queue left by an earlier unit whose node is gone (removed in Device Manager, or its port unknown) -------------

    private static Finding QueueNotConnected(DetectionResult detection, string queue) =>
        Assert.Single(detection.Findings, f => f.Code == FindingCode.DeviceNotConnected && f.Data.TryGetValue("queue", out string? name) && name == queue);

    [Fact]
    public void Queue_without_a_connected_printer_next_to_a_connected_printer_of_its_model_is_information()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: "Old ticket printer")
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        var leftover = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.QueueKey("Old ticket printer"));
        Assert.True(leftover.IsSupersededAmong(detection.Candidates));
        var finding = QueueNotConnected(detection, "Old ticket printer");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(("USB002", leftover.Key), (finding.Data["port"], finding.Data["target"]));
        Assert.Contains("left out", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(detection.Findings, f => f is { Code: FindingCode.DeviceNotConnected, Severity: Severity.Warning });
    }

    [Fact]
    public void Queue_without_a_connected_printer_next_to_a_connected_printer_of_another_model_stays_a_warning()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB001")
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: "Old ticket printer")
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        Assert.Equal(Severity.Warning, QueueNotConnected(detection, "Old ticket printer").Severity);
    }

    [Fact]
    public void Queue_without_a_connected_printer_next_to_a_network_printer_of_its_model_stays_a_warning()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: "Old ticket printer")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = ["10.0.0.5"] }, default);

        Assert.Equal(Severity.Warning, QueueNotConnected(detection, "Old ticket printer").Severity);
    }
}
