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
/// A queue already set up as the ticket printer that the setup does not configure (a class-driver queue on an IPP or LPR port,
/// pooled, or one whose printer has no queue the setup configures) keeps every other printer from being taken as the ticket
/// printer without asking, by modelId too unless its printer reports another model. A run's warning about its own change (a
/// queue added next to one that may print to the same printer, a queue chosen among the printer's queues) is kept and makes the
/// run ready with warnings. Diagnostics and Repair name every printer that may be the ticket printer.
/// </summary>
public sealed class TicketQueueElsewhereTests
{
    private const string Ipp = "Microsoft IPP Class Driver";
    private const string IppUrl = "http://10.0.0.5:631/ipp/print";
    private const string WsdPort = "WSD-5d6a1f3c-1111-2222-3333-444455556666";

    private readonly UpdateCollector _updates = new();

    private static SilentInteraction Silent(params string[] args) => new(CommandLineParser.Parse(["--silent", .. args]));

    private Task<FlowResult> Run(FlowEnvironment environment, ISetupInteraction interaction, InProcessBroker broker) =>
        new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

    private async Task<(FlowResult Result, InProcessBroker Broker)> RunSilent(Scenario scenario, string mode = "setup")
    {
        var environment = scenario.Environment(mode switch { "repair" => SetupMode.Repair, "diagnostics" => SetupMode.Diagnostics, _ => SetupMode.Setup });
        var broker = new InProcessBroker(environment);
        return (await Run(environment, Silent("--mode", mode), broker), broker);
    }

    private static NetworkIdentity Probable(string address, string model) =>
        new() { Address = address, RawPortReachable = true, SnmpResponded = true, SysDescr = $"TA Triumph-Adler {model} Series Network Printer" };

    // ---- An office unit next to the ticket printer whose ticket queue the setup does not configure ----------------------------

