using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// Ends whose outcome alone would mislead the employee, in the window and in both languages: a printer without a queue is not sent
/// to Repair, and Repair that found no printer it can change leads to Setup; IT's refused model does not call a supported printer
/// unsupported and is not offered again unchanged; Repair speaks of Repair, not of Setup.
/// </summary>
public sealed class ResultEndTextsTests
{
    private const string Address = "10.20.30.40";

    public static TheoryData<string, string> FreshInstallFailures => new()
    {
        { "packageUnavailable", "en" },
        { "packageUnavailable", "ar" },
        { "installFailed", "en" },
        { "installFailed", "ar" },
    };

    [Theory]
    [MemberData(nameof(FreshInstallFailures))]
    public void A_setup_that_ended_without_a_queue_offers_try_again_and_never_repair(string failure, string language) => Sta.Run(() =>
    {
        // A fresh install whose driver could not be installed (offline PC, or Windows refused it): no queue was added.
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        if (failure == "packageUnavailable")
            scenario.Packages.Failure = new PrinterSetupException(FindingCode.DriverPackageUnavailable, "No approved archive in 'drivers' and the download failed (HTTP 404).");
        else
            scenario.Installer.InstallFailure = new PrinterSetupException(FindingCode.DriverInstallFailed, "InstallPrinterDriverFromPackage failed (Win32 error 87).");
        var log = new MemoryLog();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Setup, language: language, log: log);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, SetupMode.Setup);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Equal("", result.Verification!.Summary.QueueName);
        // Before: the Repair button and "Run Repair or try again"; Repair then said no ticket printer was found.
        Assert.False(view.CanRepair);
        Assert.True(view.CanTryAgain);
        Assert.Equal(t.Text("result.tryAgain"), ((Button)form.AcceptButton!).Text);
        Assert.Contains(t.Text("outcome.NotReady.noQueue"), labels);
        Assert.DoesNotContain(t.OutcomeText(FlowOutcome.NotReady), labels);
        if (language == "en")
            Assert.Contains("The printer has not been added to Windows. Solve the problem shown below, then try again.", labels);

