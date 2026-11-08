using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class DetectionTests
{
    private const string Address = "10.20.30.40";

    private static DetectionResult Detect(Scenario scenario, params string[] addresses) =>
        scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = addresses }, CancellationToken.None);

    // ---- USB -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Connected_usb_printer_is_an_exact_candidate_with_its_queue()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();

        var candidate = Assert.Single(Detect(scenario).Candidates);

        Assert.Equal(PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030)), candidate.Key);
        Assert.Equal(TargetKind.UsbDevice, candidate.Kind);
        Assert.Equal(ConnectionType.Usb, candidate.Connection);
        Assert.Equal(MatchConfidence.Exact, candidate.Confidence);
        Assert.Equal(SampleConfig.P4030, candidate.Model!.Id);
        Assert.True(candidate.PhysicallyAvailable);
        Assert.True(candidate.IsEligible);
        Assert.Equal(SampleConfig.DriverName(SampleConfig.P4030), candidate.Queue!.Name);
        Assert.Contains(candidate.Evidence, e => e.Contains("approved hardware ID", StringComparison.Ordinal));
    }

    [Fact]
    public void Recorded_but_unplugged_usb_printer_is_a_candidate_that_is_not_available()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(present: false).WithModelDriver().WithUsbQueue().Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal(TargetKind.UsbDevice, candidate.Kind);
        Assert.False(candidate.PhysicallyAvailable);
        Assert.NotNull(candidate.Queue);
        Assert.True(Has(detection.Findings, FindingCode.DeviceNotConnected, Severity.Warning));
    }

    [Fact]
    public void Usb_queue_without_a_known_device_is_a_driver_only_candidate()
    {
        var scenario = new ScenarioBuilder().WithModelDriver(SampleConfig.P4531).WithUsbQueue(SampleConfig.P4531, "USB003").Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal(PrinterCandidate.QueueKey(SampleConfig.DriverName(SampleConfig.P4531)), candidate.Key);
        Assert.Equal(TargetKind.ExistingQueue, candidate.Kind);
        Assert.Equal(MatchConfidence.DriverOnly, candidate.Confidence);
        Assert.Equal(SampleConfig.P4531, candidate.Model!.Id);
        Assert.False(candidate.PhysicallyAvailable);
        Assert.True(candidate.IsEligible);
        Assert.True(Has(detection.Findings, FindingCode.DeviceNotConnected, Severity.Warning));
    }

    [Fact]
    public void Universal_driver_alone_does_not_identify_a_model()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(c => c with { Models = c.Models.Select(m => m with { AcceptedDrivers = [.. m.AcceptedDrivers, new AcceptedDriver { Name = "TA Universal KX" }] }).ToList() })
            .WithDriver("TA Universal KX")
            .WithUsbQueue(name: "Ticket", driverName: "TA Universal KX")
            .Build();

        Assert.Empty(Detect(scenario).Candidates);
    }

    [Fact]
    public void Queue_on_a_port_of_another_connected_printer_is_not_a_candidate()
    {
        var other = new PnpPrinterDevice
        {
            InstanceId = @"USBPRINT\HEWLETT-PACKARDHP_LA1234\1",
            Bus = PnpBus.Usb,
            HardwareIds = [@"USBPRINT\HEWLETT-PACKARDHP_LA1234"],
            IsPresent = true,
            UsbPortName = "USB001",
        };
        var scenario = new ScenarioBuilder().WithDevice(other).WithModelDriver().WithUsbQueue().Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        // Not silently dropped: the queue may be the ticket printer's, so it is left unchanged and named.
        string queue = SampleConfig.DriverName(SampleConfig.P4030);
        Assert.Equal([queue], detection.RejectedQueues);
        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Warning, queue, "USB001", other.InstanceId, SampleConfig.P4030),
            (finding.Severity, finding.Data["queue"], finding.Data["port"], finding.Data["device"], finding.Data["driverModel"]));
    }

    [Fact]
    public void Ta_device_of_an_unsupported_model_is_reported_not_offered()
    {
        var device = new PnpPrinterDevice
        {
            InstanceId = @"USBPRINT\KYOCERAECOSYS_P2040DW7C1A\1",
            Bus = PnpBus.Usb,
            HardwareIds = [@"USBPRINT\KYOCERAECOSYS_P2040DW7C1A"],
            FriendlyName = "Kyocera ECOSYS P2040dw",
            IsPresent = true,
        };
        var scenario = new ScenarioBuilder().WithDevice(device).Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        Assert.True(Has(detection.Findings, FindingCode.UnsupportedPrinterDetected, Severity.Info));
    }

    [Fact]
    public void Queues_on_local_ports_and_server_connections_are_never_candidates()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithQueue("TA to file", SampleConfig.DriverName(SampleConfig.P4030), "FILE:")
            .WithQueue(@"\\printsrv\Tickets", SampleConfig.DriverName(SampleConfig.P4030), "IP_10.0.0.9", serverName: @"\\printsrv")
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        Assert.True(Has(detection.Findings, FindingCode.ServerConnectionNotManaged, Severity.Info));
        // Printing to a file never reaches a printer, so that queue is no possible ticket printer either.
        Assert.Empty(detection.RejectedQueues);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
    }

    [Fact]
    public void Several_queues_of_one_printer_keep_the_preferred_one_and_warn_when_none_is_the_ticket_queue()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithDriver(SampleConfig.XpsDriverName(SampleConfig.P4030))
            .WithUsbQueue(name: "A ticket printer", driverName: SampleConfig.XpsDriverName(SampleConfig.P4030))
            .WithUsbQueue(name: "Z ticket printer")
            .Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal("Z ticket printer", candidate.Queue!.Name);
        Assert.Equal("A ticket printer", Assert.Single(candidate.AdditionalQueues).Name);
        // Neither queue is set up as the ticket printer yet, so the configured one is only chosen by its driver.
        var duplicates = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, "Z ticket printer"), (duplicates.Severity, duplicates.Data["queue"]));
        Assert.Contains("'Z ticket printer' is configured", duplicates.Detail, StringComparison.Ordinal);
        Assert.Contains("the others keep their settings: 'A ticket printer'", duplicates.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Of_several_queues_of_one_usb_printer_the_one_already_using_the_ticket_form_is_configured()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithDriver(SampleConfig.XpsDriverName(SampleConfig.P4030))
            .WithUsbQueue(name: "A ticket printer", driverName: SampleConfig.XpsDriverName(SampleConfig.P4030))
            .WithUsbQueue(name: "Z office printer")
            .WithTicketForm()
            .WithCorrectDefaults("A ticket printer")
            .Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        // Not the preferred driver's queue: the one whose machine-wide default is already the ticket form.
        Assert.Equal("A ticket printer", candidate.Queue!.Name);
        Assert.Equal("Z office printer", Assert.Single(candidate.AdditionalQueues).Name);
        var duplicates = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Info, "A ticket printer"), (duplicates.Severity, duplicates.Data["queue"]));
        Assert.Contains("already set up as the ticket printer", duplicates.Detail, StringComparison.Ordinal);
        Assert.Contains("the others keep their settings: 'Z office printer'", duplicates.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Of_several_queues_on_one_network_host_the_one_already_using_the_ticket_form_is_configured()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(Address, name: "A4 office")
            .WithNetworkQueue(Address, name: "Tickets")
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithNetworkPrinter(Address)
            .Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal((PrinterCandidate.NetworkKey(Address), "Tickets"), (candidate.Key, candidate.Queue!.Name));
        Assert.Equal("A4 office", Assert.Single(candidate.AdditionalQueues).Name);
        Assert.Equal(Severity.Info, Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues).Severity);
    }

    // ---- Network -------------------------------------------------------------------------------------------------

    [Fact]
    public void Existing_tcp_queue_with_an_snmp_identity_is_exact()
    {
        var scenario = new ScenarioBuilder().WithModelDriver().WithNetworkQueue(Address).WithNetworkPrinter(Address).Build();

        var candidate = Assert.Single(Detect(scenario).Candidates);

        Assert.Equal(PrinterCandidate.NetworkKey(Address), candidate.Key);
        Assert.Equal(MatchConfidence.Exact, candidate.Confidence);
        Assert.Equal(Address, candidate.NetworkAddress);
        Assert.True(candidate.PhysicallyAvailable);
        Assert.False(candidate.IdentityConflict);
        Assert.Contains(Address, scenario.Network.ProbedAddresses);
    }

    [Fact]
    public void Device_identity_wins_over_a_queue_driver_of_another_model()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue(Address, SampleConfig.P4030)
            .WithNetworkPrinter(Address, SampleConfig.P5032)
            .Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal(SampleConfig.P5032, candidate.Model!.Id);
        Assert.True(candidate.IdentityConflict);
        Assert.True(Has(detection.Findings, FindingCode.IdentityConflict, Severity.Warning));

        var plan = scenario.CreateSession().Assess(candidate, default);
        var change = Assert.Single(plan.Actions, a => a.Kind == ActionKind.ChangeQueueDriver);
        Assert.Equal(SampleConfig.DriverName(SampleConfig.P5032), change.Parameters["driverName"]);
    }

    [Fact]
    public void Queues_of_one_host_whose_drivers_name_different_models_are_left_unchanged()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithModelDriver(SampleConfig.P5032)
            .WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets")
            .WithNetworkQueue(Address, SampleConfig.P5032, name: "Office")
            .WithNetworkPrinter(Address, modelId: null)
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        // Not IdentityConflict, whose text says the setup followed the printer's report: these queues are not changed.
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.IdentityConflict);
        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Equal((Severity.Warning, Address), (finding.Severity, finding.Data["address"]));
        Assert.Contains("drivers of different models (P-4030DN, P-5032DN)", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(["Office", "Tickets"], detection.RejectedQueues.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Tcp_queue_without_snmp_identity_is_driver_only()
    {
        var scenario = new ScenarioBuilder().WithModelDriver().WithNetworkQueue(Address).WithNetworkPrinter(Address, modelId: null).Build();

        var candidate = Assert.Single(Detect(scenario).Candidates);

        Assert.Equal(MatchConfidence.DriverOnly, candidate.Confidence);
        Assert.True(candidate.IsEligible);
        Assert.True(candidate.PhysicallyAvailable);
    }

    [Fact]
    public void Only_hosts_of_possible_ta_queues_are_probed()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.0.0.1")
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.2", name: "Class driver queue", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithDriver("HP Universal Printing PCL 6")
            .WithNetworkQueue("10.0.0.3", name: "Office HP", driverName: "HP Universal Printing PCL 6")
            .Build();

        Detect(scenario);

        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, scenario.Network.ProbedAddresses);
        var options = scenario.Network.ProbeOptions[0];
        Assert.Equal(new NetworkProbeOptions(9100, true, "public", 2000), options);
    }

    [Fact]
    public void Probing_of_existing_queues_can_be_turned_off()
    {
        var scenario = new ScenarioBuilder().WithModelDriver().WithNetworkQueue("10.0.0.1").Build();

        scenario.CreateSession().Detect(new DetectRequest { ProbeExistingNetworkQueues = false }, default);

        Assert.Empty(scenario.Network.ProbedAddresses);
    }

    [Fact]
    public void Network_probes_are_capped_and_requested_addresses_come_first()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(c => c with { Connection = c.Connection with { MaxNetworkProbes = 2 } })
            .WithModelDriver()
            .WithNetworkQueue("10.0.0.1")
            .WithNetworkQueue("10.0.0.2", name: "Second")
            .Build();

        Detect(scenario, "10.0.0.9");

        Assert.Equal(new[] { "10.0.0.9", "10.0.0.1" }, scenario.Network.ProbedAddresses);
        Assert.True(scenario.Log.Contains("detect.probe.capped"));
    }

    [Fact]
    public void Requested_address_with_an_exact_identity_is_eligible()
    {
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P5032).Build();

        var candidate = Assert.Single(Detect(scenario, Address).Candidates);

        Assert.Equal(MatchConfidence.Exact, candidate.Confidence);
        Assert.Equal(SampleConfig.P5032, candidate.Model!.Id);
        Assert.Null(candidate.Queue);
    }

    [Fact]
    public void Requested_address_named_only_probably_needs_confirmation()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-4030DN" })
            .Build();

        var detection = Detect(scenario, Address);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal(MatchConfidence.Probable, candidate.Confidence);
        Assert.True(candidate.NeedsConfirmation);
        Assert.False(candidate.IsEligible);
        var selection = scenario.CreateSession().SelectAutomatically(detection);
        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        Assert.Null(selection.Selected);
    }

    [Fact]
    public void Reachable_printer_with_silent_snmp_is_offered_without_a_model()
    {
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, modelId: null).Build();

        var detection = Detect(scenario, Address);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Null(candidate.Model);
        Assert.Equal(MatchConfidence.None, candidate.Confidence);
        Assert.True(Has(detection.Findings, FindingCode.NetworkIdentityUnavailable, Severity.Warning));
        Assert.Equal(SelectionReason.NeedsConfirmation, scenario.CreateSession().SelectAutomatically(detection).Reason);
    }

    [Fact]
    public void Unreachable_unidentified_address_is_only_a_finding()
    {
        var scenario = new ScenarioBuilder().Build();

        var detection = Detect(scenario, Address);

        Assert.Empty(detection.Candidates);
        Assert.True(Has(detection.Findings, FindingCode.NetworkPrinterUnreachable, Severity.Warning));
        Assert.True(Has(detection.Findings, FindingCode.NetworkIdentityUnavailable));
    }

    [Fact]
    public void Address_of_another_vendors_printer_is_rejected()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Address,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "HP ETHERNET MULTI-ENVIRONMENT",
                Ieee1284DeviceId = "MFG:HP;MDL:HP LaserJet M404dn;",
            })
            .Build();

        var detection = Detect(scenario, Address);

        Assert.Empty(detection.Candidates);
        Assert.True(Has(detection.Findings, FindingCode.NetworkIdentityMismatch, Severity.Warning));
    }

    [Fact]
    public void Requested_address_whose_mdl_names_an_unsupported_model_is_rejected()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Address,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "KYOCERA Document Solutions Printing System",
                DeviceDescription = "ECOSYS P2040dw",
                Ieee1284DeviceId = "ID:ECOSYS P2040dw;MFG:Kyocera;CMD:PCLXL,PJL;MDL:ECOSYS P2040dw;CLS:PRINTER;",
            })
            .Build();

        var detection = Detect(scenario, Address);

        Assert.Empty(detection.Candidates);
        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Equal("Kyocera ECOSYS P2040dw", finding.Data["reported"]);
    }

    [Fact]
    public void Ta_queue_pointing_at_another_model_is_not_configured()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(Address)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, Ieee1284DeviceId = "MFG:Kyocera;MDL:ECOSYS P2040dw;" })
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        Assert.True(Has(detection.Findings, FindingCode.NetworkIdentityMismatch, Severity.Warning));
    }

    [Fact]
    public void Probable_identity_contradicting_the_queue_driver_needs_confirmation()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue(Address, SampleConfig.P4030)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-5032DN" })
            .Build();

        var candidate = Assert.Single(Detect(scenario).Candidates);

        Assert.Equal(SampleConfig.P5032, candidate.Model!.Id);
        Assert.Equal(MatchConfidence.Probable, candidate.Confidence);
        Assert.True(candidate.NeedsConfirmation);
        Assert.True(candidate.IdentityConflict);
    }

    // ---- WSD -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Wsd_queue_is_driver_only_and_upgraded_by_one_matching_wsd_device()
    {
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithQueue("TA P-4030DN (WSD)", SampleConfig.DriverName(SampleConfig.P4030), "WSD-5d6a1f3c-1111-2222-3333-444455556666");
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = "WSD-5d6a1f3c-1111-2222-3333-444455556666", Kind = PortKind.Wsd, MonitorName = "WSD Port" });
        var scenario = builder.Build();

        var driverOnly = Assert.Single(Detect(scenario).Candidates);
        Assert.Equal(ConnectionType.Wsd, driverOnly.Connection);
        Assert.Equal(MatchConfidence.DriverOnly, driverOnly.Confidence);

        scenario.Devices.Add(new PnpPrinterDevice
        {
            InstanceId = @"SWD\PRINTENUM\{5D6A1F3C}",
            Bus = PnpBus.Wsd,
            HardwareIds = [SampleConfig.WsdHardwareId(SampleConfig.P4030)],
            IsPresent = true,
        });
        var exact = Assert.Single(Detect(scenario).Candidates);
        Assert.Equal(MatchConfidence.Exact, exact.Confidence);
        Assert.True(exact.PhysicallyAvailable);
    }

    // ---- Environment ---------------------------------------------------------------------------------------------

    [Fact]
    public void Stopped_spooler_still_finds_usb_devices_and_reads_no_queues()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().WithSpooler(ServiceRunState.Stopped).Build();

        var detection = Detect(scenario);

        Assert.Single(detection.Candidates);
        Assert.True(Has(detection.Findings, FindingCode.SpoolerNotRunning, Severity.Warning));
        Assert.Contains(detection.Snapshot.CaptureErrors, e => e.Contains("not read", StringComparison.Ordinal));
        Assert.False(scenario.Log.Contains("detect.capture.failed"));
    }

    [Fact]
    public void Rpc_errors_from_a_running_spooler_mean_the_spooler_is_unavailable()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().Build();
        scenario.Spooler.FailOperation(nameof(IPrintSpooler.GetQueues), FakePrintSpooler.ErrorRpcServerUnavailable);

        var detection = Detect(scenario);

        Assert.True(Has(detection.Findings, FindingCode.SpoolerUnavailable, Severity.Error));
        Assert.Contains(detection.Snapshot.CaptureErrors, e => e.StartsWith("queues:", StringComparison.Ordinal));
        Assert.Single(detection.Candidates);
    }

    [Fact]
    public void Platform_problems_are_environment_errors()
    {
        var scenario = new ScenarioBuilder()
            .WithPlatform(p => p with { Architecture = "arm64", Build = 9600, ProtectedPrintModeEnabled = true })
            .Build();

        var findings = Detect(scenario).Findings;

        Assert.True(Has(findings, FindingCode.UnsupportedArchitecture, Severity.Error));
        Assert.True(Has(findings, FindingCode.UnsupportedOperatingSystem, Severity.Error));
        Assert.True(Has(findings, FindingCode.ProtectedPrintModeEnabled, Severity.Error));
    }

    [Theory]
    [InlineData(SetupMode.Setup, false, Severity.Error)]
    [InlineData(SetupMode.Repair, false, Severity.Error)]
    [InlineData(SetupMode.Setup, true, Severity.Warning)]
    [InlineData(SetupMode.Diagnostics, false, Severity.Info)]
    public void Unconfirmed_dimensions_severity_depends_on_mode(SetupMode mode, bool allowed, Severity expected)
    {
        var scenario = new ScenarioBuilder()
            .WithTicket(t => t with { DimensionsConfirmed = false })
            .WithOptions(o => o with { Mode = mode, AllowUnconfirmedDimensions = allowed })
            .Build();

        var findings = scenario.CreateSession().Detect(new DetectRequest(), default).Findings;

        Assert.Equal(expected, Assert.Single(findings, f => f.Code == FindingCode.DimensionsUnconfirmed).Severity);
    }

    [Fact]
    public void Detection_never_writes()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithNetworkPrinter(Address)
            .WithSpooler(ServiceRunState.Stopped)
            .Build();

        Detect(scenario, Address);

        Assert.Empty(scenario.Spooler.Writes);
        Assert.Empty(scenario.Installer.Calls);
        Assert.Empty(scenario.Packages.PreparedPackageIds);
    }

    // ---- Selection -----------------------------------------------------------------------------------------------

    [Fact]
    public void One_eligible_printer_next_to_a_probable_one_is_a_choice()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "UTAX P-5032DN" })
            .Build();
        var detection = Detect(scenario, Address);

        var selection = scenario.CreateSession().SelectAutomatically(detection);

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal(2, selection.Choices.Count);
    }

    [Fact]
    public void Preferred_address_that_matches_nothing_chooses_no_other_printer()
    {
        // The printer IT named may only be switched off: the USB printer is not that printer.
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithBranch(new BranchOverride { PrinterAddress = "10.9.9.9" }).Build();

        var selection = scenario.CreateSession().SelectAutomatically(Detect(scenario));

        Assert.Equal(SelectionReason.NoCandidates, selection.Reason);
        Assert.Null(selection.Selected);
        Assert.Equal("10.9.9.9", selection.Address);
    }

    // ---- Resolve -------------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_finds_the_same_printer_again()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(Detect(scenario)).Selected!;

        var resolved = session.Resolve(Detect(scenario), TargetSelector.For(target));

        Assert.Equal(target.Key, resolved!.Key);
    }

    [Fact]
    public void Resolve_rejects_a_printer_that_now_reports_another_model()
    {
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4030).Build();
        var session = scenario.CreateSession();
        var selector = TargetSelector.For(Assert.Single(Detect(scenario, Address).Candidates));

        scenario.Network.SetIdentity(SampleConfig.NetworkIdentity(Address, SampleConfig.P5032));

        Assert.Null(session.Resolve(Detect(scenario, Address), selector));
    }

    [Fact]
    public void Resolve_returns_null_when_the_printer_is_gone()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().Build();
        var session = scenario.CreateSession();

        Assert.Null(session.Resolve(Detect(scenario), new TargetSelector("usb:missing", SampleConfig.P4030)));
    }

    [Fact]
    public void Probable_printer_is_used_only_when_the_operator_asserts_its_model()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-4030DN" })
            .Build();
        var session = scenario.CreateSession();
        var detection = Detect(scenario, Address);
        var probable = Assert.Single(detection.Candidates);

        Assert.Null(session.Resolve(detection, TargetSelector.For(probable)));
        Assert.Null(session.Resolve(detection, new TargetSelector(probable.Key, SampleConfig.P5032, ModelAssertedByUser: true)));

        var asserted = session.Resolve(detection, TargetSelector.For(probable, modelAssertedByUser: true))!;
        Assert.Equal(MatchConfidence.UserAsserted, asserted.Confidence);
        Assert.True(asserted.IsEligible);
        Assert.Equal("Model asserted by operator", asserted.Evidence[^1]);
    }

    [Fact]
    public void Unidentified_printer_takes_the_operator_model_unless_the_device_names_another()
    {
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, modelId: null).Build();
        var session = scenario.CreateSession();
        var detection = Detect(scenario, Address);
        var key = Assert.Single(detection.Candidates).Key;

        var asserted = session.Resolve(detection, new TargetSelector(key, SampleConfig.P4531, ModelAssertedByUser: true))!;
        Assert.Equal(SampleConfig.P4531, asserted.Model!.Id);
        Assert.Equal(MatchConfidence.UserAsserted, asserted.Confidence);
        Assert.Null(session.Resolve(detection, new TargetSelector(key, "P-9999DN", ModelAssertedByUser: true)));

        scenario.Network.SetIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "KYOCERA P-5032DN printer" });
        var contradicted = Detect(scenario, Address);
        Assert.Equal(MatchConfidence.Probable, Assert.Single(contradicted.Candidates).Confidence);
        Assert.Null(session.Resolve(contradicted, new TargetSelector(key, SampleConfig.P4531, ModelAssertedByUser: true)));
    }
}
