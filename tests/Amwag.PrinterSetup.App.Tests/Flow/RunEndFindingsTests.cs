using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>
/// What the end of a run says. A restart Windows asked for keeps the run from "ready". A possible duplicate the run created
/// stays in the result. Starting the spooler is not a setup change. A step left unfinished by another step's failure says
/// so. A printer at the configured address that a queue the setup leaves unchanged prints to is named, not "not found".
/// </summary>
public sealed class RunEndFindingsTests
{
    private readonly UpdateCollector _updates = new();

    private static SilentInteraction Silent(params string[] args) => new(CommandLineParser.Parse(["--silent", .. args]));

    private Task<FlowResult> Run(FlowEnvironment environment, ISetupInteraction interaction, InProcessBroker broker) =>
        new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

    [Theory]
    [InlineData(true, ExitCodes.ReadyWithWarnings)]
    [InlineData(false, ExitCodes.Ready)]
    public async Task A_restart_Windows_asked_for_keeps_the_run_from_ready(bool rebootRequired, int exitCode)
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        scenario.Installer.DeviceInstallRequiresReboot = rebootRequired;
        var environment = scenario.Environment();

        FlowResult result = await Run(environment, Silent(), new InProcessBroker(environment));

        Assert.Equal((rebootRequired, exitCode), (result.RebootRequired, result.ExitCode));
    }

    [Fact]
    public async Task The_possible_duplicate_next_to_a_WSD_queue_stays_in_the_result_once_setup_added_the_queue()
    {
        const string Address = "10.12.0.50";
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithQueue("TA P-4030DN (WSD)", SampleConfig.DriverName(SampleConfig.P4030), "WSD-0001")
            .WithNetworkPrinter(Address, SampleConfig.P4030)
            .WithTicketForm()
            .Build();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });

        FlowResult result = await Run(environment, Silent("--address", Address), new InProcessBroker(environment));

        Assert.Contains(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        foreach (IReadOnlyList<Finding> findings in new[] { result.Findings, result.CurrentFindings, result.Report!.Findings })
        {
            Finding possible = Assert.Single(findings, f => f is { Code: FindingCode.DuplicateQueues, Severity: Severity.Warning });
            Assert.StartsWith($"A queue was added for {Address}, but queue 'TA P-4030DN (WSD)' may print to the same printer", possible.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Starting_the_spooler_alone_is_not_a_setup_change()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).Build();
        var environment = scenario.Environment();

        FlowResult result = await Run(environment, Silent(), new InProcessBroker(environment));

        Assert.Contains(result.Report!.ApplyResults, r => r.Results.Any(a => a.Action.Kind == ActionKind.StartSpooler));
        Assert.Equal(result.Findings, result.CurrentFindings);
    }

    [Fact]
    public async Task A_step_left_unfinished_by_another_step_s_failure_carries_that_failure_s_code()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        scenario.Installer.InstallFailure = new PrinterSetupException(FindingCode.DriverInstallFailed, "InstallPrinterDriverFromPackage failed (Win32 error 87).");
        var environment = scenario.Environment();

        await Run(environment, Silent(), new InProcessBroker(environment));

        ProgressUpdate installDriver = LastStep(StepId.InstallDriver);
        ProgressUpdate configureTicket = LastStep(StepId.ConfigureTicket);
        Assert.Equal((StepState.Failed, FindingCode.DriverInstallFailed), (installDriver.State, installDriver.Code));
        Assert.Equal((StepState.Warning, FindingCode.DriverInstallFailed), (configureTicket.State, configureTicket.Code));
        Assert.StartsWith("Not finished: ", configureTicket.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_with_nothing_failed_elsewhere_has_no_code()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var environment = scenario.Environment();

        await Run(environment, Silent(), new InProcessBroker(environment));

        Assert.All([LastStep(StepId.InstallDriver), LastStep(StepId.ConfigureTicket)], u => Assert.Null(u.Code));
    }

    [Fact]
    public async Task The_per_user_step_finds_the_queue_the_worker_configured_after_a_wrong_address_was_entered()
    {
        const string Ip = "10.12.0.50";
        const string HostName = "printer12.branch.local";
        // Three probes are allowed: with the wrong address entered first, the window's own probes stop before the ticket
        // queue's host name, while the worker probes the target's address and the queue hosts.
        Scenario scenario = new ScenarioBuilder()
            .WithConfig(c => c with { Connection = c.Connection with { MaxNetworkProbes = 3 } })
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.77", name: "Other", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(HostName, name: "Ticket printer", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null, snmp: false)
            .WithNetworkAlias(HostName, Ip)
            .WithTicketForm()
            .WithPerUserDefaults("Ticket printer", d => d.Scale = 80)
            .Build();
        var environment = scenario.Environment();
        var entered = new Queue<string>(["10.0.0.99", Ip]);
        var interaction = new ScriptedInteraction
        {
            Address = _ => entered.Dequeue(),
            ChooseModel = (_, models) => models.Single(m => m.Id == SampleConfig.P4030),
        };

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.DoesNotContain(result.Findings, f => f.Code == FindingCode.QueueMissing);
        Assert.Equal("Ticket printer", result.Verification?.Summary.QueueName);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
    }

    /// <summary>'Tickets' is pooled over the printer at 10.0.0.5 and another one at 10.0.0.6; the branch names 10.0.0.5.</summary>
    private static Scenario PooledTicketQueue() => new ScenarioBuilder()
        .WithModelDriver(SampleConfig.P4030)
        .WithTcpIpPort("10.0.0.5")
        .WithTcpIpPort("10.0.0.6")
        .WithQueue("Tickets", SampleConfig.DriverName(SampleConfig.P4030), "IP_10.0.0.5,IP_10.0.0.6")
        .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
        .WithNetworkPrinter("10.0.0.6", SampleConfig.P4030)
        .WithTicketForm()
        .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" })
        .Build();

    [Theory]
    [InlineData("setup")]
    [InlineData("repair")]
    public async Task A_printer_at_the_address_that_a_pooled_queue_prints_to_is_named_and_left_unchanged(string mode)
    {
        Scenario scenario = PooledTicketQueue();
        var environment = scenario.Environment(mode == "repair" ? SetupMode.Repair : SetupMode.Setup);

        FlowResult result = await Run(environment, Silent("--mode", mode), new InProcessBroker(environment));

        scenario.AssertNothingChanged();
        Finding none = Assert.Single(result.Findings, f => f is { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error });
        Assert.StartsWith("The printer at 10.0.0.5, the configured printer address, is not set up automatically: ", none.Detail, StringComparison.Ordinal);
        Assert.Contains("'Tickets'", none.Detail, StringComparison.Ordinal);
        // The window names the queue IT must change instead of advising the cable, and offers no Run Setup.
        Assert.Equal(("Tickets", "10.0.0.5"), (none.Data["leftUnchanged"], none.Data["address"]));
        Assert.False(none.Data.ContainsKey("suggestedMode"));
    }

    [Fact]
    public async Task The_driver_step_follows_what_the_worker_ran_when_it_found_the_queue_under_another_spelling()
    {
        const string Ip = "10.12.0.50";
        Scenario scenario = new ScenarioBuilder()
            .WithConfig(c => c with { Connection = c.Connection with { MaxNetworkProbes = 3 } })
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.77", name: "Other", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("printer12.branch.local", name: "Ticket printer", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null, snmp: false)
            .WithNetworkAlias("printer12.branch.local", Ip)
            .WithTicketForm()
            .Build();
        var environment = scenario.Environment();
        var entered = new Queue<string>(["10.0.0.99", Ip]);
        var interaction = new ScriptedInteraction
        {
            Address = _ => entered.Dequeue(),
            ChooseModel = (_, models) => models.Single(m => m.Id == SampleConfig.P4030),
        };

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, result.Outcome.ToString());
        Assert.Contains(result.Report!.ApplyResults.SelectMany(r => r.Results), a => a is { Action.Kind: ActionKind.ChangeQueueDriver, Outcome: ActionOutcome.Succeeded });
        Assert.Equal(StepState.Succeeded, LastStep(StepId.InstallDriver).State);
    }

    [Fact]
    public async Task A_port_created_without_its_queue_does_not_say_a_queue_was_added()
    {
        // An offline PC: the package cannot be obtained, so the port is created but the queue is not.
        const string Address = "10.12.0.50";
        Scenario scenario = new ScenarioBuilder()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithQueue("TA P-4030DN (WSD)", SampleConfig.MicrosoftPcl6ClassDriver, "WSD-0001")
            .WithNetworkPrinter(Address, SampleConfig.P4030)
            .Build();
        scenario.Packages.Failure = new PrinterSetupException(FindingCode.DriverPackageUnavailable, "No approved archive in 'drivers' and the download failed (HTTP 404).");
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });

        FlowResult result = await Run(environment, Silent("--address", Address), new InProcessBroker(environment));

        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.DoesNotContain(result.Findings, f => f.Detail.StartsWith("A queue was added", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repair_whose_model_question_was_answered_no_says_so_and_does_not_send_the_operator_to_setup()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(new NetworkIdentity { Address = "10.0.0.50", RawPortReachable = true, SnmpResponded = true, SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer" })
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);

        FlowResult result = await Run(environment, new ScriptedInteraction { Confirm = _ => false }, new InProcessBroker(environment));

        Finding none = Assert.Single(result.Findings, f => f is { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error });
        Assert.Equal("modelNotConfirmed", none.Data["reason"]);
        Assert.False(none.Data.ContainsKey("suggestedMode"));
    }

    [Fact]
    public async Task Repair_next_to_printers_without_a_queue_says_so_and_offers_setup()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", driverBound: false)
            .WithUsbPrinter(SampleConfig.P4531, "USB002", driverBound: false)
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);

        FlowResult result = await Run(environment, Silent("--mode", "repair"), new InProcessBroker(environment));

        Finding none = Assert.Single(result.Findings, f => f is { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error });
        Assert.Equal(("printersWithoutQueue", "setup"), (none.Data["reason"], none.Data["suggestedMode"]));
    }

    [Fact]
    public async Task Repair_in_the_window_does_not_ask_to_confirm_a_printer_it_could_not_change()
    {
        // A probable P-4030DN at the branch address without a queue: Setup, not Repair, sets it up and asks then.
        const string Address = "10.20.30.40";
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer" })
            .WithBranch(new BranchOverride { PrinterAddress = Address })
            .Build();
        var environment = scenario.Environment(SetupMode.Repair);
        var interaction = new ScriptedInteraction();

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Empty(interaction.Calls);
        Finding none = Assert.Single(result.Findings, f => f is { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error });
        Assert.Equal(("noQueue", PrinterCandidate.NetworkKey(Address), "setup"), (none.Data["reason"], none.Data["target"], none.Data["suggestedMode"]));
        scenario.AssertNothingChanged();
    }

    private ProgressUpdate LastStep(StepId step) =>
        _updates.OfKind(FlowEventKind.Step).Select(u => u.Progress!).Last(p => p.Step == step && p.Action is null);
}
