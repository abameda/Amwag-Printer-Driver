using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// A USB or WSD queue's driver does not make its printer that model when the printer's own device node says otherwise: a
/// TA/UTAX/Kyocera node that names an unsupported model, several models or another model wins over the driver, and so does a
/// connected printer of another manufacturer on a USB queue's port. A USB node is linked to its queue by its port, connected
/// or not; a WSD port cannot be linked to its printer, so for WSD queues the rule only applies when no other WSD queue can be
/// that printer's own. Such a queue is left unchanged (QueueLeftUnchanged) and may be the ticket printer's (RejectedQueues).
/// </summary>
public sealed class QueueDeviceIdentityTests
{
    private const string Queue = "AMWAG Ticket Printer";
    /// <summary>The P-4532DN (an unsupported successor model), named as its 1284-derived hardware ID in KmInstall4.ini.</summary>
    private const string P4532HardwareIdName = "TA_UTAXP-4532DNFFBC";
    private const string TicketPort = "WSD-5d6a1f3c-1111-2222-3333-444455556666";
    private const string OfficePort = "WSD-0b1c2d3e-7777-8888-9999-000011112222";

    private static DetectionResult Detect(Scenario scenario) => scenario.CreateSession().Detect(new DetectRequest(), default);

    /// <summary>The finding that names the queue left unchanged; the codes of a device that is itself the problem are not used for it.</summary>
    private static Finding LeftUnchanged(DetectionResult detection, string queue = Queue)
    {
        Assert.DoesNotContain(detection.Findings, f => f.Code is FindingCode.UnsupportedPrinterDetected or FindingCode.IdentityConflict && f.Data.ContainsKey("queue"));
        return Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged && f.Data.GetValueOrDefault("queue") == queue);
    }

    // ---- USB: the node on the queue's port ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // switched off: the node still names the printer
    [InlineData(true)]
    public void Usb_queue_on_the_port_of_an_unsupported_printer_is_not_configured_as_its_drivers_model(bool connected)
    {
        var scenario = new ScenarioBuilder()
            .WithDevice(UsbNode(P4532HardwareIdName, "TA_UTAX P-4532DN", connected))
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB001", name: Queue)
            .WithTicketForm()
            .Build();

        var run = scenario.Run();

        Assert.Empty(run.Detection.Candidates);
        var finding = LeftUnchanged(run.Detection);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal((Queue, "USB001", SampleConfig.P4531), (finding.Data["queue"], finding.Data["port"], finding.Data["driverModel"]));
        Assert.Contains("not a supported model", finding.Detail, StringComparison.Ordinal);
        Assert.Equal([Queue], run.Detection.RejectedQueues);
        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Usb_queue_on_the_port_of_another_manufacturers_connected_printer_is_left_unchanged_and_reported()
    {
        // The ticket queue was moved to the HP's port as a workaround, or the USB monitor gave the HP the ticket printer's port.
        var scenario = new ScenarioBuilder()
            .WithDevice(ConnectedHp("USB001"))
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB001", name: Queue)
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        var finding = LeftUnchanged(detection);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal((Queue, "USB001", ConnectedHp("USB001").InstanceId, SampleConfig.P4531),
            (finding.Data["queue"], finding.Data["port"], finding.Data["device"], finding.Data["driverModel"]));
        Assert.Contains("'HP LaserJet M404dn'", finding.Detail, StringComparison.Ordinal);
        Assert.Equal([Queue], detection.RejectedQueues);
    }

    [Fact]
    public void Usb_ticket_queue_on_a_connected_hp_keeps_the_only_other_ta_printer_from_being_configured_without_asking()
    {
        // As over TCP/IP (an HP answering at the queue's address), and as with the HP switched off (the queue stays a choice).
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB001", correctDefaults: false)
            .WithDevice(ConnectedHp("USB002"))
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB002", name: "Tickets")
            .Build();

        var run = scenario.Run();

        Assert.Equal(["Tickets"], run.Detection.RejectedQueues);
        Assert.Equal("USB002", LeftUnchanged(run.Detection, "Tickets").Data["port"]);
        Assert.Equal(SelectionReason.MultipleCandidates, run.Selection.Reason);
        Assert.Equal(SampleConfig.P5032, Assert.Single(run.Selection.Choices).Model!.Id);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Usb_queue_on_the_port_of_a_printer_matching_several_models_is_not_configured()
    {
        // A node whose IDs are approved for two models (a configuration error): the driver must not decide between them.
        var node = SampleConfig.UsbDevice(SampleConfig.P4030, present: false) with
        {
            HardwareIds = [SampleConfig.UsbHardwareId(SampleConfig.P4030), SampleConfig.UsbHardwareId(SampleConfig.P4531)],
        };
        var scenario = new ScenarioBuilder()
            .WithDevice(node)
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB001", name: Queue)
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        Assert.Contains("several models", LeftUnchanged(detection).Detail, StringComparison.Ordinal);
        Assert.Equal([Queue], detection.RejectedQueues);
    }

    [Fact]
    public void Usb_queue_on_a_port_whose_node_has_no_ids_stays_driver_only()
    {
        var scenario = new ScenarioBuilder()
            .WithDevice(new PnpPrinterDevice { InstanceId = @"USBPRINT\UNKNOWN\1", Bus = PnpBus.Usb, FriendlyName = "TA_UTAX P-4532DN", UsbPortName = "USB001" })
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB001", name: Queue)
            .Build();

        var candidate = Assert.Single(Detect(scenario).Candidates);

        Assert.Equal((PrinterCandidate.QueueKey(Queue), SampleConfig.P4531, MatchConfidence.DriverOnly), (candidate.Key, candidate.Model?.Id, candidate.Confidence));
    }

    [Fact]
    public void Usb_queue_on_the_port_of_another_vendors_switched_off_printer_stays_driver_only()
    {
        // Another manufacturer's node says nothing about the queue's printer once it is not connected: the port may be the
        // switched-off ticket printer's again.
        var hp = ConnectedHp("USB001") with { IsPresent = false };
        var scenario = new ScenarioBuilder().WithDevice(hp).WithModelDriver(SampleConfig.P4531).WithUsbQueue(SampleConfig.P4531, "USB001", name: Queue).Build();

        var detection = Detect(scenario);

        Assert.Equal(MatchConfidence.DriverOnly, Assert.Single(detection.Candidates).Confidence);
        Assert.DoesNotContain(detection.Findings, f => f.Code is FindingCode.UnsupportedPrinterDetected or FindingCode.QueueLeftUnchanged);
        Assert.Empty(detection.RejectedQueues);
    }

    // ---- WSD: connected WSD printers -------------------------------------------------------------------------------------

    [Fact]
    public void Wsd_queue_next_to_a_connected_wsd_printer_of_an_unsupported_model_is_not_configured()
    {
        var scenario = WsdQueue(new ScenarioBuilder(), Queue, SampleConfig.DriverName(SampleConfig.P4531), TicketPort, SampleConfig.P4531)
            .WithTicketForm()
            .WithDevice(WsdNode(P4532HardwareIdName, "TA_UTAX P-4532DN"))
            .Build();

        var run = scenario.Run();

        Assert.Empty(run.Detection.Candidates);
        var finding = LeftUnchanged(run.Detection);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal((Queue, TicketPort, SampleConfig.P4531), (finding.Data["queue"], finding.Data["port"], finding.Data["driverModel"]));
        Assert.Contains("switch it on", finding.Detail, StringComparison.Ordinal);
        Assert.Equal([Queue], run.Detection.RejectedQueues);
        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Wsd_queue_next_to_a_wsd_printer_matching_several_models_is_not_configured()
    {
        var ambiguous = WsdNode(SampleConfig.HardwareIdName(SampleConfig.P4030), "TA Triumph-Adler P-4030DN") with
        {
            HardwareIds = [SampleConfig.WsdHardwareId(SampleConfig.P4030), SampleConfig.WsdHardwareId(SampleConfig.P5032)],
        };
        var scenario = WsdQueue(new ScenarioBuilder(), Queue, SampleConfig.DriverName(SampleConfig.P4531), TicketPort, SampleConfig.P4531)
            .WithDevice(ambiguous)
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        Assert.Contains("several models", LeftUnchanged(detection).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Wsd_queue_next_to_a_wsd_printer_of_another_model_without_a_queue_of_its_own_is_not_configured()
    {
        var scenario = WsdQueue(new ScenarioBuilder(), Queue, SampleConfig.DriverName(SampleConfig.P4531), TicketPort, SampleConfig.P4531)
            .WithDevice(WsdNode(SampleConfig.HardwareIdName(SampleConfig.P5032), "TA_UTAX P-5032DN"))
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        // Not IdentityConflict: that code means the setup followed the device's model, and this queue is not changed at all.
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.IdentityConflict);
        var finding = LeftUnchanged(detection);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal((Queue, SampleConfig.P4531, SampleConfig.P5032), (finding.Data["queue"], finding.Data["driverModel"], finding.Data["deviceModel"]));
        Assert.Equal([Queue], detection.RejectedQueues);
    }

    [Fact]
    public void Wsd_queue_stays_driver_only_when_the_other_wsd_printer_has_a_queue_of_its_own()
    {
        // Windows set up a WSD queue for each printer: the switched-off ticket printer's and the office P-5032DN's.
        var builder = WsdQueue(new ScenarioBuilder(), Queue, SampleConfig.DriverName(SampleConfig.P4531), TicketPort, SampleConfig.P4531);
        var scenario = WsdQueue(builder, "Office", SampleConfig.DriverName(SampleConfig.P5032), OfficePort, SampleConfig.P5032)
            .WithDevice(WsdNode(SampleConfig.HardwareIdName(SampleConfig.P5032), "TA_UTAX P-5032DN"))
            .Build();

        var detection = Detect(scenario);

        var ticket = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.QueueKey(Queue));
        Assert.Equal((SampleConfig.P4531, MatchConfidence.DriverOnly, true), (ticket.Model?.Id, ticket.Confidence, ticket.IsEligible));
        Assert.Equal(MatchConfidence.Exact, Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.QueueKey("Office")).Confidence);
        Assert.DoesNotContain(detection.Findings, f => f.Code is FindingCode.IdentityConflict or FindingCode.UnsupportedPrinterDetected or FindingCode.QueueLeftUnchanged);
        Assert.Empty(detection.RejectedQueues);
    }

    [Fact]
    public void Wsd_queue_stays_driver_only_when_the_unsupported_wsd_printer_has_a_queue_of_its_own()
    {
        const string officeDriver = "TA P-4532DN KX";
        var builder = WsdQueue(new ScenarioBuilder(), Queue, SampleConfig.DriverName(SampleConfig.P4531), TicketPort, SampleConfig.P4531);
        var scenario = WsdQueue(builder.WithDriver(officeDriver), "Office", officeDriver, OfficePort, modelId: null)
            .WithDevice(WsdNode(P4532HardwareIdName, "TA_UTAX P-4532DN"))
            .Build();

        var candidate = Assert.Single(Detect(scenario).Candidates);

        Assert.Equal((PrinterCandidate.QueueKey(Queue), MatchConfidence.DriverOnly), (candidate.Key, candidate.Confidence));
    }

    [Fact]
    public void Wsd_queue_whose_own_printer_is_connected_is_exact_next_to_an_unsupported_one()
    {
        var scenario = WsdQueue(new ScenarioBuilder(), Queue, SampleConfig.DriverName(SampleConfig.P4531), TicketPort, SampleConfig.P4531)
            .WithDevice(WsdNode(P4532HardwareIdName, "TA_UTAX P-4532DN"))
            .WithDevice(WsdNode(SampleConfig.HardwareIdName(SampleConfig.P4531), "TA_UTAX P-4531DN"))
            .Build();

        var candidate = Assert.Single(Detect(scenario).Candidates);

        Assert.Equal((SampleConfig.P4531, MatchConfidence.Exact), (candidate.Model?.Id, candidate.Confidence));
    }

    [Fact]
    public void Wsd_queue_next_to_another_vendors_wsd_printer_stays_driver_only()
    {
        var hp = new PnpPrinterDevice { InstanceId = @"WSDPRINT\HPLASERJET_M404DN1A2B\1", Bus = PnpBus.Wsd, HardwareIds = [@"WSDPRINT\HPLASERJET_M404DN1A2B"], FriendlyName = "HP LaserJet M404dn", IsPresent = true };
        var scenario = WsdQueue(new ScenarioBuilder(), Queue, SampleConfig.DriverName(SampleConfig.P4531), TicketPort, SampleConfig.P4531)
            .WithDevice(hp)
            .Build();

        Assert.Equal(MatchConfidence.DriverOnly, Assert.Single(Detect(scenario).Candidates).Confidence);
    }

    /// <summary>A queue on a WSD port (the port is created too); with a model, that model's driver is installed.</summary>
    private static ScenarioBuilder WsdQueue(ScenarioBuilder builder, string name, string driverName, string port, string? modelId)
    {
        if (modelId is not null)
            builder.WithModelDriver(modelId);
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = port, Kind = PortKind.Wsd, MonitorName = "WSD Port" });
        return builder.WithQueue(name, driverName, port);
    }

    private static PnpPrinterDevice WsdNode(string hardwareIdName, string friendlyName) => new()
    {
        InstanceId = $@"WSDPRINT\{hardwareIdName}\5&2A1B3C4D&0&uuid:{hardwareIdName}",
        Bus = PnpBus.Wsd,
        HardwareIds = [@"WSDPRINT\" + hardwareIdName],
        FriendlyName = friendlyName,
        IsPresent = true,
    };

    /// <summary>An HP printer on the USB port: its IDs carry no TA/UTAX/Kyocera prefix and match no supported model.</summary>
    private static PnpPrinterDevice ConnectedHp(string port) => new()
    {
        InstanceId = @"USBPRINT\HEWLETT-PACKARDHP_LA1234\7&1&0&1",
        Bus = PnpBus.Usb,
        HardwareIds = [@"USBPRINT\HEWLETT-PACKARDHP_LA1234"],
        FriendlyName = "HP LaserJet M404dn",
        Manufacturer = "HP",
        IsPresent = true,
        UsbPortName = port,
    };

    private static PnpPrinterDevice UsbNode(string hardwareIdName, string friendlyName, bool present) => new()
    {
        InstanceId = $@"USBPRINT\{hardwareIdName}\7&2B5A1C3&0&R9L0309954",
        Bus = PnpBus.Usb,
        HardwareIds = [@"USBPRINT\" + hardwareIdName, hardwareIdName],
        FriendlyName = friendlyName,
        IsPresent = present,
        UsbPortName = "USB001",
    };
}