    /// <summary>
    /// An office USB printer of <paramref name="officeModel"/> with its Plug and Play queue, and the ticket printer P-4030DN at
    /// 10.0.0.5, reached only through class-driver queues: 'A4' on IP_10.0.0.5 (unless <paramref name="withA4"/> is false) and the
    /// ticket queue of <paramref name="kind"/>: on its IPP port on the ticket form ("ipp-form"), there with the configured queue
    /// name ("ipp-name"), on an LPR port ("lpr-form"), pooled over IP_10.0.0.5 and IP_10.0.0.6 ("pool-form"), on a WSD port
    /// ("wsd-form"), or on its IPP port on A4 ("ipp-plain"). The printer reports <paramref name="identity"/>.
    /// </summary>
    private static Scenario OfficeUnitBesideTicketQueue(string? modelId, string kind = "ipp-form", string identity = "exact", bool withA4 = true,
        string officeModel = SampleConfig.P4030, bool officeOnTicketForm = false)
    {
        ScenarioBuilder builder = new ScenarioBuilder()
            .WithUsbPrinter(officeModel)
            .WithModelDriver(officeModel)
            .WithUsbQueue(officeModel)
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(Ipp)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithTicketForm();
        if (officeOnTicketForm)
            builder.WithCorrectDefaults(SampleConfig.DriverName(officeModel));
        if (withA4)
            builder.WithNetworkQueue("10.0.0.5", name: "A4", driverName: SampleConfig.MicrosoftPcl6ClassDriver);
        switch (kind)
        {
            case "ipp-form":
                builder.WithInternetPort(IppUrl).WithQueue("Tickets", Ipp, IppUrl).WithCorrectDefaults("Tickets");
                break;
            case "ipp-plain":
                builder.WithInternetPort(IppUrl).WithQueue("Tickets", Ipp, IppUrl);
                break;
            case "ipp-name":
                builder.WithInternetPort(IppUrl).WithQueue(builder.Config.Connection.NewQueueName, Ipp, IppUrl);
                break;
            case "lpr-form":
                builder.WithLprPort("10.0.0.5:PASSTHRU").WithQueue("Tickets", SampleConfig.MicrosoftPcl6ClassDriver, "10.0.0.5:PASSTHRU").WithCorrectDefaults("Tickets");
                break;
            case "pool-form":
                builder.WithTcpIpPort("10.0.0.6").WithQueue("Tickets", SampleConfig.MicrosoftPcl6ClassDriver, "IP_10.0.0.5,IP_10.0.0.6").WithCorrectDefaults("Tickets")
                    .WithNetworkPrinter("10.0.0.6", SampleConfig.P4030);
                break;
            case "wsd-form":
                builder.WithQueue("Tickets", Ipp, WsdPort).WithCorrectDefaults("Tickets");
                builder.Spooler.SeedPort(new PrinterPortInfo { Name = WsdPort, Kind = PortKind.Wsd, MonitorName = "WSD Port" });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        builder = identity switch
        {
            "exact" => builder.WithNetworkPrinter("10.0.0.5", SampleConfig.P4030),
            "nosnmp" => builder.WithNetworkPrinter("10.0.0.5", modelId: null, snmp: false),
            "probable" => builder.WithNetworkIdentity(Probable("10.0.0.5", SampleConfig.P4030)),
            _ => throw new ArgumentOutOfRangeException(nameof(identity)),
        };
        return builder.WithBranch(modelId is null ? null : new BranchOverride { ModelId = modelId }).Build();
    }

    [Theory]
    [InlineData("setup", "ipp-form", "exact")]
    [InlineData("repair", "ipp-form", "exact")]
    [InlineData("setup", "ipp-name", "exact")]
    [InlineData("setup", "lpr-form", "exact")]
    [InlineData("setup", "pool-form", "exact")]
    [InlineData("setup", "ipp-form", "nosnmp")] // a printer that does not tell its model may be of the configured one
    [InlineData("setup", "ipp-form", "probable")]
    public async Task ModelId_does_not_pick_an_office_unit_of_its_model_beside_a_ticket_queue_that_names_no_model(string mode, string kind, string identity)
    {
        // Before: modelId P-4030DN set up the office unit (exit 0), and the tool's own exit-4 advice without modelId named it.
        Scenario scenario = OfficeUnitBesideTicketQueue(SampleConfig.P4030, kind, identity);

        var (result, broker) = await RunSilent(scenario, mode);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Finding stop = Assert.Single(result.Findings, f => f.Code == FindingCode.Cancelled);
        Assert.DoesNotContain("modelId P-4030DN in branch.json", stop.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_modelId_the_advice_does_not_offer_a_model_that_would_pick_the_office_unit()
    {
        Scenario scenario = OfficeUnitBesideTicketQueue(modelId: null);

        var (result, _) = await RunSilent(scenario);

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Finding stop = Assert.Single(result.Findings, f => f.Code == FindingCode.Cancelled);
        Assert.Contains("No switch tells them apart, because the printer of those queues may be of the same model", stop.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("--model", stop.Detail, StringComparison.Ordinal);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task The_configured_model_still_picks_its_only_printer_when_the_ticket_queue_s_printer_reports_another_model()
    {
        // IT names the office unit's model P-4531DN; the printer behind the class-driver ticket queue reports P-4030DN.
        Scenario scenario = OfficeUnitBesideTicketQueue(SampleConfig.P4531, officeModel: SampleConfig.P4531);

        var (result, broker) = await RunSilent(scenario);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        Assert.StartsWith("usb:", Assert.Single(broker.Requests).Target.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_does_not_take_the_office_queue_on_the_ticket_paper_while_a_queue_it_cannot_configure_is_on_it_too()
    {
        // Both the office unit's queue and the class-driver 'Tickets' use the ticket form: the ticket form tells neither apart.
        Scenario scenario = OfficeUnitBesideTicketQueue(modelId: null, officeOnTicketForm: true);

        var (result, broker) = await RunSilent(scenario, "repair");

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Theory]
    [InlineData("ipp-form")]
    [InlineData("lpr-form")]
    [InlineData("wsd-form")]
    public async Task A_ticket_queue_whose_printer_has_no_queue_the_setup_configures_keeps_the_office_unit_from_being_chosen(string kind)
    {
        // Before: the office unit was the single candidate and got the ticket defaults (exit 0); the ticket queue was not seen.
        Scenario scenario = OfficeUnitBesideTicketQueue(modelId: null, kind, withA4: false);

        var (result, broker) = await RunSilent(scenario);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Finding left = Assert.Single(result.Findings, f => f.Code == FindingCode.QueueLeftUnchanged && f.Data.GetValueOrDefault("queue") == "Tickets");
        Assert.Contains("is already set up as the ticket printer", left.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_queue_of_another_vendor_s_printer_does_not_keep_the_office_unit_from_being_chosen()
    {
        // The class-driver queue on the ticket form prints to an HP printer, which reports itself: it is not the TA ticket printer.
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.20", name: "Front desk", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithCorrectDefaults("Front desk")
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = "10.0.0.20",
                ResolvedAddress = "10.0.0.20",
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "HP ETHERNET MULTI-ENVIRONMENT",
                Ieee1284DeviceId = "MFG:HP;MDL:HP LaserJet M404dn;CMD:PJL,PCLXL;CLS:PRINTER;",
            })
            .Build();

        var (result, broker) = await RunSilent(scenario);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        Assert.StartsWith("usb:", Assert.Single(broker.Requests).Target.Key, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
    }

    [Fact]
    public async Task A_class_driver_ipp_queue_on_the_ticket_paper_beside_the_printer_s_own_ticket_queue_is_not_taken_for_another_printer_s()
    {
        // Both the printer's own queue and Windows' IPP queue to the same printer use the ticket form: the printer is the ticket
        // printer either way, and its own rule names the IPP queue (check which queue employees print to); nothing is rejected.
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(Ipp)
            .WithTicketForm()
            .WithNetworkQueue("10.0.0.5", SampleConfig.P4030, name: "Tickets")
            .WithCorrectDefaults("Tickets")
            .WithInternetPort(IppUrl)
            .WithQueue("Tickets (IPP)", Ipp, IppUrl)
            .WithCorrectDefaults("Tickets (IPP)")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .Build();

        var (result, _) = await RunSilent(scenario);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        Assert.Equal(PrinterCandidate.NetworkKey("10.0.0.5"), result.Target?.Key);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.QueueLeftUnchanged);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.DuplicateQueues && f.Detail.Contains("Tickets (IPP)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Control_a_class_driver_queue_on_A4_keeps_the_office_unit_the_only_choice()
    {
        Scenario scenario = OfficeUnitBesideTicketQueue(modelId: null, "ipp-plain", withA4: false);

        var (result, broker) = await RunSilent(scenario);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        Assert.StartsWith("usb:", Assert.Single(broker.Requests).Target.Key, StringComparison.Ordinal);
    }

    // ---- The configured printer whose only queue is a class-driver queue on its IPP port ------------------------------------

    /// <summary>
    /// The P-4030DN at 10.0.0.5 (branch printerAddress) with one queue, 'Tickets', which Windows made on its IPP port with the
    /// IPP class driver: on the ticket form ("form"), named by branch queueName ("name"), or on A4 ("plain").
    /// </summary>
    internal static Scenario IppQueueOnly(string variant)
    {
        ScenarioBuilder builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(Ipp)
            .WithTicketForm()
            .WithInternetPort(IppUrl)
            .WithQueue("Tickets", Ipp, IppUrl)
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030);
        if (variant == "form")
            builder.WithCorrectDefaults("Tickets");
        return builder.WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5", QueueName = variant == "name" ? "Tickets" : null }).Build();
    }

    [Theory]
    [InlineData("form")]
    [InlineData("name")]
    public async Task A_configured_printer_whose_only_queue_is_its_class_driver_ticket_queue_gets_no_second_queue(string variant)
    {
        // Before: Setup added IP_10.0.0.5 and a queue next to 'Tickets' (exit 0); later runs rejected the printer (exit 2 or 3).
        Scenario scenario = IppQueueOnly(variant);

        var (result, broker) = await RunSilent(scenario);

        Assert.Equal((FlowOutcome.NoPrinter, ExitCodes.NoSupportedPrinter), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Finding none = Assert.Single(result.Findings, f => f is { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error });
        Assert.Equal(("10.0.0.5", "Tickets"), (none.Data["address"], none.Data["leftUnchanged"]));
    }

    [Fact]
    public async Task Setup_next_to_windows_own_class_driver_queue_for_the_printer_ends_ready_with_warnings_once()
    {
        // Before: Ready (exit 0) and the window showed nothing, although two queues now print to the ticket printer.
        Scenario scenario = IppQueueOnly("plain");

        var (result, broker) = await RunSilent(scenario);

        Assert.Equal((FlowOutcome.ReadyWithWarnings, ExitCodes.ReadyWithWarnings), (result.Outcome, result.ExitCode));
        Assert.Single(broker.Requests);
        Finding kept = Assert.Single(result.CurrentFindings, f => f is { Code: FindingCode.DuplicateQueues, Severity: Severity.Warning });
        Assert.Equal(("queueAdded", "Tickets"), (kept.Data["reason"], kept.Data["queue"]));

        // The next run has nothing to add: it cannot tell any more, and it is ready.
        var (again, _) = await RunSilent(scenario);
        Assert.Equal((FlowOutcome.Ready, ExitCodes.Ready), (again.Outcome, again.ExitCode));
    }

    [Fact]
    public async Task Setup_next_to_a_wsd_class_driver_queue_says_that_it_may_print_to_the_same_printer()
    {
        ScenarioBuilder builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(Ipp)
            .WithQueue("TA P-4030DN", Ipp, WsdPort)
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" });
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = WsdPort, Kind = PortKind.Wsd, MonitorName = "WSD Port" });
        Scenario scenario = builder.Build();

        var (result, _) = await RunSilent(scenario);

        Assert.Equal((FlowOutcome.ReadyWithWarnings, ExitCodes.ReadyWithWarnings), (result.Outcome, result.ExitCode));
        Assert.Contains(result.CurrentFindings, f => f.Code == FindingCode.DuplicateQueues && f.Data.GetValueOrDefault("reason") == "queueAdded"
            && f.Data.GetValueOrDefault("queue") == "TA P-4030DN");
    }

    [Fact]
    public async Task A_class_driver_ipp_queue_whose_host_name_does_not_resolve_is_still_a_possible_duplicate()
    {
        // Windows' IPP queue names the printer by a host name that does not resolve: it cannot be compared with the address.
        const string url = "http://KM5A1B2C.local:631/ipp/print";
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(Ipp)
            .WithInternetPort(url)
            .WithQueue("Tickets", Ipp, url)
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" })
            .Build();

        var (result, _) = await RunSilent(scenario);

        // Before: Ready (exit 0) with no warning, next to a queue that may print to the same printer.
        Assert.Equal((FlowOutcome.ReadyWithWarnings, ExitCodes.ReadyWithWarnings), (result.Outcome, result.ExitCode));
        Assert.Contains(result.CurrentFindings, f => f.Code == FindingCode.DuplicateQueues && f.Data.GetValueOrDefault("reason") == "queueAdded"
            && f.Data.GetValueOrDefault("queue") == "Tickets");
    }

    // ---- A Standard TCP/IP ticket queue whose printer does not report its model -------------------------------------------

    /// <summary>
    /// 'Tickets' (named by branch queueName) with the PCL6 class driver on IP_10.0.0.5, whose P-4030DN answers with SNMP off,
    /// next to an office USB P-4030DN; no printerAddress.
    /// </summary>
    private static Scenario TcpTicketQueueWithoutModel() => new ScenarioBuilder()
        .WithInstalledUsbPrinter(correctDefaults: false)
        .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
        .WithTicketForm()
        .WithNetworkQueue("10.0.0.5", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
        .WithNetworkPrinter("10.0.0.5", modelId: null, snmp: false)
        .WithBranch(new BranchOverride { QueueName = "Tickets" })
        .Build();

    [Fact]
    public async Task A_tcp_ticket_queue_whose_printer_does_not_report_its_model_is_named_by_its_host_not_moved_or_deleted()
    {
        Scenario scenario = TcpTicketQueueWithoutModel();

        var (result, broker) = await RunSilent(scenario);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        Assert.Empty(broker.Requests);
        Finding left = Assert.Single(result.Findings, f => f.Code == FindingCode.QueueLeftUnchanged && f.Data.GetValueOrDefault("queue") == "Tickets");
        Assert.Contains("prints to 10.0.0.5, whose printer does not report its model or does not answer", left.Detail, StringComparison.Ordinal);
        Assert.Contains("name that host with printerAddress 10.0.0.5 (silent: --address 10.0.0.5 with --model and --confirm-model;", left.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("put the queue on a Standard TCP/IP port", left.Detail, StringComparison.Ordinal);
        // Before: "No switch tells them apart ... (delete a queue whose printer is gone)".
        Assert.Contains("If 'Tickets' on 10.0.0.5 is the ticket queue, name its host with printerAddress in branch.json or --address, and confirm its "
            + "model with --model and --confirm-model; otherwise choose the ticket printer in Setup.",
            Assert.Single(result.Findings, f => f.Code == FindingCode.Cancelled).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Following_that_advice_configures_the_ticket_queue_itself()
    {
        Scenario scenario = TcpTicketQueueWithoutModel();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = "10.0.0.5", PreferredModelId = SampleConfig.P4030 });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, Silent("--address", "10.0.0.5", "--model", SampleConfig.P4030, "--confirm-model"), broker);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        Assert.Equal(PrinterCandidate.NetworkKey("10.0.0.5"), Assert.Single(broker.Requests).Target.Key);
        Assert.Contains(scenario.Spooler.Writes, w => w.Target == "Tickets");
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == "AddQueue");
    }

    // ---- A queue chosen among the printer's queues ------------------------------------------------------------------------

    [Theory]
    [InlineData("setup")]
    [InlineData("repair")]
    public async Task A_queue_chosen_among_the_printer_s_queues_keeps_its_warning_and_the_run_is_ready_with_warnings(string mode)
    {
        // Two queues with the approved driver on the printer's one port, neither on the ticket form nor with the configured name:
        // the run picks one by its driver and name. Before: the result said that queue "is already set up as the ticket printer".
        string driver = SampleConfig.DriverName(SampleConfig.P4030);
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithTicketForm()
            .WithNetworkQueue("10.0.0.5", SampleConfig.P4030, name: "Tickets")
            .WithQueue("A4 office", driver, "IP_10.0.0.5")
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" })
            .Build();

        var (result, broker) = await RunSilent(scenario, mode);

        Assert.Equal((FlowOutcome.ReadyWithWarnings, ExitCodes.ReadyWithWarnings), (result.Outcome, result.ExitCode));
        Assert.Single(broker.Requests);
        Finding guessed = Assert.Single(result.CurrentFindings, f => f is { Code: FindingCode.DuplicateQueues, Severity: Severity.Warning });
        Assert.Contains("none of them can be told apart", guessed.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.DuplicateQueues && f.Detail.Contains("already set up as the ticket printer", StringComparison.Ordinal));
    }

    // ---- Several printers that may be the ticket printer ------------------------------------------------------------------

    [Fact]
    public async Task Diagnostics_of_several_printers_whose_model_must_be_confirmed_names_each_of_them()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.40", name: "A4 office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.40", SampleConfig.P4030))
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
            .Build();

        var (result, _) = await RunSilent(scenario, "diagnostics");

        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Finding several = Assert.Single(result.Findings, f => f.Code == FindingCode.MultipleSupportedPrinters);
        Assert.StartsWith("2 supported printers were found and none is known to be the ticket printer (the printer at ", several.Detail, StringComparison.Ordinal);
        Assert.Equal([PrinterCandidate.NetworkKey("10.0.0.40"), PrinterCandidate.NetworkKey("10.0.0.50")],
            several.Data["choices"].Split(", ").Order(StringComparer.Ordinal));
        ProgressUpdate detect = _updates.OfKind(FlowEventKind.Step).Select(u => u.Progress!).Last(p => p.Step == StepId.Detect);
        Assert.Equal(("Found 2 supported printers", FindingCode.MultipleSupportedPrinters), (detect.Detail, detect.Code));
        Assert.Equal(2, result.Findings.Count(f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error }));
    }

    [Fact]
    public async Task Silent_repair_beside_a_unit_offered_again_names_the_printer_whose_model_must_be_confirmed()
    {
        // The earlier unit's queue alone is on the ticket paper; the new unit is connected; 'Front desk' is probably a P-4030DN.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030, "USB001", name: "Old tickets")
            .WithTicketForm()
            .WithCorrectDefaults("Old tickets")
            .WithUsbPrinter(SampleConfig.P4030, "USB002")
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: "TA P-4030DN KX (Copy 1)")
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Front desk", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(Probable("10.0.0.50", SampleConfig.P4030))
            .Build();

        var (result, broker) = await RunSilent(scenario, "repair");

        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        Assert.Empty(broker.Requests);
        const string named = "The printers whose model must be confirmed may be the ticket printer too (the printer at 10.0.0.50 (probably a P-4030DN, queue \"Front desk\"))";
        Assert.Contains(named, Assert.Single(result.Findings, f => f.Code == FindingCode.Cancelled).Detail, StringComparison.Ordinal);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.MultipleSupportedPrinters && f.Detail.Contains(named, StringComparison.Ordinal));
    }
}
