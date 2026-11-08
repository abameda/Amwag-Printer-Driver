using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>The setup flow over the real <see cref="SetupSession"/> and the in-memory fakes; nothing touches this machine.</summary>
public sealed class SetupFlowTests
{
    private const string Address = "10.0.0.50";

    private readonly UpdateCollector _updates = new();

    [Fact]
    public async Task A_single_new_usb_printer_is_set_up_without_any_question()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var interaction = new ScriptedInteraction();
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), interaction, broker);

        Assert.Empty(interaction.Calls);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
        Assert.Equal(result.Outcome == FlowOutcome.Ready ? ExitCodes.Ready : ExitCodes.ReadyWithWarnings, result.ExitCode);
        WorkerRequest request = Assert.Single(broker.Requests);
        Assert.Equal(SetupMode.Setup, request.Mode);
        Assert.Equal(scenario.Log.SessionId, request.SessionId);
        Assert.Single(_updates.OfKind(FlowEventKind.ElevationRequested));
        Assert.Single(_updates.OfKind(FlowEventKind.TargetSelected));
        Assert.Equal(SampleConfig.P4030, result.Target?.Model?.Id);
        Assert.NotNull(result.Verification);
        Assert.NotNull(result.Report);
        Assert.Contains(scenario.Spooler.Forms, f => f.Name == SampleConfig.FormName && f.HasSize(SampleConfig.Ticket));
        Assert.Equal(StepState.Succeeded, _updates.FinalStates()[StepId.InstallDriver]);
    }

    [Fact]
    public async Task A_correctly_configured_printer_needs_no_elevation()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
        Assert.Empty(broker.Requests);
        Assert.Empty(_updates.OfKind(FlowEventKind.ElevationRequested));
        Assert.Equal(StepState.Skipped, _updates.FinalStates()[StepId.InstallDriver]);
    }

    [Fact]
    public async Task Several_printers_ask_exactly_once()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();
        var interaction = new ScriptedInteraction { Choose = choices => choices.Single(c => c.Model?.Id == SampleConfig.P4531) };

        FlowResult result = await Run(scenario.Environment(), interaction, new InProcessBroker(scenario.Environment()));

        Assert.Equal(new[] { "choose" }, interaction.Calls);
        Assert.Equal(SampleConfig.P4531, result.Target?.Model?.Id);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task Not_choosing_between_printers_offers_the_address_and_then_cancels()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();
        var interaction = new ScriptedInteraction { Choose = _ => null, Address = _ => null };

        FlowResult result = await Run(scenario.Environment(), interaction, new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(new[] { "choose", "address" }, interaction.Calls);
        Assert.True(Assert.Single(interaction.Prompts).OfferedPrintersDeclined);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.Cancelled } && f.Detail.Contains("2 supported printers were found and none was chosen", StringComparison.Ordinal));
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Silent_runs_with_several_printers_need_a_selection()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();
        var interaction = new SilentInteraction(CommandLineParser.Parse(["--silent"]));

        FlowResult result = await Run(scenario.Environment(), interaction, new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.SelectionRequired, result.ExitCode);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Declined_elevation_changes_nothing()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Declined, ExitCodes.ElevationRequired) { Error = "The administrator approval prompt was declined." },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.ElevationDeclined, result.Outcome);
        Assert.Equal(ExitCodes.ElevationRequired, result.ExitCode);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.ElevationDeclined && f.Severity == Severity.Error);
        Assert.Single(_updates.OfKind(FlowEventKind.ElevationDeclined));
        Assert.Null(result.Verification);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_failed_worker_fails_the_run_with_its_error()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.WorkerFailed, ExitCodes.InternalError) { Error = "worker ended without a result (exit code 10)" },
        };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal(ExitCodes.InternalError, result.ExitCode);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.OperationFailed && f.Detail.Contains("without a result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_found_asks_for_an_address_and_continues_with_it()
    {
        Scenario scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4030).Build();
        var interaction = new ScriptedInteraction { Address = _ => Address };
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), interaction, broker);

        Assert.Equal(new[] { "address" }, interaction.Calls);
        NetworkAddressPrompt prompt = Assert.Single(interaction.Prompts);
        Assert.True(prompt.NothingDetected);
        Assert.Null(prompt.PreviousAddress);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), result.Target?.Key);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
        Assert.Contains(scenario.Spooler.Ports, p => p.HostAddress == Address);
    }

    [Fact]
    public async Task Nothing_found_and_no_address_given_is_no_printer()
    {
        Scenario scenario = new ScenarioBuilder().Build();
        var interaction = new ScriptedInteraction { Address = _ => null };

        FlowResult result = await Run(scenario.Environment(), interaction, new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.NoPrinter, result.Outcome);
        Assert.Equal(ExitCodes.NoSupportedPrinter, result.ExitCode);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Address_prompts_stop_after_five_attempts()
    {
        Scenario scenario = new ScenarioBuilder().Build();
        int attempt = 0;
        var interaction = new ScriptedInteraction { Address = _ => "10.9.9." + ++attempt };

        FlowResult result = await Run(scenario.Environment(), interaction, new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.NoPrinter, result.Outcome);
        Assert.Equal(5, interaction.Prompts.Count);
        Assert.Equal("10.9.9.4", interaction.Prompts[^1].PreviousAddress);
    }

    [Fact]
    public async Task An_invalid_address_is_asked_again()
    {
        Scenario scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4030).Build();
        var answers = new Queue<string>(["not an address", Address]);
        var interaction = new ScriptedInteraction { Address = _ => answers.Dequeue() };

        FlowResult result = await Run(scenario.Environment(), interaction, new InProcessBroker(scenario.Environment()));

        Assert.Equal(2, interaction.Prompts.Count);
        Assert.Equal("not an address", interaction.Prompts[1].PreviousAddress);
        Assert.NotNull(interaction.Prompts[1].PreviousProblem);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), result.Target?.Key);
    }

    [Fact]
    public async Task A_confirmed_probable_match_is_set_up_as_asserted_by_the_operator()
    {
        Scenario scenario = ProbableNetworkPrinter();
        var interaction = new ScriptedInteraction { Confirm = candidate => candidate.Model?.Id == SampleConfig.P4030 };
        var broker = new InProcessBroker(scenario.Environment(edit: o => o with { PreferredAddress = Address }));

        FlowResult result = await Run(scenario.Environment(edit: o => o with { PreferredAddress = Address }), interaction, broker);

        Assert.Equal(new[] { "confirm" }, interaction.Calls);
        WorkerRequest request = Assert.Single(broker.Requests);
        Assert.True(request.Target.ModelAssertedByUser);
        Assert.Equal(SampleConfig.P4030, request.Target.ModelId);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task A_declined_probable_match_changes_nothing()
    {
        Scenario scenario = ProbableNetworkPrinter();
        var interaction = new ScriptedInteraction { Confirm = _ => false, Address = _ => null };
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });

        FlowResult result = await Run(environment, interaction, new InProcessBroker(environment));

        Assert.Equal(new[] { "confirm", "address" }, interaction.Calls);
        Assert.Equal(Address, interaction.Prompts.Single().PreviousAddress);
        Assert.Equal(FlowOutcome.NoPrinter, result.Outcome);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Info });
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task An_address_the_operator_enters_after_declining_the_configured_printer_is_the_printer_set_up()
    {
        // The entered address replaces the configured one for this run, so the operator is not asked about the same printer again.
        const string other = "10.0.0.9";
        Scenario scenario = ProbableNetworkPrinter();
        scenario.Network.SetIdentity(SampleConfig.NetworkIdentity(other, SampleConfig.P4531));
        var interaction = new ScriptedInteraction { Confirm = _ => false, Address = _ => other };
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(new[] { "confirm", "address" }, interaction.Calls);
        Assert.Equal(PrinterCandidate.NetworkKey(other), Assert.Single(broker.Requests).Target.Key);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task Silent_mode_confirms_a_probable_match_only_when_IT_asserted_it()
    {
        Scenario scenario = ProbableNetworkPrinter();
        var environment = scenario.Environment(edit: o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4030 });
        var interaction = new SilentInteraction(CommandLineParser.Parse(["--silent", "--address", Address, "--model", SampleConfig.P4030, "--confirm-model"]));
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
        Assert.True(Assert.Single(broker.Requests).Target.ModelAssertedByUser);
    }

    [Fact]
    public async Task Repair_with_several_queues_and_no_preference_asks_once_and_changes_only_the_chosen_one()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: false)
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .Build();
        var interaction = new ScriptedInteraction { Choose = choices => choices.Single(c => c.Model?.Id == SampleConfig.P4531) };
        var environment = scenario.Environment(SetupMode.Repair);
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Equal(new[] { "choose" }, interaction.Calls);
        string chosen = Assert.Single(_updates.OfKind(FlowEventKind.TargetSelected)).Target!.Key;
        Assert.Equal(chosen, result.Target?.Key);
        Assert.Equal(SampleConfig.P4531, result.Target?.Model?.Id);
        Assert.All(broker.Requests, r => Assert.Equal((SetupMode.Repair, chosen), (r.Mode, r.Target.Key)));
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == SampleConfig.DriverName(SampleConfig.P4030));
        Assert.Single(result.Verifications);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task Repair_without_an_existing_queue_points_to_setup()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var environment = scenario.Environment(SetupMode.Repair);

        FlowResult result = await Run(environment, new ScriptedInteraction(), new InProcessBroker(environment));

        Assert.Equal(FlowOutcome.NoPrinter, result.Outcome);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.NoSupportedPrinterFound && f.Data.GetValueOrDefault("suggestedMode") == "setup");
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_never_changes_anything_and_never_asks()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", driverBound: false)
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .WithSpooler(ServiceRunState.Running)
            .Build();
        var interaction = new ScriptedInteraction();
        var environment = scenario.Environment(SetupMode.Diagnostics);
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, interaction, broker);

        Assert.Empty(interaction.Calls);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
        Assert.Equal(2, result.Verifications.Count);
        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Equal(ExitCodes.NotReady, result.ExitCode);
        Assert.NotNull(result.Report);
    }

    [Fact]
    public async Task Diagnostics_does_not_start_a_stopped_spooler()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).Build();
        var environment = scenario.Environment(SetupMode.Diagnostics);
        var broker = new InProcessBroker(environment);

        await Run(environment, new ScriptedInteraction(), broker);

        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Unconfirmed_ticket_dimensions_block_setup()
    {
        Scenario scenario = new ScenarioBuilder().WithTicket(t => t with { DimensionsConfirmed = false }).WithUsbPrinter(driverBound: false).Build();
        var interaction = new ScriptedInteraction();
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), interaction, broker);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.Equal(ExitCodes.ConfigurationInvalid, result.ExitCode);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.DimensionsUnconfirmed && f.Severity == Severity.Error);
        Assert.Empty(interaction.Calls);
        Assert.Empty(broker.Requests);
        Assert.NotNull(result.Report);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task IT_may_run_with_unconfirmed_dimensions_explicitly()
    {
        Scenario scenario = new ScenarioBuilder().WithTicket(t => t with { DimensionsConfirmed = false }).WithUsbPrinter(driverBound: false).Build();
        var environment = scenario.Environment(edit: o => o with { AllowUnconfirmedDimensions = true });
        var broker = new InProcessBroker(environment);

        FlowResult result = await Run(environment, new ScriptedInteraction(), broker);

        Assert.NotEqual(FlowOutcome.Blocked, result.Outcome);
        Assert.True(Assert.Single(broker.Requests).AllowUnconfirmedDimensions);
    }

    [Fact]
    public async Task A_stopped_spooler_is_started_through_the_broker_first()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).Build();
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(WorkerArguments.SpoolerStartTarget, broker.Requests[0].Target.Key);
        Assert.True(scenario.Spooler.ServiceStatus.IsRunning);
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task A_spooler_that_does_not_start_blocks_with_the_spooler_exit_code()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).Build();
        scenario.Spooler.StartServiceSucceeds = false;
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.Equal(ExitCodes.SpoolerUnavailable, result.ExitCode);
    }

    [Fact]
    public async Task A_disabled_spooler_blocks_without_trying_to_start_it()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Disabled).Build();
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.Equal(ExitCodes.SpoolerUnavailable, result.ExitCode);
        Assert.Empty(broker.Requests);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Blockers_the_spooler_cannot_fix_stop_the_run_before_any_elevation()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithTicket(t => t with { DimensionsConfirmed = false })
            .WithInstalledUsbPrinter()
            .WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual)
            .Build();
        var broker = new InProcessBroker(scenario.Environment());

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.Empty(broker.Requests);
        Assert.Empty(_updates.OfKind(FlowEventKind.ElevationRequested));
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_running_spooler_that_is_set_to_disabled_does_not_block()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Running, ServiceStartMode.Disabled).Build();

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
        Assert.Contains(result.Findings, f => f.Code == FindingCode.SpoolerDisabled && f.Severity == Severity.Warning);
    }

    [Fact]
    public async Task An_elevated_process_applies_machine_steps_itself()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var broker = new InProcessBroker(scenario.Environment()) { IsElevated = true };

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), broker);

        Assert.Empty(broker.Requests);
        Assert.Empty(_updates.OfKind(FlowEventKind.ElevationRequested));
        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task Unexpected_exceptions_become_a_failed_result()
    {
        Scenario scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();
        var interaction = new ScriptedInteraction { Choose = _ => throw new InvalidOperationException("The panel broke.") };

        FlowResult result = await Run(scenario.Environment(), interaction, new InProcessBroker(scenario.Environment()));

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal(ExitCodes.InternalError, result.ExitCode);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.OperationFailed && f.Detail == "The panel broke.");
        Assert.True(scenario.Log.Contains("flow.failed"));
        Assert.NotNull(result.Report);
    }

    [Fact]
    public async Task Cancellation_ends_the_run_as_cancelled()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        FlowResult result = await new SetupFlow(scenario.Environment(), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()))
            .RunAsync(_updates, cancellation.Token);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExitCodes.Cancelled, result.ExitCode);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task A_failing_progress_display_does_not_break_the_run()
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        var flow = new SetupFlow(scenario.Environment(), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        FlowResult result = await flow.RunAsync(new Progress(_ => throw new ObjectDisposedException("MainForm")), CancellationToken.None);

        Assert.True(result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, Describe(result));
    }

    [Fact]
    public async Task The_report_carries_the_run()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        FlowResult result = await Run(scenario.Environment(), new ScriptedInteraction(), new InProcessBroker(scenario.Environment()));

        DiagnosticReport report = Assert.IsType<DiagnosticReport>(result.Report);
        Assert.Equal(SetupMode.Setup, report.Mode);
        Assert.NotEmpty(report.Plans);
        Assert.NotEmpty(report.ApplyResults);
        Assert.NotEmpty(report.Verifications);
    }

    private static Scenario ProbableNetworkPrinter() =>
        new ScenarioBuilder()
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Address,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer",
            })
            .Build();

    private async Task<FlowResult> Run(FlowEnvironment environment, ISetupInteraction interaction, InProcessBroker broker) =>
        await new SetupFlow(environment, interaction, broker).RunAsync(_updates, CancellationToken.None);

    private static string Describe(FlowResult result) =>
        $"Outcome {result.Outcome}; findings: " + string.Join("; ", result.Findings.Where(f => f.Severity != Severity.Info).Select(f => $"{f.Code}: {f.Detail}"));

    private sealed class Progress(Action<FlowUpdate> report) : IProgress<FlowUpdate>
    {
        public void Report(FlowUpdate value) => report(value);
    }
}
