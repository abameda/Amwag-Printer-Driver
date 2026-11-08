using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// Queues on several ports or on ports the setup cannot configure. PRINTER_INFO_2.pPortName lists a pooled queue's ports
/// separated by commas: a pooled queue with the USB printer's port is that printer's queue (never a second queue, and its
/// pooling is kept), unless the printer is not connected and another printer on the queue's ports uses it. A queue whose
/// driver names a supported model on ports the setup cannot configure as a network queue (pooled ports, an LPR or Internet
/// Port), and a class-driver queue pooled over Standard TCP/IP ports, is linked to every printer it prints to: the queue is
/// never configured, and those printers are not set up automatically, so no second queue is added next to it and no other
/// queue of theirs is changed. A class-driver queue on an LPR or Internet Port does the same only when it is its printer's
/// ticket queue; otherwise the report names it next to the printer's queue. A queue whose port gives no printer's host (a
/// redirected local port, a Standard TCP/IP port without a host) is left unchanged, may be the ticket printer's, so no other
/// printer is chosen without asking, and may be a requested printer's. Neither the plan nor the worker adds a queue on a
/// Standard TCP/IP or USB port that another queue already prints to.
/// </summary>
public sealed class QueuePortTests
{
    private const string Tickets = "Tickets";
    private static readonly string Driver4030 = SampleConfig.DriverName(SampleConfig.P4030);

    private static string[] Writes(Scenario scenario, params string[] operations) =>
        [.. scenario.Spooler.Writes.Where(w => operations.Contains(w.Operation)).Select(w => $"{w.Operation} {w.Target}")];

