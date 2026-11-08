using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class VerificationTests
{
    private const string HostName = "printer12.branch.local";
    private const string Ip = "10.12.0.50";
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    private static VerificationReport Verify(Scenario scenario, TargetSelector? selector = null)
    {
        var session = scenario.CreateSession();
        selector ??= TargetSelector.For(session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!);
        return session.Verify(selector, default);
    }

    [Fact]
    public void Correct_printer_is_ready_with_every_check_and_a_summary()
    {
        var report = Verify(new ScenarioBuilder().WithInstalledUsbPrinter().Build());

        Assert.Equal(ReadinessLevel.Ready, report.Readiness);
        Assert.Equal(Enum.GetValues<VerificationItem>().Order(), report.Checks.Select(c => c.Item).Order());
        Assert.Equal("TA Triumph-Adler P-4030DN", report.Summary.ModelName);
        Assert.Equal(Queue4030, report.Summary.QueueName);
        Assert.Equal(Queue4030, report.Summary.DriverName);
        Assert.Equal("AMWAG Ticket, 82.296 mm x 170.18 mm (3.24 in x 6.70 in)", report.Summary.PaperSize);
        Assert.Equal("82296 x 170180 µm, full imageable area", Check(report, VerificationItem.FormSizeExact).Expected);
    }

    [Fact]
    public void Missing_target_is_not_ready_and_explains_why()
    {
        var scenario = new ScenarioBuilder().Build();

        var report = Verify(scenario, new TargetSelector(PrinterCandidate.UsbKey("USBPRINT\\GONE\\1"), SampleConfig.P4030));

        Assert.Equal(ReadinessLevel.NotReady, report.Readiness);
        var check = Check(report, VerificationItem.QueueExists);
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("was not found again", check.Detail, StringComparison.Ordinal);
        Assert.True(Has(report.Findings, FindingCode.NoSupportedPrinterFound, Severity.Error));
        Assert.Null(report.Target);
    }

    [Fact]
    public void Wrong_defaults_fail_with_expected_and_actual_values()
    {
        var report = Verify(new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build());

        Assert.Equal(ReadinessLevel.NotReady, report.Readiness);
        var paper = Check(report, VerificationItem.DefaultPaper);
        Assert.Equal(CheckStatus.Fail, paper.Status);
        Assert.Equal("'AMWAG Ticket' (paper 257)", paper.Expected);
        Assert.Equal("'A4' (paper 9)", paper.Actual);
        Assert.Equal(CheckStatus.Fail, Check(report, VerificationItem.DefaultDuplex).Status);
        Assert.Equal(CheckStatus.Pass, Check(report, VerificationItem.DefaultOrientation).Status);
        Assert.True(Has(report.Findings, FindingCode.GlobalDefaultsIncorrect, Severity.Error));
    }

    [Fact]
    public void Unavailable_print_ticket_is_unknown_never_a_failure()
    {
        var report = Verify(new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { PrintTicketAvailable = false })
            .Build());

        Assert.Equal(CheckStatus.Unknown, Check(report, VerificationItem.PrintTicketMediaSize).Status);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, report.Readiness);
    }

    [Fact]
    public void Print_ticket_size_mismatch_is_a_warning()
    {
        var report = Verify(new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with
            {
                PrintTicketOverride = new PrintTicketMediaInfo { Available = true, MediaSizeName = "psk:ISOA4", WidthMicrometers = 210_000, HeightMicrometers = 297_000 },
            })
            .Build());

        Assert.Equal(CheckStatus.Warning, Check(report, VerificationItem.PrintTicketMediaSize).Status);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, report.Readiness);
        Assert.True(Has(report.Findings, FindingCode.PrintTicketMismatch, Severity.Warning));
    }

    [Fact]
    public void Print_ticket_within_one_millimeter_passes()
    {
        var report = Verify(new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with
            {
                PrintTicketOverride = new PrintTicketMediaInfo { Available = true, WidthMicrometers = 82_300, HeightMicrometers = 170_200 },
            })
            .Build());

        Assert.Equal(CheckStatus.Pass, Check(report, VerificationItem.PrintTicketMediaSize).Status);
    }

    [Fact]
    public void Driver_default_paper_source_policy_is_not_applicable()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(c => c with { PrintPreferences = c.PrintPreferences with { PaperSource = PaperSourcePolicy.DriverDefault } })
            .WithInstalledUsbPrinter()
            .Build();

        var report = Verify(scenario);

        Assert.Equal(CheckStatus.NotApplicable, Check(report, VerificationItem.DefaultPaperSource).Status);
        Assert.Null(report.Summary.PaperSource);
        Assert.Equal(ReadinessLevel.Ready, report.Readiness);
    }

    [Fact]
    public void Disconnected_usb_printer_is_ready_with_warnings()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(present: false)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();

        var report = Verify(scenario);

        Assert.Equal(CheckStatus.Warning, Check(report, VerificationItem.DeviceConnected).Status);
        Assert.Equal(CheckStatus.Unknown, Check(report, VerificationItem.QueuePortCorrect).Status);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, report.Readiness);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disconnected_usb_printer_is_never_reported_as_not_linked_to_its_driver(bool driverBound)
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(present: false, driverBound: driverBound, driverDescription: driverBound ? SampleConfig.MicrosoftPcl6ClassDriver : null)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();

        var report = Verify(scenario);

        var device = Check(report, VerificationItem.DeviceConnected);
        Assert.Equal((CheckStatus.Warning, "not connected"), (device.Status, device.Actual));
        Assert.True(Has(report.Findings, FindingCode.DeviceNotConnected, Severity.Warning));
        Assert.False(Has(report.Findings, FindingCode.DeviceDriverNotBound));
    }

    [Theory]
    [InlineData("TA P-4030DN KX")]
    [InlineData("TA P-4030DN KX (XPS)")]
    public void Connected_usb_printer_on_any_accepted_driver_passes_the_device_check(string driver)
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: driver)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();

        var report = Verify(scenario);

        Assert.Equal((CheckStatus.Pass, "connected"), (Check(report, VerificationItem.DeviceConnected).Status, Check(report, VerificationItem.DeviceConnected).Actual));
        Assert.False(Has(report.Findings, FindingCode.DeviceDriverNotBound));
        Assert.Equal(ReadinessLevel.Ready, report.Readiness);
    }

    [Theory]
    [InlineData(true, "connected, driver 'Microsoft PCL6 Class Driver'")]
    [InlineData(false, "connected, problem code 28")]
    public void Connected_usb_printer_that_windows_has_not_linked_to_an_accepted_driver_is_a_warning(bool classDriver, string actual)
    {
        // The queue prints with the approved driver; the device node itself is on a class driver, or has no driver at all.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: classDriver, driverDescription: classDriver ? SampleConfig.MicrosoftPcl6ClassDriver : null)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();

        var report = Verify(scenario);

        var device = Check(report, VerificationItem.DeviceConnected);
        Assert.Equal((CheckStatus.Warning, actual), (device.Status, device.Actual));
        Assert.Equal("Connected, with an accepted driver of P-4030DN", device.Expected);
        var finding = Assert.Single(report.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(CheckStatus.Pass, Check(report, VerificationItem.QueueUsesAcceptedDriver).Status);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, report.Readiness);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Connected_usb_printer_waiting_for_a_restart_is_linked_only_with_an_accepted_driver(bool acceptedDriver)
    {
        // Problem code 14 (CM_PROB_NEED_RESTART) after a binding that asked for a restart: the accepted driver takes effect
        // once Windows restarts, so the device is linked and only the restart is pending; replugging would not change that.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: acceptedDriver ? Queue4030 : SampleConfig.MicrosoftPcl6ClassDriver)
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();
        scenario.Devices.Update(SampleConfig.UsbInstanceId(SampleConfig.P4030), d => d with { ProblemCode = DriverRules.NeedRestartProblemCode });

        var report = Verify(scenario);

        var device = Check(report, VerificationItem.DeviceConnected);
        Assert.Equal(CheckStatus.Warning, device.Status);
        Assert.Equal(!acceptedDriver, Has(report.Findings, FindingCode.DeviceDriverNotBound));
        Assert.Equal(acceptedDriver, Has(report.Findings, FindingCode.RebootRequired, Severity.Warning));
        Assert.Equal(acceptedDriver ? "connected, driver 'TA P-4030DN KX', problem code 14 (restart pending)" : "connected, problem code 14", device.Actual);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, report.Readiness);
    }

    // ---- The printer the worker configured is the one checked -----------------------------------------------------------

    /// <summary>
    /// What the elevated worker does (WorkerHost.Apply): detect with the selector's address, --address and the branch
    /// address plus the existing queues' hosts, resolve the selector, assess and apply.
    /// </summary>
    private static PrinterCandidate WorkerApply(Scenario scenario, SetupSession session, TargetSelector selector)
    {
        string[] addresses = new[] { selector.NetworkAddress, scenario.Options.PreferredAddress, scenario.Branch?.PrinterAddress }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var target = session.Resolve(session.Detect(new DetectRequest { NetworkAddresses = addresses }, default), selector)
            ?? throw new InvalidOperationException("The worker did not find the printer again.");
        var apply = session.Apply(session.Assess(target, default), ApplyScope.All, null, default);
        Assert.True(apply.Succeeded, string.Join("; ", apply.Results.Select(r => $"{r.Action.Kind}: {r.Outcome} {r.Detail}")));
        return target;
    }

    [Fact]
    public void Final_check_finds_the_queue_the_worker_configured_under_another_spelling_of_the_printer()
    {
        // The window's detection could not resolve the queue's host name, so it offered only the requested address (SNMP off)
        // and the operator named the model. When the worker detects again the name resolves: the selector is found by alias.
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(HostName, name: "Ticket printer", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();
        var session = scenario.CreateSession();
        var chosen = Assert.Single(session.Detect(new DetectRequest(), default).Candidates);
        var selector = new TargetSelector(chosen.Key, SampleConfig.P4030, ModelAssertedByUser: true);

        scenario.Network.SetAlias(HostName, Ip);
        var configured = WorkerApply(scenario, session, selector);

        Assert.Equal((PrinterCandidate.NetworkKey(HostName), "Ticket printer"), (configured.Key, configured.Queue?.Name));
        var report = session.Verify(selector, default);

        Assert.Equal(configured.Key, report.Target!.Key);
        Assert.Equal(("Ticket printer", ReadinessLevel.Ready), (report.Summary.QueueName, report.Readiness));
    }

    [Fact]
    public void Final_check_probes_the_queue_hosts_that_the_windows_probe_limit_left_out_as_the_worker_does()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(c => c with { Connection = c.Connection with { MaxNetworkProbes = 3 } })
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.77", name: "Other", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(HostName, name: "Ticket printer", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            .WithNetworkAlias(HostName, Ip)
            .WithTicketForm()
            .Build();
        var session = scenario.CreateSession();
        // The operator entered 10.0.0.99, then 10.12.0.50: the window's probes stop before the ticket queue's host.
        var window = session.Detect(new DetectRequest { NetworkAddresses = ["10.0.0.99", Ip] }, default);
        var chosen = Assert.Single(window.Candidates, c => c.IsAtAddress(Ip));
        Assert.Null(chosen.Queue);
        var selector = new TargetSelector(chosen.Key, SampleConfig.P4030, ModelAssertedByUser: true);

        var configured = WorkerApply(scenario, session, selector);

        Assert.Equal("Ticket printer", configured.Queue?.Name);
        var report = session.Verify(selector, default);
        Assert.Equal(("Ticket printer", ReadinessLevel.Ready), (report.Summary.QueueName, report.Readiness));
    }

    [Fact]
    public void Final_check_of_a_usb_printer_probes_no_network_address()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithModelDriver(SampleConfig.P5032).WithNetworkQueue("10.0.0.5", SampleConfig.P5032).Build();
        var session = scenario.CreateSession();
        var selector = new TargetSelector(PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030)), SampleConfig.P4030);

        Assert.Equal(ReadinessLevel.Ready, session.Verify(selector, default).Readiness);
        Assert.Empty(scenario.Network.ProbedAddresses);
    }

    [Fact]
    public void Network_printer_summary_names_address_and_port()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.0.0.5")
            .WithNetworkPrinter("10.0.0.5")
            .WithTicketForm()
            .WithCorrectDefaults("AMWAG Ticket Printer")
            .Build();

        var report = Verify(scenario, new TargetSelector(PrinterCandidate.NetworkKey("10.0.0.5"), SampleConfig.P4030));

        Assert.Equal(ReadinessLevel.Ready, report.Readiness);
        Assert.Equal("Network 10.0.0.5, port 9100", report.Summary.Connection);
        Assert.Equal(CheckStatus.Pass, Check(report, VerificationItem.QueuePortCorrect).Status);
    }

    [Fact]
    public void Queue_with_a_wrong_driver_fails_the_driver_check()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithUsbQueue(name: "Ticket", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .Build();

        var report = Verify(scenario);

        Assert.Equal(CheckStatus.Fail, Check(report, VerificationItem.QueueUsesAcceptedDriver).Status);
        Assert.Equal(CheckStatus.Fail, Check(report, VerificationItem.DriverInstalled).Status);
        Assert.True(Has(report.Findings, FindingCode.QueueDriverMismatch, Severity.Error));
    }

    [Fact]
    public void Verification_never_writes()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).WithPerUserDefaults(Queue4030, d => d.Scale = 70).Build();

        var report = Verify(scenario);

        Assert.Equal(CheckStatus.Fail, Check(report, VerificationItem.UserDefaultsConsistent).Status);
        Assert.Empty(scenario.Spooler.Writes);
    }
}
