using System.Globalization;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// What the result screen says about the state a run ended in: no warning the run already fixed, a sign of tampering first and
/// never left out, then the failure that ended a failed run; in Diagnostics the ticket printer's problems, not those of the
/// other printers checked; and no "nothing was changed" after changes were made (starting the Print Spooler is not one).
/// </summary>
public sealed class ResultFindingsTests
{
    private static readonly Strings English = Strings.English;

    [Fact]
    public async Task A_fresh_install_lists_only_the_warnings_that_still_hold()
    {
        // Everything is installed and verified; only the size modern apps receive is a real warning.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with
            {
                PrintTicketOverride = new PrintTicketMediaInfo { Available = true, MediaSizeName = "ISOA4", WidthMicrometers = 210000, HeightMicrometers = 297000 },
            })
            .Build();

        FlowResult result = await RunAsync(scenario);
        List<string> notes = NotesOf(result);

        Assert.Equal(FlowOutcome.ReadyWithWarnings, result.Outcome);
        Assert.Equal(CheckStatus.Pass, result.Verification!.Checks.Single(c => c.Item == VerificationItem.DeviceConnected).Status);
        // What the first plan found ("the driver will be installed", "unplug the USB cable", "the size will be created") was fixed.
        Assert.Contains(result.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        foreach (FindingCode fixedBefore in new[] { FindingCode.DriverMissing, FindingCode.DeviceDriverNotBound, FindingCode.FormMissing })
            Assert.DoesNotContain(notes, n => n.StartsWith(English.FindingTitle(fixedBefore) + ".", StringComparison.Ordinal));
        Assert.Contains(English.FindingTitle(FindingCode.PrintTicketMismatch) + ". " + English.FindingText(FindingCode.PrintTicketMismatch), notes);
    }