        // Try again runs Setup again.
        WindowRun.Press(form, t.Text("result.tryAgain"));
        Assert.Equal(["Setup", "Setup"], log.Entries.Where(e => e.Event == "ui.run.started").Select(e => e.Data?["mode"]?.ToString()).ToList());
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Repair_of_a_printer_without_a_queue_says_so_and_runs_setup_instead(string language) => Sta.Run(() =>
    {
        // The ticket printer is connected, but it was never added to Windows: Repair never adds a printer.
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var log = new MemoryLog();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Repair, language: language, log: log);
        Strings t = Strings.For(form.Language);

        FlowResult repair = WindowRun.Run(form, SetupMode.Repair);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        Assert.Equal((FlowOutcome.NoPrinter, ExitCodes.NoSupportedPrinter), (repair.Outcome, repair.ExitCode));
        scenario.AssertNothingChanged();
        // Before: "No ticket printer found. Connect the printer with its USB cable...", and Try again repeated Repair.
        Assert.Contains(t.Text("outcome.NoPrinter.needsSetup.title"), labels);
        Assert.Contains(t.Text("outcome.NoPrinter.needsSetup.text"), labels);
        Assert.DoesNotContain(t.OutcomeTitle(FlowOutcome.NoPrinter), labels);
        Assert.DoesNotContain(t.OutcomeText(FlowOutcome.NoPrinter), labels);
        Assert.Equal(t.Text("finding.NoSupportedPrinterFound.needsSetup.title") + ". " + t.Text("finding.NoSupportedPrinterFound.needsSetup.text"), WindowRun.Notes(view)[0]);
        Assert.Equal(t.StepTitle(StepId.Detect) + ": " + t.Text("step.Detect.notSetUp"), form.Steps.Describe(StepId.Detect));
        Assert.False(view.CanTryAgain);
        Assert.True(view.CanRunSetup);
        Assert.Equal(t.Text("result.runSetup"), ((Button)form.AcceptButton!).Text);
        if (language == "en")
        {
            Assert.Contains("Nothing to repair yet", labels);
            Assert.Equal("Printer not set up yet. This printer has not been added to Windows yet, so Repair cannot change it. Choose Run Setup to set it up.", WindowRun.Notes(view)[0]);
            Assert.Equal("Detecting printer: Printer not set up yet", form.Steps.Describe(StepId.Detect));
        }
        else
        {
            Assert.Equal("تشغيل الإعداد", ((Button)form.AcceptButton!).Text);
        }

        // Run Setup sets the printer up.
        FlowResult setup = WindowRun.Press(form, t.Text("result.runSetup"));
        Assert.Equal(["Repair", "Setup"], log.Entries.Where(e => e.Event == "ui.run.started").Select(e => e.Data?["mode"]?.ToString()).ToList());
        Assert.True(setup.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, setup.Outcome.ToString());
        Assert.NotEmpty(setup.Verification!.Summary.QueueName);
        Assert.True(Assert.IsType<ResultView>(form.CurrentView).CanRepair);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Repair_that_found_no_printer_runs_setup_instead_of_repeating_itself(string language) => Sta.Run(() =>
    {
        using MainForm form = WindowRun.Create(new ScenarioBuilder().Build(), SetupMode.Repair, language: language);
        Strings t = Strings.For(form.Language);

        FlowResult repair = WindowRun.Run(form, SetupMode.Repair);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        Assert.Equal(FlowOutcome.NoPrinter, repair.Outcome);
        Assert.Contains(t.OutcomeTitle(FlowOutcome.NoPrinter), labels);
        Assert.Contains(t.Text("outcome.NoPrinter.runSetup"), labels);
        Assert.Equal(t.FindingTitle(FindingCode.NoSupportedPrinterFound) + ". " + t.FindingText(FindingCode.NoSupportedPrinterFound), WindowRun.Notes(view)[0]);
        Assert.Equal(t.StepTitle(StepId.Detect) + ": " + t.StepStatus(StepId.Detect, StepState.Failed), form.Steps.Describe(StepId.Detect));
        Assert.False(view.CanTryAgain);
        Assert.True(view.CanRunSetup);
        Assert.Equal(t.Text("result.runSetup"), ((Button)form.AcceptButton!).Text);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Diagnostics_of_a_printer_never_set_up_does_not_offer_repair(string language) => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics, language: language);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, SetupMode.Diagnostics);
        var view = Assert.IsType<ResultView>(form.CurrentView);

        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.NotNull(result.Target);
        scenario.AssertNothingChanged();
        // Before: the Repair button, which ended with "No ticket printer found".
        Assert.False(view.CanRepair);
        Assert.Contains(t.Text("outcome.NotReady.noQueue.Diagnostics"), WindowRun.Labels(view));
        if (language == "en")
            Assert.Contains("The printer has not been added to Windows yet. Run Setup to set it up.", WindowRun.Labels(view));
    });

    [Fact]
    public void Diagnostics_offers_repair_for_a_printer_with_a_queue_and_among_several_but_not_when_none_was_found() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en", SetupMode.Diagnostics));

        form.ShowResult(UiSamples.Diagnostics());
        Assert.True(((ResultView)form.CurrentView!).CanRepair);

        // Several printers and none known to be the ticket printer, one of them with a queue: Repair asks which one.
        var several = new Finding(FindingCode.MultipleSupportedPrinters, Severity.Warning, "2 supported printers were found and none is known to be the ticket printer.")
        {
            Data = new Dictionary<string, string> { ["choices"] = UiSamples.UsbPrinter.Key + ", " + UiSamples.NetworkPrinter.Key },
        };
        var among = UiSamples.Diagnostics() with
        {
            Outcome = FlowOutcome.NotReady,
            Target = null,
            Verification = null,
            Findings = [several],
            Report = UiSamples.Report(SetupMode.Diagnostics) with { Candidates = [UiSamples.UsbPrinter, UiSamples.NetworkPrinter] },
        };
        form.ShowResult(among);
        Assert.True(((ResultView)form.CurrentView!).CanRepair);
        Assert.Contains(Strings.English.OutcomeText(FlowOutcome.NotReady), WindowRun.Labels(form.CurrentView!));

        // Changed expectation: a result about no single printer that names none Repair could ask about (before, any such result
        // offered Repair) offers none, as for nothing usable at the configured address.
        form.ShowResult(among with { Findings = [] });
        Assert.False(((ResultView)form.CurrentView!).CanRepair);
        var nowhere = new Finding(FindingCode.NoSupportedPrinterFound, Severity.Error, "No supported TA Triumph-Adler printer is at 10.0.0.99, the configured printer address.")
        {
            Data = new Dictionary<string, string> { ["address"] = "10.0.0.99" },
        };
        form.ShowResult(among with { Findings = [nowhere] });
        Assert.False(((ResultView)form.CurrentView!).CanRepair);

        form.ShowResult(new FlowResult(FlowOutcome.NoPrinter) { Report = UiSamples.Report(SetupMode.Diagnostics) });
        Assert.False(((ResultView)form.CurrentView!).CanRepair);
    });

    public static TheoryData<string, SetupMode, bool> RefusedModels => new()
    {
        { "en", SetupMode.Setup, true },
        { "ar", SetupMode.Setup, true },
        { "en", SetupMode.Setup, false },
        { "en", SetupMode.Repair, false },
        { "ar", SetupMode.Repair, false },
    };

    [Theory]
    [MemberData(nameof(RefusedModels))]
    public void A_model_from_IT_that_the_printer_contradicts_names_both_models_and_is_not_offered_again(string language, SetupMode mode, bool withQueue) => Sta.Run(() =>
    {
        // The window is started with --address 10.20.30.40 --model P-4531DN --confirm-model; the printer there is an exactly
        // identified P-4030DN, set up as "Tickets" or not yet added to Windows.
        var builder = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4030);
        if (withQueue)
            builder.WithModelDriver(SampleConfig.P4030).WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets").WithTicketForm();
        Scenario scenario = builder.Build();
        var commandLine = new CommandLineOptions { Language = language, Mode = mode, Address = Address, ModelId = SampleConfig.P4531, ConfirmModel = true };
        using MainForm form = WindowRun.Create(scenario, mode, options: o => o with { PreferredAddress = Address, PreferredModelId = SampleConfig.P4531 }, commandLine: commandLine);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, mode);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        Assert.Equal((FlowOutcome.Cancelled, ExitCodes.SelectionRequired), (result.Outcome, result.ExitCode));
        scenario.AssertNothingChanged();
        // Before: "Different printer at this address. The printer at this address is not a supported ticket printer. Check the
        // address." under "Setup was stopped", and Try again repeated the same refusal.
        Assert.Equal(t.Text("finding.NetworkIdentityMismatch.modelRefused.title") + ". "
            + t.Format("finding.NetworkIdentityMismatch.modelRefused.text", SampleConfig.P4531, Address, SampleConfig.P4030), WindowRun.Notes(view)[0]);
        Assert.DoesNotContain(t.FindingText(FindingCode.NetworkIdentityMismatch), labels);
        Assert.Contains(t.Text("outcome.Cancelled.modelRefused.title"), labels);
        Assert.Contains(t.Text("outcome.Cancelled.modelRefused.text"), labels);
        Assert.DoesNotContain(t.OutcomeTitle(FlowOutcome.Cancelled, mode), labels);
        Assert.False(view.CanTryAgain);
        Assert.False(view.CanRunSetup);
        Assert.Equal(t.Text("result.close"), ((Button)form.AcceptButton!).Text);
        // The printer was found and identified; the step does not say the detection was skipped.
        Assert.Equal(t.StepTitle(StepId.Detect) + ": " + t.Text("step.Detect.modelRefused"), form.Steps.Describe(StepId.Detect));
        if (language == "en")
        {
            Assert.Equal("Model does not match this printer. The model P-4531DN was given for the printer at 10.20.30.40, but the printer reports P-4030DN. "
                + "Check the address and the model on the printer's label, then start the program again with the correct ones, or contact IT.", WindowRun.Notes(view)[0]);
            Assert.Contains("The model given for this printer does not match the model it reports, so nothing was changed.", labels);
            Assert.Equal("Detecting printer: Printer found; the model given does not match", form.Steps.Describe(StepId.Detect));
        }
        else
        {
            Assert.Equal("البحث عن الطابعة: تم العثور على الطابعة؛ الطراز المحدَّد لا يطابقها", form.Steps.Describe(StepId.Detect));
        }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_refused_model_whose_printer_names_no_model_of_its_own_says_another_model(string language) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create(language));
        Strings t = Strings.For(form.Language);
        var refused = new Finding(FindingCode.NetworkIdentityMismatch, Severity.Error, "The model P-4531DN was given for the printer at 10.20.30.40 (--model with --confirm-model), but the printer reports another model; nothing was changed.")
        {
            Data = new Dictionary<string, string> { ["target"] = PrinterCandidate.NetworkKey(Address), ["address"] = Address, ["assertedModel"] = SampleConfig.P4531 },
        };

        form.ShowResult(new FlowResult(FlowOutcome.Cancelled) { Findings = [refused], Report = UiSamples.Report(SetupMode.Setup), ExitCode = ExitCodes.SelectionRequired });

        Assert.Equal(t.Text("finding.NetworkIdentityMismatch.modelRefused.title") + ". " + t.Format("finding.NetworkIdentityMismatch.modelRefused.otherModel", SampleConfig.P4531, Address),
            WindowRun.Notes(form.CurrentView!)[0]);
        Assert.False(((ResultView)form.CurrentView!).CanTryAgain);
    });

    [Fact]
    public void A_model_the_operator_chose_that_the_printer_contradicts_keeps_its_own_text() => Sta.Run(() =>
    {
        // Only IT's model (assertedModel) gets the refusal's text; the employee's choice was a question answered in the window.
        using var form = new MainForm(TestRuntime.Create("en"));
        var mismatch = new Finding(FindingCode.NetworkIdentityMismatch, Severity.Error, "The device at 10.0.0.9 reports an unsupported model.")
        {
            Data = new Dictionary<string, string> { ["address"] = "10.0.0.9" },
        };

        form.ShowResult(new FlowResult(FlowOutcome.NoPrinter) { Findings = [mismatch], Report = UiSamples.Report(SetupMode.Setup) });

        Assert.Equal("Different printer at this address. The printer at this address is not a supported ticket printer. Check the address.", WindowRun.Notes(form.CurrentView!)[0]);
        Assert.True(((ResultView)form.CurrentView!).CanTryAgain);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Repair_whose_choice_was_declined_speaks_of_repair(string language) => Sta.Run(() =>
    {
        // The earlier ticket printer is unplugged and its queue alone has the ticket paper; a new unit of the same model is
        // connected. Repair asks which one is the ticket printer; the employee chooses none.
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", present: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01"))
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030, "USB001", name: "Old ticket printer")
            .WithTicketForm()
            .WithCorrectDefaults("Old ticket printer")
            .WithUsbPrinter(SampleConfig.P4030, "USB002")
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: "TA P-4030DN KX (Copy 1)")
            .Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Repair, language: language);
        Strings t = Strings.For(form.Language);

        Task run = form.RunFlowAsync(SetupMode.Repair);
        WindowRun.PumpUntil(() => form.CurrentView is ChoosePrinterView || run.IsCompleted);
        WindowRun.Click(Assert.IsType<ChoosePrinterView>(form.CurrentView), t.Text("ui.cancel"));
        WindowRun.PumpUntil(() => run.IsCompleted);
        FlowResult result = WindowRun.ResultOf(form)!;
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        scenario.AssertNothingChanged();
        // Before: "Setup was stopped", and the headline "Several supported printers found ... so each one was checked", although
        // Repair checked none.
        Assert.Contains(t.Text("outcome.Cancelled.title.Repair"), labels);
        Assert.Contains(t.Text("outcome.Cancelled.text.Repair"), labels);
        Assert.DoesNotContain(t.OutcomeTitle(FlowOutcome.Cancelled), labels);
        Assert.Equal(t.FindingTitle(FindingCode.Cancelled) + ". " + t.Text("finding.Cancelled.text.Repair"), WindowRun.Notes(view)[0]);
        Assert.DoesNotContain(WindowRun.Notes(view), n => n.StartsWith(t.FindingTitle(FindingCode.MultipleSupportedPrinters), StringComparison.Ordinal));
        Assert.True(view.CanTryAgain);
        if (language == "en")
        {
            Assert.Contains("Repair was stopped", labels);
            Assert.Contains("Nothing else was changed. You can run Repair again at any time.", labels);
            Assert.Equal("Cancelled. Repair was stopped before it finished.", WindowRun.Notes(view)[0]);
        }
    });

    public static TheoryData<FlowOutcome, string, string> RepairTitles => new()
    {
        { FlowOutcome.Cancelled, "Repair was stopped", "Setup was stopped" },
        { FlowOutcome.Blocked, "Repair cannot continue", "Setup cannot continue" },
        { FlowOutcome.Failed, "Repair did not finish", "Setup did not finish" },
    };

    [Theory]
    [MemberData(nameof(RepairTitles))]
    public void Repair_titles_its_ends_as_repair_and_setup_keeps_its_own(FlowOutcome outcome, string repair, string setup) => Sta.Run(() =>
    {
        var result = new FlowResult(outcome) { Findings = [new Finding(FindingCode.OperationFailed, Severity.Error, "detail")], Report = UiSamples.Report(SetupMode.Repair) };

        using var repairWindow = new MainForm(TestRuntime.Create("en", SetupMode.Repair));
        repairWindow.ShowResult(result);
        using var arabicRepairWindow = new MainForm(TestRuntime.Create("ar", SetupMode.Repair));
        arabicRepairWindow.ShowResult(result);
        using var setupWindow = new MainForm(TestRuntime.Create("en", SetupMode.Setup));
        setupWindow.ShowResult(result);

        Assert.Contains(repair, WindowRun.Labels(repairWindow.CurrentView!));
        Assert.Contains(Strings.Arabic.OutcomeTitle(outcome, SetupMode.Repair), WindowRun.Labels(arabicRepairWindow.CurrentView!));
        Assert.NotEqual(Strings.Arabic.OutcomeTitle(outcome), Strings.Arabic.OutcomeTitle(outcome, SetupMode.Repair));
        Assert.Contains(setup, WindowRun.Labels(setupWindow.CurrentView!));
    });
}