    /// <summary>The warning that the printer at <paramref name="address"/> is not set up automatically because of <paramref name="queue"/>.</summary>
    private static Finding LeftUnchangedAt(DetectionResult detection, string address, string queue = Tickets) =>
        Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged
            && f.Data.GetValueOrDefault("address") == address && f.Data.GetValueOrDefault("queue") == queue);

    // ---- Pooled USB ports -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("USB001")] // the first of the pooled ports
    [InlineData("USB002")]
    public void Pooled_queue_with_the_usb_printers_port_is_its_queue_and_keeps_its_ports(string devicePort)
    {
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, devicePort)
            .WithModelDriver(SampleConfig.P4030)
            .WithTicketForm();
        builder.Spooler.SeedUsbPort("USB001");
        builder.Spooler.SeedUsbPort("USB002");
        builder.WithQueue(Tickets, Driver4030, "USB001,USB002");
        var scenario = builder.Build();

        var run = scenario.Run();

        var candidate = Assert.Single(run.Detection.Candidates);
        Assert.Equal((TargetKind.UsbDevice, Tickets), (candidate.Kind, candidate.Queue?.Name));
        Assert.DoesNotContain(CreateQueue, Kinds(run.Plan!));
        Assert.DoesNotContain(ChangeQueuePort, Kinds(run.Plan!));
        Assert.DoesNotContain(run.Plan!.Findings, f => f.Code == FindingCode.QueuePortMismatch);
        Assert.True(run.Apply!.Succeeded);
        Assert.Empty(Writes(scenario, nameof(IPrintSpooler.AddQueue), nameof(IPrintSpooler.SetQueuePort)));
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Target: Tickets });
        Assert.Equal("USB001,USB002", Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).PortName);

        var verification = run.Verification!;
        Assert.Equal((ReadinessLevel.Ready, Tickets), (verification.Readiness, verification.Summary.QueueName));
        var port = Check(verification, VerificationItem.QueuePortCorrect);
        Assert.Equal((CheckStatus.Pass, devicePort), (port.Status, port.Expected));
    }

    [Fact]
    public void Queue_pooled_to_the_printers_new_port_after_the_plan_is_not_moved()
    {
        // Planned while the queue still printed to the old port only; IT then pooled it with the printer's new port.
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        string instance = SampleConfig.UsbInstanceId(SampleConfig.P4030);
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        scenario.Spooler.SeedUsbPort("USB002");
        scenario.Devices.Update(instance, d => d with { UsbPortName = "USB002" });
        var plan = session.Assess(target, default);
        Assert.Contains(ChangeQueuePort, Kinds(plan));
        var queue = Assert.Single(scenario.Spooler.Queues, q => q.Name == Driver4030);
        scenario.Spooler.SeedQueue(queue with { PortName = "USB001,USB002" }, scenario.Spooler.GetStoredDevMode(Driver4030, DevModeScope.Global));

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(apply, ChangeQueuePort));
        Assert.Empty(Writes(scenario, nameof(IPrintSpooler.SetQueuePort)));
        Assert.Equal("USB001,USB002", Assert.Single(scenario.Spooler.Queues, q => q.Name == Driver4030).PortName);
    }

    [Theory]
    [InlineData("USB001,USB002", PortKind.Usb)]
    [InlineData("IP_10.0.0.5, USB002", PortKind.Usb)]
    [InlineData("IP_10.0.0.5,IP_10.0.0.6", PortKind.Other)]
    [InlineData("USB001", PortKind.Usb)]
    [InlineData("IP_10.0.0.5", PortKind.StandardTcpIp)]
    public void Pooled_ports_are_usb_ports_when_one_of_them_is(string portName, PortKind expected)
    {
        PrinterPortInfo[] ports =
        [
            new() { Name = "IP_10.0.0.5", Kind = PortKind.StandardTcpIp, HostAddress = "10.0.0.5", PortNumber = 9100 },
            new() { Name = "IP_10.0.0.6", Kind = PortKind.StandardTcpIp, HostAddress = "10.0.0.6", PortNumber = 9100 },
        ];

        Assert.Equal(expected, QueueLocator.KindOf(portName, ports));
        Assert.True(QueueLocator.UsesPort(new PrintQueueInfo { Name = "Q", PortName = portName }, portName.Split(',')[^1].Trim()));
    }

    [Fact]
    public void A_connected_printer_keeps_its_pooled_queue_from_an_earlier_unit_of_another_model()
    {
        // The P-4531DN ticket printer on USB002 was replaced by a connected P-4030DN on USB001; the helpdesk gave 'Tickets' the
        // P-4030DN driver and pooled it over both ports. branch.json still names the old model, which picks the old unit.
        string oldUnit = SampleConfig.UsbInstanceId(SampleConfig.P4531, "OLDSERIAL01");
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4531, "USB002", present: false, instanceId: oldUnit)
            .WithModelDriver(SampleConfig.P4030)
            .WithModelDriver(SampleConfig.P4531)
            .WithTicketForm()
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4531 });
        builder.WithQueue(Tickets, Driver4030, "USB001,USB002");
        builder.WithCorrectDefaults(Tickets);
        var scenario = builder.Build();

        var run = scenario.Run();

        var connected = Assert.Single(run.Detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030)));
        Assert.Equal(Tickets, connected.Queue?.Name);
        var old = Assert.Single(run.Detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(oldUnit));
        Assert.Null(old.Queue);
        Assert.Empty(old.AdditionalQueues);
        Assert.False(old.IdentityConflict);
        Assert.DoesNotContain(run.Detection.Findings, f => f.Code == FindingCode.IdentityConflict);
        // As without the pool, whichever printer is set up: the connected unit's queue keeps its driver and settings.
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == Tickets);
        Assert.Equal(Driver4030, Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).DriverName);
    }

    [Fact]
    public void Two_connected_units_pooled_into_one_queue_are_both_its_printers()
    {
        string second = SampleConfig.UsbInstanceId(SampleConfig.P4030, "SERIALB002");
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4030, "USB002", instanceId: second)
            .WithModelDriver()
            .WithTicketForm();
        builder.WithQueue(Tickets, Driver4030, "USB001,USB002");
        var scenario = builder.Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        Assert.Equal(2, detection.Candidates.Count);
        Assert.All(detection.Candidates, c => Assert.Equal((TargetKind.UsbDevice, Tickets), (c.Kind, c.Queue?.Name)));
    }

    [Fact]
    public void Planner_and_worker_leave_a_pool_over_a_connected_printers_port_to_that_printer()
    {
        // The old unit is the target (chosen before the pool existed, or by its key): neither the plan nor the worker adopts the
        // connected unit's pooled queue for it or changes its driver, and neither adds a second queue on the old unit's port,
        // which the pool already prints to.
        string oldUnit = SampleConfig.UsbInstanceId(SampleConfig.P4531, "OLDSERIAL01");
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4531, "USB002", present: false, instanceId: oldUnit)
            .WithModelDriver(SampleConfig.P4030)
            .WithModelDriver(SampleConfig.P4531)
            .WithTicketForm();
        var scenario = builder.Build();
        var session = scenario.CreateSession();
        var target = Assert.Single(session.Detect(new DetectRequest(), default).Candidates, c => c.Key == PrinterCandidate.UsbKey(oldUnit));
        var plan = session.Assess(target, default);
        Assert.Contains(CreateQueue, Kinds(plan));
        scenario.Spooler.SeedQueue(new PrintQueueInfo { Name = Tickets, DriverName = Driver4030, PortName = "USB001,USB002", Attributes = PrinterAttributes.Local });

        var replanned = session.Assess(target, default);
        Assert.DoesNotContain(ChangeQueueDriver, Kinds(replanned));
        Assert.DoesNotContain(CreateQueue, Kinds(replanned));
        var blocker = Assert.Single(replanned.Blockers, b => b.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Error, Tickets, "USB001,USB002"), (blocker.Severity, blocker.Data["queue"], blocker.Data["port"]));
        Assert.Contains($"Queue '{Tickets}' (ports USB001,USB002), which the setup does not configure for this printer, already prints to USB002", blocker.Detail, StringComparison.Ordinal);
        Assert.Contains("nothing is changed", blocker.Detail, StringComparison.Ordinal);

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        var create = Result(apply, CreateQueue);
        Assert.Equal((ActionOutcome.Failed, FindingCode.QueueLeftUnchanged), (create.Outcome, create.FailureCode));
        Assert.Contains($"Queue '{Tickets}' (ports USB001,USB002), which the setup does not configure for this printer, already prints to USB002", create.Detail, StringComparison.Ordinal);
        Assert.Empty(Writes(scenario, nameof(IPrintSpooler.AddQueue)));
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == Tickets);
        Assert.Equal(Driver4030, Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).DriverName);
        Assert.Equal([Tickets], scenario.Spooler.Queues.Where(q => QueueLocator.UsesPort(q, "USB002")).Select(q => q.Name));
    }

    // ---- Pooled TCP/IP ports, LPR and Internet Ports --------------------------------------------------------------------

    [Fact]
    public void Pooled_queue_without_a_usb_port_is_left_unchanged_and_named_for_each_printer_it_prints_to()
    {
        var builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithTcpIpPort("10.0.0.5")
            .WithTcpIpPort("10.0.0.6");
        builder.WithQueue(Tickets, Driver4030, "IP_10.0.0.5,IP_10.0.0.6");
        var scenario = builder.Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        Assert.Empty(detection.Candidates);
        Assert.Equal([Tickets], detection.RejectedQueues);
        // The pool's IP addresses need no probe: they name their printers as they are, and those are not set up either way.
        Assert.Empty(scenario.Network.ProbedAddresses);
        foreach (string address in new[] { "10.0.0.5", "10.0.0.6" })
        {
            var finding = LeftUnchangedAt(detection, address);
            Assert.Equal((Severity.Warning, "IP_10.0.0.5,IP_10.0.0.6", SampleConfig.P4030), (finding.Severity, finding.Data["port"], finding.Data["driverModel"]));
            Assert.Contains("pooled ports IP_10.0.0.5,IP_10.0.0.6", finding.Detail, StringComparison.Ordinal);
            Assert.Contains($"If the printer at {address} is the ticket printer, un-pool the queue or put it on a single Standard TCP/IP port", finding.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pooled_tcpip_ticket_queue_at_the_configured_address_gets_no_second_queue_on_its_port()
    {
        // The branch pooled its two network ticket printers into one queue; branch.json names one of them.
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithTcpIpPort("10.0.0.5")
            .WithTcpIpPort("10.0.0.6")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithNetworkPrinter("10.0.0.6", SampleConfig.P4030)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" });
        builder.WithQueue(Tickets, Driver4030, "IP_10.0.0.5,IP_10.0.0.6");
        var scenario = builder.Build();

        var run = scenario.Run();

        // Neither printer the pool prints to is a candidate, so nothing is chosen at the configured address and nothing changes.
        Assert.Empty(run.Detection.Candidates);
        Assert.Equal([Tickets], run.Detection.RejectedQueues);
        Assert.Equal((SelectionReason.NoCandidates, "10.0.0.5"), (run.Selection.Reason, run.Selection.Address));
        Assert.Null(run.Plan);
        Assert.Empty(scenario.Spooler.Writes);
        var finding = LeftUnchangedAt(run.Detection, "10.0.0.5");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("The queue is not configured, and the printer is not set up automatically", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged && f.Severity == Severity.Info);
    }

    [Theory]
    [InlineData(SetupMode.Setup)]
    [InlineData(SetupMode.Repair)]
    public void Office_queue_of_a_printer_the_pooled_ticket_queue_prints_to_does_not_get_the_ticket_defaults(SetupMode mode)
    {
        // One printer at 10.0.0.5 has an A4 'Office' queue; 'Tickets' (already on the ticket form) is pooled over it and 10.0.0.6.
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.0.0.5", name: "Office")
            .WithTcpIpPort("10.0.0.6")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithNetworkPrinter("10.0.0.6", SampleConfig.P4030)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" });
        builder.WithQueue(Tickets, Driver4030, "IP_10.0.0.5,IP_10.0.0.6");
        builder.WithCorrectDefaults(Tickets);
        var scenario = builder.Build();
        var session = scenario.CreateSession(mode);

        var detection = session.Detect(new DetectRequest(), default);
        var selection = mode == SetupMode.Repair ? session.SelectRepairTarget(detection, null) : session.SelectAutomatically(detection);

        Assert.Empty(detection.Candidates);
        Assert.Equal(["Office", Tickets], detection.RejectedQueues.Order(StringComparer.Ordinal));
        Assert.Null(selection.Selected);
        Assert.Equal(SelectionReason.NoCandidates, selection.Reason);
        Assert.Null(selection.NeedsSetup);
        Assert.Equal(Severity.Warning, LeftUnchangedAt(detection, "10.0.0.5").Severity);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Queue_pooled_over_the_old_and_the_new_address_after_an_ip_change_keeps_the_new_address_from_a_second_queue()
    {
        // The printer moved from 10.0.0.6 (no longer answering) to 10.0.0.5; the helpdesk pooled the ticket queue over both, and
        // branch.json names the new address.
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithTcpIpPort("10.0.0.5")
            .WithTcpIpPort("10.0.0.6")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithNetworkPrinter("10.0.0.6", modelId: null, reachable: false, snmp: false)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" });
        builder.WithQueue(Tickets, Driver4030, "IP_10.0.0.6, IP_10.0.0.5");
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Empty(scenario.Spooler.Writes);
        var finding = LeftUnchangedAt(run.Detection, "10.0.0.5");
        Assert.Equal((Severity.Warning, "IP_10.0.0.6, IP_10.0.0.5"), (finding.Severity, finding.Data["port"]));
        Assert.Contains("prints to the printer at 10.0.0.5 on pooled ports IP_10.0.0.6, IP_10.0.0.5", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(Severity.Warning, LeftUnchangedAt(run.Detection, "10.0.0.6").Severity);
    }

    [Fact]
    public void Queue_pooled_over_a_usb_port_and_a_tcpip_port_stays_the_usb_printers_queue_and_its_network_printer_gets_no_second_queue()
    {
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithModelDriver()
            .WithTcpIpPort("10.0.0.5")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithTicketForm();
        builder.WithQueue(Tickets, Driver4030, "USB001,IP_10.0.0.5");

        // At the network printer's address nothing is set up: a queue added for it would duplicate the pool's TCP/IP member.
        var atAddress = builder.WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" }).Build();
        var run = atAddress.Run();

        var usb = Assert.Single(run.Detection.Candidates);
        Assert.Equal((TargetKind.UsbDevice, Tickets), (usb.Kind, usb.Queue?.Name));
        Assert.Empty(run.Detection.RejectedQueues);
        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Empty(atAddress.Spooler.Writes);
        var finding = LeftUnchangedAt(run.Detection, "10.0.0.5");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains($"it is the queue of the USB printer {SampleConfig.P4030} ({usb.Key}), and it also prints to the printer at 10.0.0.5", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("The queue is not configured", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Queue_pooled_over_a_usb_port_and_a_tcpip_port_is_configured_for_the_usb_printer_without_an_address()
    {
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithModelDriver()
            .WithTcpIpPort("10.0.0.5")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithTicketForm();
        builder.WithQueue(Tickets, Driver4030, "USB001,IP_10.0.0.5");
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Equal((SelectionReason.SingleCandidate, Tickets), (run.Selection.Reason, run.Selection.Selected?.Queue?.Name));
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Target: Tickets });
        Assert.Empty(Writes(scenario, nameof(IPrintSpooler.AddQueue), nameof(IPrintSpooler.AddStandardTcpIpPort), nameof(IPrintSpooler.SetQueuePort)));
        Assert.Equal("USB001,IP_10.0.0.5", Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).PortName);
        Assert.Equal(Severity.Warning, LeftUnchangedAt(run.Detection, "10.0.0.5").Severity);
    }

    [Theory]
    [InlineData("http://10.1.2.80:631/ipp/print")] // IPP over HTTP, an Internet Port
    [InlineData("10.1.2.80:Tickets")] // an LPR Port
    public void Ticket_queue_on_an_lpr_or_internet_port_to_the_configured_address_gets_no_second_port_or_queue(string portName)
    {
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkPrinter("10.1.2.80", SampleConfig.P4030)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = "10.1.2.80" });
        if (portName.StartsWith("http", StringComparison.Ordinal))
            builder.WithInternetPort(portName);
        else
            builder.WithLprPort(portName);
        builder.WithQueue(Tickets, Driver4030, portName);
        builder.WithCorrectDefaults(Tickets);
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Contains("10.1.2.80", scenario.Network.ProbedAddresses);
        Assert.Empty(run.Detection.Candidates);
        Assert.Equal([Tickets], run.Detection.RejectedQueues);
        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Empty(scenario.Spooler.Writes);
        var finding = LeftUnchangedAt(run.Detection, "10.1.2.80");
        Assert.Equal((Severity.Warning, portName), (finding.Severity, finding.Data["port"]));
        Assert.Contains($"Port {portName}, which the setup cannot configure", finding.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.1.2.80:Tickets", "10.1.2.80")]
    [InlineData("printsrv.branch.local:lp", "printsrv.branch.local")]
    [InlineData("2001:db8::50:Tickets", null)] // an IPv6 server makes "host:queue" ambiguous
    [InlineData("Tickets", null)]
    [InlineData("10.1:Tickets", null)]
    [InlineData("10.1.2.80:", null)]
    public void The_server_of_an_lpr_port_is_read_from_its_name_only_when_that_is_reliable(string portName, string? server)
    {
        PrinterPortInfo[] ports = [new() { Name = portName, Kind = PortKind.Lpr, MonitorName = "LPR Port" }];
        string[] hosts = server is null ? [] : [server];

        Assert.Equal(server, QueueLocator.LprServer(portName));
        Assert.Equal(hosts, QueueLocator.HostsOf(new PrintQueueInfo { Name = "Q", PortName = portName }, ports));
    }

    [Theory]
    [InlineData("http://10.1.2.80:631/ipp/print", "10.1.2.80")]
    [InlineData("https://printer12.branch.local/ipp/print", "printer12.branch.local")]
    [InlineData("http://[2001:db8::50]:631/ipp/print", "2001:db8::50")]
    [InlineData("ftp://10.1.2.80/queue", null)]
    [InlineData("http:/ipp", null)]
    public void The_host_of_an_internet_port_is_read_from_its_url(string portName, string? host)
    {
        string[] hosts = host is null ? [] : [host];

        Assert.Equal(host, QueueLocator.UrlHost(portName));
        // Also when Windows does not list the port: its URL names it an Internet Port.
        Assert.Equal(hosts, QueueLocator.HostsOf(new PrintQueueInfo { Name = "Q", PortName = portName }, []));
    }

    [Fact]
    public void Every_host_a_pooled_queue_prints_to_is_known()
    {
        PrinterPortInfo[] ports =
        [
            new() { Name = "IP_10.0.0.5", Kind = PortKind.StandardTcpIp, HostAddress = "10.0.0.5", PortNumber = 9100 },
            new() { Name = "IP_10.0.0.5_2", Kind = PortKind.StandardTcpIp, HostAddress = "10.0.0.5", PortNumber = 515 },
            new() { Name = "IP_unknown", Kind = PortKind.StandardTcpIp },
            new() { Name = "10.0.0.7:Tickets", Kind = PortKind.Lpr, MonitorName = "LPR Port" },
            new() { Name = "USB001", Kind = PortKind.Usb },
        ];
        var queue = new PrintQueueInfo { Name = "Q", PortName = "USB001,IP_10.0.0.5,IP_10.0.0.5_2,IP_unknown,10.0.0.7:Tickets,http://10.0.0.8/ipp" };

        Assert.Equal(["10.0.0.5", "10.0.0.7", "10.0.0.8"], QueueLocator.HostsOf(queue, ports));
        Assert.Null(QueueLocator.HostOf(queue, ports));
    }

    // ---- Ports that give no printer's host ------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\PC12\Tickets", PortKind.Local, "Local Port")] // a local port redirected to another computer's share
    [InlineData("IP_10.1.2.80", PortKind.StandardTcpIp, "Standard TCP/IP Port")] // its host could not be read
    [InlineData("2001:db8::50:Tickets", PortKind.Lpr, "LPR Port")] // the name does not give the server reliably
    public void Queue_of_a_supported_model_on_a_port_that_leads_to_no_known_printer_keeps_another_printer_from_being_chosen_without_asking(
        string portName, PortKind kind, string monitor)
    {
        var builder = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB001", correctDefaults: false)
            .WithModelDriver(SampleConfig.P4030);
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = portName, Kind = kind, MonitorName = monitor });
        builder.WithQueue(Tickets, Driver4030, portName);
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Equal([Tickets], run.Detection.RejectedQueues);
        var finding = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Info, Tickets, portName, SampleConfig.P4030), (finding.Severity, finding.Data["queue"], finding.Data["port"], finding.Data["driverModel"]));
        Assert.Contains("so the setup cannot link it to a printer; the queue is not configured", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("is not a USB, Standard TCP/IP or WSD port", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(SelectionReason.MultipleCandidates, run.Selection.Reason);
        Assert.Equal(SampleConfig.P5032, Assert.Single(run.Selection.Choices).Model!.Id);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Pooled_ports_that_give_no_host_are_named_as_pooled()
    {
        var builder = new ScenarioBuilder().WithModelDriver(SampleConfig.P4030);
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = "IP_a", Kind = PortKind.StandardTcpIp, MonitorName = "Standard TCP/IP Port" });
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = "IP_b", Kind = PortKind.StandardTcpIp, MonitorName = "Standard TCP/IP Port" });
        builder.WithQueue(Tickets, Driver4030, "IP_a,IP_b");
        var scenario = builder.Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("it is pooled over ports IP_a,IP_b, none of which is a USB port or names the host of a printer", finding.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\PC12\Tickets", PortKind.Local, "Local Port")]
    [InlineData("IP_10.1.2.80", PortKind.StandardTcpIp, "Standard TCP/IP Port")]
    [InlineData("2001:db8::50:Tickets", PortKind.Lpr, "LPR Port")]
    public void Queue_whose_port_gives_no_host_is_a_possible_duplicate_of_a_requested_printer_without_a_queue(string portName, PortKind kind, string monitor)
    {
        var builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkPrinter("10.1.2.80", SampleConfig.P4030)
            .WithNetworkPrinter("10.1.2.81", SampleConfig.P5032)
            .WithNetworkPrinter("10.1.2.82", modelId: null, snmp: false);
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = portName, Kind = kind, MonitorName = monitor });
        builder.WithQueue(Tickets, Driver4030, portName);
        var scenario = builder.Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = ["10.1.2.80", "10.1.2.81", "10.1.2.82"] }, default);

        // Of the queue's model, or of a model the printer does not report: a second queue may be added for the same printer.
        foreach (string address in new[] { "10.1.2.80", "10.1.2.82" })
        {
            var possible = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues && f.Data.GetValueOrDefault("address") == address);
            Assert.Equal((Severity.Warning, Tickets, SampleConfig.P4030), (possible.Severity, possible.Data["queue"], possible.Data["model"]));
            Assert.Contains($"its port {portName} cannot be linked to a printer, so they may be one printer", possible.Detail, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.DuplicateQueues && f.Data.GetValueOrDefault("address") == "10.1.2.81");
    }

    [Fact]
    public void Queue_on_an_internet_port_whose_driver_names_no_model_is_not_rejected()
    {
        var builder = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P5032, "USB001", correctDefaults: false);
        builder.WithInternetPort("http://10.1.2.80:631/ipp/print");
        builder.WithDriver(SampleConfig.MicrosoftPcl6ClassDriver).WithQueue("Office", SampleConfig.MicrosoftPcl6ClassDriver, "http://10.1.2.80:631/ipp/print");
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Empty(run.Detection.RejectedQueues);
        Assert.DoesNotContain(run.Detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal(SelectionReason.SingleCandidate, run.Selection.Reason);
    }

    [Theory]
    [InlineData("http://10.1.2.80:631/ipp/print", "10.1.2.80", true)]
    [InlineData("http://printer80.branch.local:631/ipp/print", "10.1.2.80", true)]
    [InlineData("http://10.1.2.81:631/ipp/print", "10.1.2.80", false)]
    public void A_class_driver_queue_on_an_internet_port_to_the_configured_printer_is_a_possible_duplicate(string url, string address, bool samePrinter)
    {
        // Windows' IPP class-driver queue blocks nothing (the printer is set up with the approved driver), but the new queue then
        // prints to the same printer as it.
        var builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithInternetPort(url)
            .WithQueue("TA P-4030DN (IPP)", SampleConfig.MicrosoftPcl6ClassDriver, url)
            .WithNetworkPrinter(address, SampleConfig.P4030)
            .WithNetworkAlias("printer80.branch.local", "10.1.2.80")
            .WithBranch(new BranchOverride { PrinterAddress = address });
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Equal(SelectionReason.PreferredByConfiguration, run.Selection.Reason);
        var possible = run.Detection.Findings.Where(f => f is { Code: FindingCode.DuplicateQueues, Severity: Severity.Warning }).ToList();
        if (samePrinter)
        {
            Finding duplicate = Assert.Single(possible);
            Assert.Equal((address, "TA P-4030DN (IPP)"), (duplicate.Data["address"], duplicate.Data["queue"]));
            Assert.Contains($"through port {url}, which the setup does not configure", duplicate.Detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(possible);
        }
    }

    // ---- No second queue on a port another queue prints to (planner and worker) -----------------------------------------

    private static PrinterCandidate NetworkTarget(Scenario scenario, string address) => new()
    {
        Key = PrinterCandidate.NetworkKey(address),
        Kind = TargetKind.NetworkAddress,
        Model = scenario.Config.FindModel(SampleConfig.P4030),
        Confidence = MatchConfidence.Exact,
        Connection = ConnectionType.TcpIp,
        NetworkAddress = address,
        NetworkIdentity = SampleConfig.NetworkIdentity(address, SampleConfig.P4030),
        PhysicallyAvailable = true,
    };

    [Fact]
    public void Planner_adds_no_queue_on_a_port_a_pooled_queue_already_prints_to()
    {
        // Defense in depth: detection gives such a printer no candidate, but a target chosen otherwise is not given a second queue.
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithTcpIpPort("10.0.0.5")
            .WithTcpIpPort("10.0.0.6")
            .WithTicketForm();
        builder.WithQueue(Tickets, Driver4030, "IP_10.0.0.5,IP_10.0.0.6");
        var scenario = builder.Build();
        var session = scenario.CreateSession();

        var plan = session.Assess(NetworkTarget(scenario, "10.0.0.5"), default);

        Assert.DoesNotContain(CreateQueue, Kinds(plan));
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(plan));
        var blocker = Assert.Single(plan.Blockers, b => b.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Error, Tickets, "IP_10.0.0.5,IP_10.0.0.6", "10.0.0.5"), (blocker.Severity, blocker.Data["queue"], blocker.Data["port"], blocker.Data["address"]));
        Assert.Contains($"Queue '{Tickets}' (ports IP_10.0.0.5,IP_10.0.0.6), which the setup does not configure for this printer, already prints to port IP_10.0.0.5", blocker.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot configure", blocker.Detail, StringComparison.Ordinal);
        Assert.Contains("nothing is changed", blocker.Detail, StringComparison.Ordinal);

        var apply = session.Apply(plan, ApplyScope.All, null, default);
        Assert.True(apply.Refused);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Worker_adds_no_queue_on_a_port_a_pooled_queue_prints_to_since_the_plan()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithTcpIpPort("10.0.0.5")
            .WithTcpIpPort("10.0.0.6")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" })
            .Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        var plan = session.Assess(target, default);
        Assert.Equal("IP_10.0.0.5", plan.Actions.Single(a => a.Kind == CreateQueue).Parameters["portName"]);
        scenario.Spooler.SeedQueue(new PrintQueueInfo { Name = Tickets, DriverName = Driver4030, PortName = "IP_10.0.0.5,IP_10.0.0.6", Attributes = PrinterAttributes.Local });

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        var create = Result(apply, CreateQueue);
        Assert.Equal((ActionOutcome.Failed, FindingCode.QueueLeftUnchanged), (create.Outcome, create.FailureCode));
        Assert.Contains($"Queue '{Tickets}' (ports IP_10.0.0.5,IP_10.0.0.6), which the setup does not configure for this printer, already prints to port IP_10.0.0.5", create.Detail, StringComparison.Ordinal);
        Assert.Empty(Writes(scenario, nameof(IPrintSpooler.AddQueue), nameof(IPrintSpooler.SetDevMode)));
        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, ConfigureGlobalDefaults));
    }

    [Fact]
    public void Planner_names_a_usb_printers_pool_on_the_port_as_a_queue_it_does_not_configure_for_this_printer()
    {
        // The pool is the connected USB printer's queue (configured for it), and it also prints to the printer at 10.0.0.5.
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithTcpIpPort("10.0.0.5")
            .WithTicketForm();
        builder.WithQueue(Tickets, SampleConfig.MicrosoftPcl6ClassDriver, "USB001,IP_10.0.0.5");
        var scenario = builder.Build();

        var plan = scenario.CreateSession().Assess(NetworkTarget(scenario, "10.0.0.5"), default);

        var blocker = Assert.Single(plan.Blockers, b => b.Code == FindingCode.QueueLeftUnchanged);
        Assert.Contains("which the setup does not configure for this printer, already prints to port IP_10.0.0.5", blocker.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot configure", blocker.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(CreateQueue, Kinds(plan));
    }

    // ---- A class-driver queue on ports the setup cannot configure ---------------------------------------------------------

    private const string ClassDriver = "Microsoft IPP Class Driver";

    /// <summary>
    /// One P-4030DN at 10.0.0.5 (SNMP exact): the TA driver's 'Office' on IP_10.0.0.5, and 'Tickets' with a class driver on a
    /// port the setup cannot configure that prints to the same printer: an Internet (IPP) Port, an LPR Port, or an Internet Port
    /// named by a host name that leads to the printer's address.
    /// </summary>
    private static ScenarioBuilder OfficeAndClassDriverQueueOnAnotherPort(string port, BranchOverride? branch)
    {
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(ClassDriver)
            .WithNetworkQueue("10.0.0.5", name: "Office")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithNetworkAlias("printer5.branch.local", "10.0.0.5")
            .WithTicketForm()
            .WithBranch(branch);
        if (port.StartsWith("http", StringComparison.Ordinal))
            builder.WithInternetPort(port);
        else
            builder.WithLprPort(port);
        return builder.WithQueue(Tickets, ClassDriver, port);
    }

    [Theory]
    [InlineData("http://10.0.0.5:631/ipp/print", "10.0.0.5", "10.0.0.5")]
    [InlineData("10.0.0.5:lp", "10.0.0.5", "10.0.0.5")]
    [InlineData("http://printer5.branch.local:631/ipp/print", "printer5.branch.local", "printer5.branch.local (also reached as 10.0.0.5)")]
    public void Class_driver_ticket_queue_on_an_lpr_or_internet_port_keeps_its_printer_from_being_set_up_at_the_configured_address(string port, string host, string printer)
    {
        // 'Tickets' already uses the ticket form: it is the printer's ticket queue, so 'Office' does not get the ticket defaults.
        var scenario = OfficeAndClassDriverQueueOnAnotherPort(port, new BranchOverride { PrinterAddress = "10.0.0.5" }).WithCorrectDefaults(Tickets).Build();

        var run = scenario.Run();

        Assert.Empty(run.Detection.Candidates);
        Assert.Equal(["Office", Tickets], run.Detection.RejectedQueues.Order(StringComparer.Ordinal));
        Assert.Equal((SelectionReason.NoCandidates, "10.0.0.5"), (run.Selection.Reason, run.Selection.Address));
        Assert.Null(run.Plan);
        Assert.Empty(scenario.Spooler.Writes);
        var finding = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Warning, Tickets, port, host, ClassDriver),
            (finding.Severity, finding.Data["queue"], finding.Data["port"], finding.Data["address"], finding.Data["driver"]));
        Assert.Contains($"Queue '{Tickets}' uses driver '{ClassDriver}', which names no single model, is already set up as the ticket printer and prints to the printer at {printer}",
            finding.Detail, StringComparison.Ordinal);
        Assert.Contains($"Port {port}, which the setup cannot configure", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("the printer is not set up automatically", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues && f.Severity != Severity.Info);
    }

    [Theory]
    [InlineData(SetupMode.Setup)]
    [InlineData(SetupMode.Repair)]
    public void Class_driver_ticket_queue_on_an_internet_port_keeps_its_printers_other_queue_from_being_configured_without_an_address(SetupMode mode)
    {
        var scenario = OfficeAndClassDriverQueueOnAnotherPort("http://10.0.0.5:631/ipp/print", branch: null).WithCorrectDefaults(Tickets).Build();
        var session = scenario.CreateSession(mode);

        var detection = session.Detect(new DetectRequest(), default);
        var selection = mode == SetupMode.Repair ? session.SelectRepairTarget(detection, null) : session.SelectAutomatically(detection);

        Assert.Empty(detection.Candidates);
        Assert.Contains(Tickets, detection.RejectedQueues);
        Assert.Null(selection.Selected);
        Assert.Equal(SelectionReason.NoCandidates, selection.Reason);
        Assert.Equal("10.0.0.5", Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged).Data["address"]);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Class_driver_queue_on_an_internet_port_beside_the_printers_queue_is_named_and_only_that_queue_is_configured()
    {
        // Neither queue is on the ticket paper yet: 'Office' is configured, and the report says that 'Tickets' prints there too.
        const string url = "http://10.0.0.5:631/ipp/print";
        var scenario = OfficeAndClassDriverQueueOnAnotherPort(url, new BranchOverride { PrinterAddress = "10.0.0.5" }).Build();

        var run = scenario.Run();

        Assert.Equal((SelectionReason.PreferredByConfiguration, "Office"), (run.Selection.Reason, run.Selection.Selected?.Queue?.Name));
        Assert.Empty(run.Detection.RejectedQueues);
        var duplicate = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, Tickets, url, ClassDriver, "10.0.0.5", PrinterCandidate.NetworkKey("10.0.0.5")),
            (duplicate.Severity, duplicate.Data["queue"], duplicate.Data["port"], duplicate.Data["driver"], duplicate.Data["address"], duplicate.Data["candidate"]));
        Assert.Equal($"Queue '{Tickets}' (driver '{ClassDriver}', which names no single model) also prints to the printer of queue 'Office' on 10.0.0.5 through port {url}, "
            + $"which the setup does not configure; only 'Office' is configured and '{Tickets}' keeps its settings. Check which queue employees print to.", duplicate.Detail);
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Target: "Office" });
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == Tickets);
    }

    [Fact]
    public void Class_driver_queue_on_an_internet_port_beside_the_printers_ticket_queue_is_information()
    {
        // 'Office' is already the queue on the ticket paper (set up by an earlier run), so it is configured again.
        var scenario = OfficeAndClassDriverQueueOnAnotherPort("http://10.0.0.5:631/ipp/print", new BranchOverride { PrinterAddress = "10.0.0.5" })
            .WithCorrectDefaults("Office")
            .Build();

        var run = scenario.Run();

        Assert.Equal("Office", run.Selection.Selected?.Queue?.Name);
        var duplicate = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal(Severity.Info, duplicate.Severity);
        Assert.Contains($"'Office' is configured because it is already set up as the ticket printer, and '{Tickets}' keeps its settings.", duplicate.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IP_10.0.0.5,IP_10.0.0.6")]
    [InlineData("USB001,IP_10.0.0.5")] // no printer is connected on USB001
    public void Class_driver_pool_over_the_configured_printers_tcpip_port_keeps_that_printer_from_being_set_up(string ports)
    {
        // As a pool with a supported model's driver: a queue added for 10.0.0.5 would print through the same port.
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(ClassDriver)
            .WithTcpIpPort("10.0.0.5")
            .WithTcpIpPort("10.0.0.6")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithNetworkPrinter("10.0.0.6", SampleConfig.P4030)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" });
        builder.Spooler.SeedUsbPort("USB001");
        builder.WithQueue(Tickets, ClassDriver, ports);
        var scenario = builder.Build();

        var run = scenario.Run();

        Assert.Empty(run.Detection.Candidates);
        Assert.Equal([Tickets], run.Detection.RejectedQueues);
        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Null(run.Plan);
        Assert.Empty(scenario.Spooler.Writes);
        var finding = LeftUnchangedAt(run.Detection, "10.0.0.5");
        Assert.Equal((Severity.Warning, ports, ClassDriver), (finding.Severity, finding.Data["port"], finding.Data["driver"]));
        Assert.False(finding.Data.ContainsKey("driverModel"));
        Assert.Contains($"Queue '{Tickets}' uses driver '{ClassDriver}', which names no single model, and prints to the printer at 10.0.0.5 on pooled ports {ports}", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    [Theory]
    [InlineData(SetupMode.Setup)]
    [InlineData(SetupMode.Repair)]
    public void Office_queue_of_a_printer_a_class_driver_pool_prints_to_does_not_get_the_ticket_defaults(SetupMode mode)
    {
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(ClassDriver)
            .WithNetworkQueue("10.0.0.5", name: "Office")
            .WithTcpIpPort("10.0.0.6")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" });
        builder.WithQueue(Tickets, ClassDriver, "IP_10.0.0.6,IP_10.0.0.5");
        var scenario = builder.Build();
        var session = scenario.CreateSession(mode);

        var detection = session.Detect(new DetectRequest(), default);
        var selection = mode == SetupMode.Repair ? session.SelectRepairTarget(detection, null) : session.SelectAutomatically(detection);

        Assert.Empty(detection.Candidates);
        Assert.Equal(["Office", Tickets], detection.RejectedQueues.Order(StringComparer.Ordinal));
        Assert.Equal(SelectionReason.NoCandidates, selection.Reason);
        Assert.Equal(Severity.Warning, LeftUnchangedAt(detection, "10.0.0.5").Severity);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Class_driver_pool_of_a_connected_usb_printer_stays_its_queue_and_its_network_printer_is_not_set_up(bool atAddress)
    {
        // At the network printer's address nothing is set up; without an address the USB printer's queue is configured.
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithModelDriver()
            .WithDriver(ClassDriver)
            .WithTcpIpPort("10.0.0.5")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithTicketForm()
            .WithBranch(atAddress ? new BranchOverride { PrinterAddress = "10.0.0.5" } : null);
        builder.WithQueue(Tickets, ClassDriver, "USB001,IP_10.0.0.5");
        var scenario = builder.Build();

        var run = scenario.Run();

        var usb = Assert.Single(run.Detection.Candidates);
        Assert.Equal((TargetKind.UsbDevice, Tickets), (usb.Kind, usb.Queue?.Name));
        Assert.Empty(run.Detection.RejectedQueues);
        var finding = LeftUnchangedAt(run.Detection, "10.0.0.5");
        Assert.Contains($"it is the queue of the USB printer {SampleConfig.P4030} ({usb.Key}), and it also prints to the printer at 10.0.0.5", finding.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        if (atAddress)
        {
            Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
            Assert.Empty(scenario.Spooler.Writes);
        }
        else
        {
            Assert.Equal((SelectionReason.SingleCandidate, usb.Key), (run.Selection.Reason, run.Selection.Selected?.Key));
            Assert.Empty(Writes(scenario, nameof(IPrintSpooler.AddQueue), nameof(IPrintSpooler.AddStandardTcpIpPort), nameof(IPrintSpooler.SetQueuePort)));
            Assert.Equal("USB001,IP_10.0.0.5", Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).PortName);
        }
    }

    // ---- An earlier unit's node next to the printer its queues print to now ----------------------------------------------

    private static readonly string OldUnit = SampleConfig.UsbInstanceId(SampleConfig.P4531, "OLDSERIAL01");
    private static readonly string Driver4531 = SampleConfig.DriverName(SampleConfig.P4531);

    /// <summary>An unsupported TA successor (P-4532DN) or another manufacturer's printer, connected on <paramref name="port"/>.</summary>
    private static PnpPrinterDevice ConnectedOther(string kind, string port) => kind == "hp"
        ? new PnpPrinterDevice
        {
            InstanceId = @"USBPRINT\HEWLETT-PACKARDHP_LA1234\7&1&0&1",
            Bus = PnpBus.Usb,
            HardwareIds = [@"USBPRINT\HEWLETT-PACKARDHP_LA1234"],
            FriendlyName = "HP LaserJet M404dn",
            Manufacturer = "HP",
            IsPresent = true,
            UsbPortName = port,
        }
        : new PnpPrinterDevice
        {
            InstanceId = @"USBPRINT\TA_UTAXP-4532DNFFBC\7&2B5A1C3&0&R9L0309954",
            Bus = PnpBus.Usb,
            HardwareIds = [@"USBPRINT\TA_UTAXP-4532DNFFBC", "TA_UTAXP-4532DNFFBC"],
            FriendlyName = "TA_UTAX P-4532DN",
            IsPresent = true,
            UsbPortName = port,
        };

    [Theory]
    [InlineData("unsupported-ta", SetupMode.Setup)]
    [InlineData("unsupported-ta", SetupMode.Repair)]
    [InlineData("hp", SetupMode.Setup)]
    [InlineData("hp", SetupMode.Repair)]
    public void Pool_over_a_connected_printer_of_any_model_is_not_claimed_by_an_earlier_units_node(string kind, SetupMode mode)
    {
        // The P-4531DN ticket printer (USB002, its node remains) was replaced by a printer the setup does not support, and the
        // helpdesk pooled 'Tickets' over the new printer's port: the printers on the pool's ports decide, as without the old node.
        var builder = new ScenarioBuilder()
            .WithDevice(ConnectedOther(kind, "USB001"))
            .WithUsbPrinter(SampleConfig.P4531, "USB002", present: false, instanceId: OldUnit)
            .WithModelDriver(SampleConfig.P4531)
            .WithTicketForm();
        builder.Spooler.SeedUsbPort("USB001");
        builder.WithQueue(Tickets, Driver4531, "USB001,USB002");
        builder.WithCorrectDefaults(Tickets);
        var scenario = builder.Build();
        var session = scenario.CreateSession(mode);

        var detection = session.Detect(new DetectRequest(), default);
        var selection = mode == SetupMode.Repair ? session.SelectRepairTarget(detection, null) : session.SelectAutomatically(detection);

        Assert.Null(Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(OldUnit)).Queue);
        Assert.Equal([Tickets], detection.RejectedQueues);
        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Warning, Tickets, ConnectedOther(kind, "USB001").InstanceId), (finding.Severity, finding.Data["queue"], finding.Data["device"]));
        Assert.Null(selection.Selected);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Theory]
    [InlineData(SetupMode.Setup)]
    [InlineData(SetupMode.Repair)]
    public void Pool_with_the_unsupported_successors_driver_keeps_that_driver_next_to_an_earlier_units_node(SetupMode mode)
    {
        // The helpdesk gave 'Tickets' the P-4532DN's own driver (not in the configuration) and pooled it over its port.
        const string driver4532 = "TA P-4532DN KX";
        var builder = new ScenarioBuilder()
            .WithDevice(ConnectedOther("unsupported-ta", "USB001"))
            .WithUsbPrinter(SampleConfig.P4531, "USB002", present: false, instanceId: OldUnit)
            .WithModelDriver(SampleConfig.P4531)
            .WithDriver(driver4532)
            .WithTicketForm();
        builder.Spooler.SeedUsbPort("USB001");
        builder.WithQueue(Tickets, driver4532, "USB001,USB002");
        var scenario = builder.Build();
        var session = scenario.CreateSession(mode);

        var detection = session.Detect(new DetectRequest(), default);
        var selection = mode == SetupMode.Repair ? session.SelectRepairTarget(detection, null) : session.SelectAutomatically(detection);
        var plan = selection.Selected is { } target ? session.Assess(target, default) : null;
        if (plan is not null)
            session.Apply(plan, ApplyScope.All, null, default);

        Assert.Null(Assert.Single(detection.Candidates).Queue);
        Assert.DoesNotContain(ChangeQueueDriver, plan is null ? [] : Kinds(plan));
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == Tickets);
        Assert.Equal(driver4532, Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).DriverName);
        Assert.Empty(Writes(scenario, nameof(IPrintSpooler.AddQueue)));
    }

    /// <summary>
    /// The replaced-unit PC: the P-4531DN ticket printer on USB002 was replaced by a P-4030DN on <paramref name="newUnitPort"/>;
    /// 'Tickets' has the P-4030DN driver and correct defaults, on <paramref name="pool"/>. branch.json still names the old model.
    /// </summary>
    private static ScenarioBuilder ReplacedUnitPc(bool newUnitPresent, string newUnitPort = "USB001", string pool = "USB001,USB002")
    {
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, newUnitPort, present: newUnitPresent)
            .WithUsbPrinter(SampleConfig.P4531, "USB002", present: false, instanceId: OldUnit)
            .WithModelDriver(SampleConfig.P4030)
            .WithModelDriver(SampleConfig.P4531)
            .WithTicketForm()
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4531 });
        builder.Spooler.SeedUsbPort("USB001");
        builder.WithQueue(Tickets, Driver4030, pool);
        builder.WithCorrectDefaults(Tickets);
        return builder;
    }

    [Fact]
    public void Setup_adds_no_queue_on_an_earlier_units_port_that_the_connected_units_pool_prints_to()
    {
        // The stale modelId picks the old unit, which has no queue: a queue on USB002 would print to the same port as the pool.
        var scenario = ReplacedUnitPc(newUnitPresent: true).Build();

        var run = scenario.Run();

        Assert.Equal(PrinterCandidate.UsbKey(OldUnit), run.Selection.Selected?.Key);
        Assert.DoesNotContain(CreateQueue, Kinds(run.Plan!));
        var blocker = Assert.Single(run.Plan!.Blockers, b => b.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Error, Tickets, "USB001,USB002", OldUnit, PrinterCandidate.UsbKey(OldUnit)),
            (blocker.Severity, blocker.Data["queue"], blocker.Data["port"], blocker.Data["device"], blocker.Data["candidate"]));
        Assert.Contains($"already prints to USB002, the port of the USB printer {SampleConfig.P4531} ({PrinterCandidate.UsbKey(OldUnit)})", blocker.Detail, StringComparison.Ordinal);
        Assert.True(run.Apply!.Refused);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Equal([Tickets], scenario.Spooler.Queues.Where(q => QueueLocator.UsesPort(q, "USB002")).Select(q => q.Name));
    }

    [Fact]
    public void Diagnostics_does_not_say_that_setup_would_add_a_queue_on_a_port_a_pool_prints_to()
    {
        var scenario = ReplacedUnitPc(newUnitPresent: true).Build();
        var session = scenario.CreateSession(SetupMode.Diagnostics);
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;

        var plan = session.Assess(target, default);

        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.QueueLeftUnchanged);
        Assert.DoesNotContain(plan.Findings, f => f.Code == FindingCode.QueueMissing);
    }

    [Theory]
    [InlineData(SetupMode.Setup)]
    [InlineData(SetupMode.Repair)]
    public void Switched_off_successor_keeps_its_pooled_queue_from_an_earlier_unit_of_another_model(SetupMode mode)
    {
        // Neither unit is connected (a run at night): the queue's driver names the successor's model, so the old unit does not
        // claim the pool, and the stale modelId changes nothing.
        var scenario = ReplacedUnitPc(newUnitPresent: false).Build();
        var session = scenario.CreateSession(mode);

        var detection = session.Detect(new DetectRequest(), default);
        var selection = mode == SetupMode.Repair ? session.SelectRepairTarget(detection, null) : session.SelectAutomatically(detection);
        var plan = selection.Selected is { } target ? session.Assess(target, default) : null;
        if (plan is not null)
            session.Apply(plan, ApplyScope.All, null, default);

        var old = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(OldUnit));
        Assert.Null(old.Queue);
        Assert.False(old.IdentityConflict);
        Assert.Equal(Tickets, Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030))).Queue?.Name);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.IdentityConflict);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Equal(Driver4030, Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).DriverName);
    }

    [Fact]
    public void Successor_on_the_earlier_units_own_port_keeps_its_queue()
    {
        // The USB monitor gave the connected P-4030DN the old P-4531DN's port, and 'Tickets' is on that port only.
        var scenario = ReplacedUnitPc(newUnitPresent: true, newUnitPort: "USB002", pool: "USB002").Build();

        var run = scenario.Run();

        Assert.Null(Assert.Single(run.Detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(OldUnit)).Queue);
        Assert.Equal(Tickets, Assert.Single(run.Detection.Candidates, c => c.Key == PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030))).Queue?.Name);
        Assert.Contains(run.Plan?.Blockers ?? [], b => b.Code == FindingCode.QueueLeftUnchanged);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Equal(Driver4030, Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).DriverName);
    }

    [Fact]
    public void Planner_that_cannot_read_the_devices_keeps_to_the_queues_detection_gave_the_printer()
    {
        var scenario = ReplacedUnitPc(newUnitPresent: true).Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        Assert.Equal((PrinterCandidate.UsbKey(OldUnit), (PrintQueueInfo?)null), (target.Key, target.Queue));

        // A transient SetupAPI failure after detection: which printer uses the pool cannot be checked.
        scenario.Devices.Failure = new InvalidOperationException("SetupDiGetClassDevs failed (simulated).");
        var plan = session.Assess(target, default);
        var apply = session.Apply(plan, ApplyScope.All, null, default);

        Assert.DoesNotContain(ChangeQueueDriver, Kinds(plan));
        Assert.DoesNotContain(CreateQueue, Kinds(plan));
        var unreadable = Assert.Single(plan.Findings, f => f.Code == FindingCode.OperationFailed);
        Assert.Equal(Severity.Warning, unreadable.Severity);
        Assert.Contains("The printer devices could not be read (SetupDiGetClassDevs failed (simulated).); detection found no queue for the printer", unreadable.Detail, StringComparison.Ordinal);
        Assert.True(apply.Refused);
        Assert.Equal(Driver4030, Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).DriverName);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Planner_that_cannot_read_the_devices_still_finds_the_queue_detection_gave_the_printer()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        scenario.Devices.Failure = new InvalidOperationException("SetupDiGetClassDevs failed (simulated).");

        var plan = session.Assess(target, default);

        Assert.DoesNotContain(CreateQueue, Kinds(plan));
        Assert.Contains(ConfigureGlobalDefaults, Kinds(plan));
        Assert.Equal(Driver4030, plan.Actions.Single(a => a.Kind == ConfigureGlobalDefaults).Parameters["queueName"]);
        Assert.Contains($"only the queues detection found for the printer are used ('{Driver4030}')", Assert.Single(plan.Findings, f => f.Code == FindingCode.OperationFailed).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_that_cannot_read_the_devices_adopts_no_queue_detection_did_not_give_the_printer()
    {
        // Planned before the pool existed (no queue on USB002 then); the pool appears and the worker cannot read the devices.
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4531, "USB002", present: false, instanceId: OldUnit)
            .WithModelDriver(SampleConfig.P4030)
            .WithModelDriver(SampleConfig.P4531)
            .WithTicketForm();
        var scenario = builder.Build();
        var session = scenario.CreateSession();
        var target = Assert.Single(session.Detect(new DetectRequest(), default).Candidates, c => c.Key == PrinterCandidate.UsbKey(OldUnit));
        var plan = session.Assess(target, default);
        Assert.Equal("USB002", plan.Actions.Single(a => a.Kind == CreateQueue).Parameters["portName"]);
        scenario.Spooler.SeedQueue(new PrintQueueInfo { Name = Tickets, DriverName = Driver4030, PortName = "USB001,USB002", Attributes = PrinterAttributes.Local });
        scenario.Devices.Failure = new InvalidOperationException("SetupDiGetClassDevs failed (simulated).");

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        var create = Result(apply, CreateQueue);
        Assert.Equal((ActionOutcome.Failed, FindingCode.QueueLeftUnchanged), (create.Outcome, create.FailureCode));
        var unreadable = Assert.Single(apply.Findings, f => f.Code == FindingCode.OperationFailed);
        Assert.Equal(Severity.Warning, unreadable.Severity);
        Assert.Contains("only the queues detection found for the printer are used, and no other queue is adopted or given another driver for it", unreadable.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == Tickets);
        Assert.Empty(Writes(scenario, nameof(IPrintSpooler.AddQueue)));
        Assert.Equal(Driver4030, Assert.Single(scenario.Spooler.Queues, q => q.Name == Tickets).DriverName);
    }

    // ---- A mixed pool whose network printer is not a supported model -----------------------------------------------------

    [Theory]
    [InlineData(false)] // the network printer's address is requested, so it is probed
    [InlineData(true)] // the network printer also has an office queue, whose host is probed
    public void Mixed_pool_of_the_usb_printer_is_not_said_to_be_left_unconfigured_when_its_network_printer_names_another_model(bool officeQueue)
    {
        var builder = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithModelDriver()
            .WithTcpIpPort("10.0.0.5")
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = "10.0.0.5",
                ResolvedAddress = "10.0.0.5",
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "TA_UTAX Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41",
                VendorModelName = "P-4532DN",
            })
            .WithTicketForm();
        builder.WithQueue(Tickets, Driver4030, "USB001,IP_10.0.0.5");
        if (officeQueue)
            builder.WithQueue("Office", Driver4030, "IP_10.0.0.5");
        var scenario = builder.Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = officeQueue ? [] : ["10.0.0.5"] }, default);

        Assert.Contains("10.0.0.5", scenario.Network.ProbedAddresses);
        var usb = Assert.Single(detection.Candidates);
        Assert.Equal((TargetKind.UsbDevice, Tickets), (usb.Kind, usb.Queue?.Name));
        var mismatch = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Equal(Severity.Warning, mismatch.Severity);
        Assert.Contains($"Queue '{Tickets}' stays the queue of the USB printer {SampleConfig.P4030} ({usb.Key}) and also prints to this device.", mismatch.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{Tickets}') are not configured", mismatch.Detail, StringComparison.Ordinal);
        if (officeQueue)
        {
            Assert.Contains("which is not a supported model; its queues ('Office') are not configured.", mismatch.Detail, StringComparison.Ordinal);
            Assert.Equal(["Office"], detection.RejectedQueues);
        }
        else
        {
            Assert.Equal("The device at 10.0.0.5 reports itself as 'P-4532DN', which is not a supported model. "
                + $"Queue '{Tickets}' stays the queue of the USB printer {SampleConfig.P4030} ({usb.Key}) and also prints to this device.", mismatch.Detail);
            Assert.Empty(detection.RejectedQueues);
        }
    }
}