    [Fact]
    public async Task A_tampered_package_is_the_first_warning_of_a_run_ready_with_warnings()
    {
        // The approved driver is installed; the package needed only to link the USB device fails its integrity check.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithUsbQueue(driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithTicketForm()
            .Build();
        scenario.Packages.Failure = new PrinterSetupException(FindingCode.DriverPackageIntegrityFailed, "The offline copy has SHA-256 0000 instead of 9f2c.");

        FlowResult result = await RunAsync(scenario);
        List<string> notes = NotesOf(result);

        Assert.Equal(FlowOutcome.ReadyWithWarnings, result.Outcome);
        Assert.StartsWith(English.FindingTitle(FindingCode.DriverPackageIntegrityFailed) + ".", notes[0], StringComparison.Ordinal);
        // The queue was switched to the approved driver: "the printer will be switched" no longer holds.
        Assert.DoesNotContain(notes, n => n.StartsWith(English.FindingTitle(FindingCode.QueueDriverMismatch) + ".", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(FindingCode.DriverPackageIntegrityFailed)]
    [InlineData(FindingCode.DriverSignatureInvalid)]
    [InlineData(FindingCode.ConfigurationInvalid)]
    public void A_sign_of_tampering_or_a_broken_configuration_leads_the_warnings_and_is_never_left_out(FindingCode alarming)
    {
        Finding[] others =
        [
            Warning(FindingCode.DeviceDriverNotBound),
            Warning(FindingCode.DuplicateQueues),
            Warning(FindingCode.QueueLeftUnchanged),
            Warning(FindingCode.PrintTicketMismatch),
            Warning(FindingCode.PrinterOffline),
        ];
        var result = UiSamples.ReadyWithWarnings() with { Findings = [.. others, Warning(alarming)], CurrentFindings = [.. others, Warning(alarming)] };

        List<string> notes = NotesOf(result);

        Assert.Equal(4, notes.Count);
        Assert.Equal(English.FindingTitle(alarming) + ". " + English.FindingText(alarming), notes[0]);
        Assert.StartsWith(English.FindingTitle(FindingCode.DeviceDriverNotBound) + ".", notes[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_configuration_is_the_headline_among_warnings_of_a_run_that_is_not_ready()
    {
        var result = new FlowResult(FlowOutcome.NotReady)
        {
            Findings = [Warning(FindingCode.PrinterOffline), Warning(FindingCode.ConfigurationInvalid)],
            Report = UiSamples.Report(SetupMode.Setup),
        };

        Assert.Equal(English.FindingTitle(FindingCode.ConfigurationInvalid) + ". " + English.FindingText(FindingCode.ConfigurationInvalid), NotesOf(result)[0]);
    }

    [Fact]
    public void The_headline_of_a_run_stopped_after_changes_is_not_what_the_plan_found_before_them()
    {
        // The driver was installed; then the run was stopped. "The driver will be installed" no longer holds.
        Finding stopped = Warning(FindingCode.Cancelled);
        var result = new FlowResult(FlowOutcome.Cancelled)
        {
            Findings = [Warning(FindingCode.DriverMissing), stopped],
            CurrentFindings = [stopped],
            Report = UiSamples.Report(SetupMode.Setup) with { ApplyResults = [Applied(ActionKind.InstallDriver)] },
        };

        Assert.Equal(English.FindingTitle(FindingCode.Cancelled) + ". " + English.FindingText(FindingCode.Cancelled), NotesOf(result)[0]);
    }

    [Fact]
    public void A_result_built_outside_a_run_shows_its_own_warnings()
    {
        List<string> notes = NotesOf(UiSamples.ReadyWithWarnings());

        Assert.Contains(notes, n => n.StartsWith(English.FindingTitle(FindingCode.ApplicationMayOverrideDefaults) + ".", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.StartsWith(English.FindingTitle(FindingCode.PaperSourceNotFound) + ".", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public async Task A_run_blocked_after_changes_does_not_say_that_nothing_was_changed(UiLanguage language)
    {
        // The installed driver's paper range is only readable once it is installed: the machine steps ran, then the per-user
        // step found the blocker.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { MinWidthTenthsOfMillimeter = 900 })
            .Build();

        FlowResult result = await RunAsync(scenario);
        var t = Strings.For(language);
        List<string> texts = TextsOf(result, language);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.NotEmpty(scenario.Installer.Calls);
        Assert.Contains(t.Text("outcome.Blocked.afterChanges"), texts);
        Assert.DoesNotContain(t.OutcomeText(FlowOutcome.Blocked), texts);
    }

    [Fact]
    public async Task A_run_blocked_before_any_change_says_that_nothing_was_changed()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPackage(p => p with { Approved = false }).Build();

        FlowResult result = await RunAsync(scenario);
        List<string> texts = TextsOf(result, UiLanguage.English);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        scenario.AssertNothingChanged();
        Assert.Contains("A required condition is not met. Nothing was changed.", texts);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public async Task A_run_blocked_after_only_the_print_spooler_was_started_says_that_nothing_was_changed(UiLanguage language)
    {
        // The helper starts the stopped Print Spooler (the first approval); the plan is then blocked: the driver must come from a
        // package IT has not approved. No driver, queue, port, form or preference was touched.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithPackage(p => p with { Approved = false })
            .WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual)
            .Build();

        FlowResult result = await RunAsync(scenario);
        var t = Strings.For(language);
        List<string> texts = TextsOf(result, language);

        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.Equal(ActionKind.StartSpooler, Assert.Single(result.Report!.ApplyResults.SelectMany(r => r.Results)).Action.Kind);
        Assert.Equal(["TryStartService"], scenario.Spooler.Writes.Select(w => w.Operation));
        Assert.Empty(scenario.Installer.Calls);
        // Before: "Some changes were made, but the setup cannot finish", while both apply steps said nothing was changed.
        Assert.Contains(t.OutcomeText(FlowOutcome.Blocked), texts);
        Assert.DoesNotContain(t.Text("outcome.Blocked.afterChanges"), texts);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public void A_blocked_diagnostics_without_a_report_says_that_nothing_was_changed(UiLanguage language)
    {
        // Diagnostics knows the ticket printer, so its current findings are that printer's only; the office printer's finding stays
        // in Findings. The report could not be created, so no apply results tell what ran.
        var blocker = new Finding(FindingCode.ProtectedPrintModeEnabled, Severity.Error, "Windows protected print mode is on.");
        var ticket = new Finding(FindingCode.DriverReady, Severity.Info, "Queue 'AMWAG Ticket Printer' uses an accepted driver.");
        var office = new Finding(FindingCode.GlobalDefaultsIncorrect, Severity.Error, "The queue's default paper.") { Data = new Dictionary<string, string> { ["queue"] = "Office" } };
        var result = UiSamples.Ready(SetupMode.Diagnostics) with
        {
            Outcome = FlowOutcome.Blocked,
            Report = null,
            Findings = [blocker, office, ticket],
            CurrentFindings = [blocker, ticket],
        };
        var t = Strings.For(language);

        List<string> texts = TextsOf(result, language, SetupMode.Diagnostics);

        // Before: "Some changes were made, but the setup cannot finish", although Diagnostics never changes anything.
        Assert.Contains(t.OutcomeText(FlowOutcome.Blocked, SetupMode.Diagnostics), texts);
        Assert.DoesNotContain(t.Text("outcome.Blocked.afterChanges", SetupMode.Diagnostics), texts);
        // Setup and Repair, which apply changes, still read the difference as changes made.
        Assert.Contains(t.Text("outcome.Blocked.afterChanges"), TextsOf(result, language, SetupMode.Setup));
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public async Task The_headline_of_a_run_whose_helper_gave_no_result_is_that_failure(UiLanguage language)
    {
        const string reason = "The pipe to the elevated worker failed: Pipe is broken.";
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        FlowResult result = await RunAsync(scenario, broker: environment => new InProcessBroker(environment)
        {
            Answer = new ElevatedRunResult(ElevationOutcome.WorkerFailed, ExitCodes.InternalError) { Error = reason },
        });
        var t = Strings.For(language);
        List<string> notes = NotesOf(result, language: language);

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        // The read-back still finds the driver missing; before, its "The approved printer driver will be installed." was the
        // headline, and why the run failed was nowhere on the screen.
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.DriverMissing, Severity: Severity.Error });
        Assert.Equal(t.FindingTitle(FindingCode.OperationFailed) + ". " + t.FindingText(FindingCode.OperationFailed), notes[0]);
        Assert.Contains(TextsOf(result, language), text => text.Contains("OperationFailed: " + reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_sign_of_tampering_still_comes_before_the_failure_that_ended_the_run()
    {
        var result = new FlowResult(FlowOutcome.Failed)
        {
            Findings =
            [
                new Finding(FindingCode.DriverMissing, Severity.Error, "Driver 'TA P-4030DN KX' is not installed for Windows x64."),
                new Finding(FindingCode.OperationFailed, Severity.Error, "The elevated worker did not connect within 60 seconds."),
                new Finding(FindingCode.DriverPackageIntegrityFailed, Severity.Error, "The offline copy has SHA-256 0000 instead of 9f2c."),
            ],
            Report = UiSamples.Report(SetupMode.Setup),
        };

        Assert.StartsWith(English.FindingTitle(FindingCode.DriverPackageIntegrityFailed) + ".", NotesOf(result)[0], StringComparison.Ordinal);
        Assert.StartsWith(English.FindingTitle(FindingCode.OperationFailed) + ".", NotesOf(result with { Findings = result.Findings.Take(2).ToArray() })[0], StringComparison.Ordinal);
        // Only a failed run puts its own failure first; a run that is merely not ready keeps the order found.
        Assert.StartsWith(English.FindingTitle(FindingCode.DriverMissing) + ".", NotesOf(result with { Outcome = FlowOutcome.NotReady, Findings = result.Findings.Take(2).ToArray() })[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public async Task Diagnostics_explains_the_ticket_printer_not_an_office_printer_checked_for_the_report(UiLanguage language)
    {
        // branch.json names the model, so the USB P-4030DN is the ticket printer; only the employee's own preferences differ
        // (scale 80). The office P-4531DN at 10.0.0.60 is set up for A4, as office printers are; Diagnostics checks it too.
        const string office = "10.0.0.60";
        string ticketQueue = SampleConfig.DriverName(SampleConfig.P4030);
        Scenario scenario = new ScenarioBuilder()
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .WithInstalledUsbPrinter()
            .WithPerUserDefaults(ticketQueue, d => d.Scale = 80)
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(office, SampleConfig.P4531, name: "Office")
            .WithNetworkPrinter(office, SampleConfig.P4531)
            .Build();

        FlowResult result = await RunAsync(scenario, SetupMode.Diagnostics);
        var t = Strings.For(language);
        List<string> notes = NotesOf(result, SetupMode.Diagnostics, language);

        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Equal(ticketQueue, result.Verification!.Summary.QueueName);
        Assert.Contains(result.Verifications.Single(v => v.Summary.QueueName == "Office").Findings, f => f is { Code: FindingCode.GlobalDefaultsIncorrect, Severity: Severity.Error });
        // Before: the office printer's "Printer defaults need correcting" was the headline of the ticket printer's result.
        Assert.Equal(t.FindingTitle(FindingCode.UserDefaultsIncorrect) + ". " + t.FindingText(FindingCode.UserDefaultsIncorrect), notes[0]);
        Assert.DoesNotContain(notes, n => n.StartsWith(t.FindingTitle(FindingCode.GlobalDefaultsIncorrect), StringComparison.Ordinal));
        // The details still list every printer checked, and the report keeps their findings.
        Assert.Equal(2, result.Verifications.Count);
        Assert.Contains(result.Report!.Findings.Concat(result.Findings), f => f.Code == FindingCode.GlobalDefaultsIncorrect);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public async Task Diagnostics_lists_only_the_ticket_printers_warnings()
    {
        // The ticket printer is set up; its only warning is the size modern apps receive. The office printer is set up for A4.
        const string office = "10.0.0.60";
        Scenario scenario = new ScenarioBuilder()
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .WithInstalledUsbPrinter()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with
            {
                PrintTicketOverride = new PrintTicketMediaInfo { Available = true, MediaSizeName = "ISOA4", WidthMicrometers = 210000, HeightMicrometers = 297000 },
            })
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(office, SampleConfig.P4531, name: "Office")
            .WithNetworkPrinter(office, SampleConfig.P4531)
            .Build();

        FlowResult result = await RunAsync(scenario, SetupMode.Diagnostics);
        List<string> notes = NotesOf(result, SetupMode.Diagnostics);

        Assert.Equal(FlowOutcome.ReadyWithWarnings, result.Outcome);
        Assert.Contains(English.FindingTitle(FindingCode.PrintTicketMismatch) + ". " + English.FindingText(FindingCode.PrintTicketMismatch), notes);
        Assert.DoesNotContain(notes, n => n.StartsWith(English.FindingTitle(FindingCode.GlobalDefaultsIncorrect), StringComparison.Ordinal));
        scenario.AssertNothingChanged();
    }

    [Fact]
    public void Diagnostics_takes_its_headline_from_the_findings_about_the_ticket_printer()
    {
        // The flow's findings keep every printer checked (the office printer's error first); the current ones are the ticket printer's.
        var office = new Finding(FindingCode.GlobalDefaultsIncorrect, Severity.Error, "The queue's default paper.") { Data = new Dictionary<string, string> { ["queue"] = "Office" } };
        var ticket = new Finding(FindingCode.UserDefaultsIncorrect, Severity.Error, "Your preferences for 'AMWAG Ticket Printer' print at 80%.");
        var result = UiSamples.Ready(SetupMode.Diagnostics) with { Outcome = FlowOutcome.NotReady, Findings = [office, ticket], CurrentFindings = [ticket] };

        Assert.Equal(English.FindingTitle(FindingCode.UserDefaultsIncorrect) + ". " + English.FindingText(FindingCode.UserDefaultsIncorrect), NotesOf(result, SetupMode.Diagnostics)[0]);
        // Setup and Repair configure only the ticket printer: any error found holds.
        Assert.Equal(English.FindingTitle(FindingCode.GlobalDefaultsIncorrect) + ". " + English.FindingText(FindingCode.GlobalDefaultsIncorrect), NotesOf(result, SetupMode.Repair)[0]);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public async Task A_printer_entry_the_probe_limit_left_out_is_shown_as_not_checked_next_to_a_model_that_could_not_be_read(UiLanguage language)
    {
        // The detection warning about 'Tickets', which was never probed, as a run reports it, next to a printer that answered
        // without SNMP. Before: one note for both, saying that "the printer answers but does not report its model", which is
        // false for the entry that was not checked.
        FlowResult diagnosed = await RunAsync(QueueHostDecisionTests.TicketQueueNotChecked(), SetupMode.Diagnostics);
        Finding notProbed = Assert.Single(diagnosed.Findings, f => f.Code == FindingCode.NetworkIdentityUnavailable);
        var snmpOff = new Finding(FindingCode.NetworkIdentityUnavailable, Severity.Warning, "10.0.0.9 did not answer SNMP, so its model cannot be read.")
        {
            Data = new Dictionary<string, string> { ["address"] = "10.0.0.9" },
        };
        FlowResult result = UiSamples.ReadyWithWarnings() with { Findings = [notProbed, snmpOff] };
        Strings t = language == UiLanguage.Arabic ? Strings.Arabic : English;

        List<string> notes = NotesOf(result, language: language);

        Assert.Contains(t.Text("finding.NetworkIdentityUnavailable.notChecked.title") + ". " + t.Text("finding.NetworkIdentityUnavailable.notChecked.text"), notes);
        Assert.Contains(t.FindingTitle(FindingCode.NetworkIdentityUnavailable) + ". " + t.FindingText(FindingCode.NetworkIdentityUnavailable), notes);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public async Task A_queue_added_next_to_another_entry_for_the_printer_names_that_entry_without_saying_it_is_the_same_printer(UiLanguage language)
    {
        // Windows' own IPP class-driver queue 'Tickets' for the printer at the branch address; Setup adds its own queue next to it.
        // Before: Ready with no note. The note may not say "several entries for this printer": a WSD entry may be another printer.
        FlowResult result = await RunAsync(TicketQueueElsewhereTests.IppQueueOnly("plain"));
        Strings t = language == UiLanguage.Arabic ? Strings.Arabic : English;

        List<string> notes = NotesOf(result, language: language);

        Assert.Equal(FlowOutcome.ReadyWithWarnings, result.Outcome);
        Assert.Contains(t.Text("finding.DuplicateQueues.queueAdded.title") + ". " + t.Format("finding.DuplicateQueues.queueAdded.text", "Tickets"), notes);
        Assert.DoesNotContain(t.FindingTitle(FindingCode.DuplicateQueues) + ". " + t.FindingText(FindingCode.DuplicateQueues), notes);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public async Task Every_entry_next_to_which_a_queue_was_added_is_named_the_one_that_prints_to_the_printer_first(UiLanguage language)
    {
        // Windows' IPP entry for the ticket printer at the branch address, and an office HP that Windows added on WSD with the
        // IPP class driver. Before: one note, naming the HP entry only.
        const string url = "http://10.0.0.5:631/ipp/print";
        const string wsd = "WSD-5d6a1f3c-1111-2222-3333-444455556666";
        var builder = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver("Microsoft IPP Class Driver")
            .WithQueue("HP LaserJet Pro M404dn", "Microsoft IPP Class Driver", wsd)
            .WithInternetPort(url)
            .WithQueue("TA P-4030DN (IPP)", "Microsoft IPP Class Driver", url)
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" });
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = wsd, Kind = PortKind.Wsd, MonitorName = "WSD Port" });
        FlowResult result = await RunAsync(builder.Build());
        Strings t = language == UiLanguage.Arabic ? Strings.Arabic : English;

        List<string> notes = NotesOf(result, language: language);

        Assert.Equal(FlowOutcome.ReadyWithWarnings, result.Outcome);
        string listed = string.Format(CultureInfo.InvariantCulture, t.Text("finding.DuplicateQueues.queueAdded.many"),
            t.DataList(["TA P-4030DN (IPP)", "HP LaserJet Pro M404dn"]));
        Assert.Contains(t.Text("finding.DuplicateQueues.queueAdded.title") + ". " + listed, notes);
    }

    private static Task<FlowResult> RunAsync(Scenario scenario, SetupMode mode = SetupMode.Setup, Func<FlowEnvironment, IElevationBroker>? broker = null)
    {
        FlowEnvironment environment = scenario.Environment(mode);
        return new SetupFlow(environment, new ScriptedInteraction(), broker?.Invoke(environment) ?? new InProcessBroker(environment)).RunAsync(new UpdateCollector(), CancellationToken.None);
    }

    private static Finding Warning(FindingCode code) => new(code, Severity.Warning, code + " detail");

    private static ApplyResult Applied(ActionKind kind) =>
        new() { Results = [new ActionResult(new PlannedAction(kind, true, kind.ToString()), ActionOutcome.Succeeded, "Done")] };

    /// <summary>The notes the result screen of a window in <paramref name="mode"/> shows, as screen readers hear them ("Title. Text").</summary>
    private static List<string> NotesOf(FlowResult result, SetupMode mode = SetupMode.Setup, UiLanguage language = UiLanguage.English)
    {
        List<string> notes = [];
        Sta.Run(() =>
        {
            using var form = new MainForm(TestRuntime.Create(language == UiLanguage.Arabic ? "ar" : "en", mode));
            form.ShowResult(result);
            notes = All(form.CurrentView!).OfType<NotePanel>().Select(n => n.AccessibleName ?? "").ToList();
        });
        return notes;
    }

    /// <summary>Every label text on the result screen of a window in <paramref name="mode"/> (the banner's title and sentence among them).</summary>
    private static List<string> TextsOf(FlowResult result, UiLanguage language, SetupMode mode = SetupMode.Setup)
    {
        List<string> texts = [];
        Sta.Run(() =>
        {
            using var form = new MainForm(TestRuntime.Create(language == UiLanguage.Arabic ? "ar" : "en", mode));
            form.ShowResult(result);
            texts = All(form.CurrentView!).OfType<Label>().Select(l => l.Text).ToList();
        });
        return texts;
    }

    private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));
}
