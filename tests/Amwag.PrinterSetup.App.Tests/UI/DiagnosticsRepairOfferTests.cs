using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// Diagnostics offers Repair only where Repair can change something: a result about no single printer only when a printer Repair
/// would ask about has a queue of its own, and never on a computer an environment blocker stops. Where it offers none, the banner
/// does not send the employee to Repair, and the step list says what Diagnostics could check (not the printer checked last).
/// </summary>
public sealed class DiagnosticsRepairOfferTests
{
    private const string Nowhere = "10.0.0.99";
    private const string Probable = "10.0.0.70";

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Nothing_at_the_configured_address_next_to_an_office_printer_offers_no_repair(string language) => Sta.Run(() =>
    {
        // branch.json names 10.0.0.99, where nothing answers; an office USB P-4531DN is set up and checked for the report.
        Scenario scenario = new ScenarioBuilder()
            .WithBranch(new BranchOverride { PrinterAddress = Nowhere })
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics, language: language);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, SetupMode.Diagnostics);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Null(result.Target);
        // Before: Repair, under "Some settings could not be confirmed. Run Repair or try again."; Repair then found no printer, and
        // the headline told the employee to check the USB cable of a network printer.
        Assert.False(view.CanRepair);
        Assert.True(view.CanTryAgain);
        Assert.DoesNotContain(t.OutcomeText(FlowOutcome.NotReady), labels);
        Assert.Contains(t.Text("outcome.notAtAddress.title"), labels);
        Assert.Contains(t.Text("outcome.notAtAddress.text", SetupMode.Diagnostics), labels);
        Assert.Equal(t.Text("finding.NoSupportedPrinterFound.notAtAddress.title") + ". " + t.Format("finding.NoSupportedPrinterFound.notAtAddress.text", Nowhere),
            WindowRun.Notes(view)[0]);
        // The office printer, checked last, does not stand in for the ticket printer on the step list ("All settings verified").
        Assert.Equal(t.StepTitle(StepId.CheckDriver) + ": " + t.Text("step.notChecked"), form.Steps.Describe(StepId.CheckDriver));
        Assert.Equal(t.StepTitle(StepId.Verify) + ": " + t.Text("step.notChecked"), form.Steps.Describe(StepId.Verify));
        Assert.Equal(StepState.Skipped, form.Steps.StateOf(StepId.Verify));
        if (language == "en")
        {
            Assert.Contains("Ticket printer not found at its address", labels);
            Assert.Contains("No other printer was taken as the ticket printer in its place. Solve the problem shown below, then try again.", labels);
            Assert.Equal("No ticket printer at this address. No supported ticket printer was found at 10.0.0.99, the address set for the ticket printer: nothing "
                + "answers there, or the device there is not a supported ticket printer. Check the address, and that the printer is switched on and connected to the network.",
                WindowRun.Notes(view)[0]);
            Assert.Equal("Verification: Not checked", form.Steps.Describe(StepId.Verify));
        }
        else
        {
            Assert.Equal("التحقق: لم يتم الفحص", form.Steps.Describe(StepId.Verify));
        }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_printer_to_confirm_that_was_never_set_up_offers_setup_instead_of_repair(string language) => Sta.Run(() =>
    {
        // branch.json names 10.0.0.70: a P-4030DN that reports only a descriptive identity (its model must be confirmed), no queue.
        Scenario scenario = ProbableAt(withQueue: false);
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics, language: language);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, SetupMode.Diagnostics);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.NotReady, result.Outcome);
        Assert.Null(result.Target);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.ProbableMatchRequiresConfirmation, Severity: Severity.Error });
        // Before: Repair, which asked the model and then found nothing to repair, under "Run Repair or try again".
        Assert.False(view.CanRepair);
        Assert.DoesNotContain(t.OutcomeText(FlowOutcome.NotReady), labels);
        Assert.Contains(t.Text("outcome.NotReady.noQueue", SetupMode.Diagnostics), labels);
        Assert.Equal(t.StepTitle(StepId.Verify) + ": " + t.Text("step.notChecked"), form.Steps.Describe(StepId.Verify));
        if (language == "en")
            Assert.Contains("The printer has not been added to Windows yet. Run Setup to set it up.", labels);
    });

    [Fact]
    public void A_printer_to_confirm_that_has_a_queue_still_offers_repair_which_asks_once_and_repairs_it() => Sta.Run(() =>
    {
        // The same printer with a class-driver queue "Tickets": Repair can change that queue once the model is confirmed.
        Scenario scenario = ProbableAt(withQueue: true);
        var log = new MemoryLog();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics, log: log);
        Strings t = Strings.For(form.Language);

        FlowResult diagnostics = WindowRun.Run(form, SetupMode.Diagnostics);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        Assert.Null(diagnostics.Target);
        Assert.True(view.CanRepair);
        Assert.Contains(t.OutcomeText(FlowOutcome.NotReady), WindowRun.Labels(view));

        WindowRun.Click(view, t.Text("result.repair"));
        WindowRun.PumpUntil(() => form.CurrentView is ConfirmModelView || (form.CurrentView is ResultView shown && shown != view && WindowRun.ResultOf(form) is not null));
        WindowRun.Click(Assert.IsType<ConfirmModelView>(form.CurrentView), t.Text("ui.yes"));
        WindowRun.PumpUntil(() => form.CurrentView is ResultView shown && shown != view && WindowRun.ResultOf(form) is not null);

        Assert.Equal(1, log.Entries.Count(e => e.Event == "ui.confirm_model.answered"));
        Assert.Equal("Tickets", WindowRun.ResultOf(form)!.Target?.Queue?.Name);
        Assert.NotEmpty(scenario.Spooler.Writes);
    });

    [Fact]
    public void Two_printers_never_set_up_offer_setup_instead_of_repair_and_say_that_several_were_checked() => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", driverBound: false)
            .WithUsbPrinter(SampleConfig.P4531, "USB002", driverBound: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4531, "SECOND0001"))
            .Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, SetupMode.Diagnostics);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        scenario.AssertNothingChanged();
        Assert.Null(result.Target);
        Assert.Contains(result.Findings, f => f.Code == FindingCode.MultipleSupportedPrinters && f.Data.ContainsKey("choices"));
        // Before: Repair, which then said no ticket printer was found and to connect it.
        Assert.False(view.CanRepair);
        Assert.DoesNotContain(t.OutcomeText(FlowOutcome.NotReady), labels);
        Assert.Contains("The printers found have not been added to Windows yet. Run Setup to set up the ticket printer.", labels);
        // Neither printer is the ticket printer: the list does not show the one checked last.
        Assert.Equal("Checking driver: Checked; ticket printer not known", form.Steps.Describe(StepId.CheckDriver));
        Assert.Equal("Verification: Checked; ticket printer not known", form.Steps.Describe(StepId.Verify));
        form.SwitchLanguage();
        Assert.Equal(Strings.Arabic.StepTitle(StepId.Verify) + ": " + Strings.Arabic.Text("step.ticketUnknown"), form.Steps.Describe(StepId.Verify));
        Assert.Contains(Strings.Arabic.Text("outcome.NotReady.printersWithoutQueue"), WindowRun.Labels(form.CurrentView!));
    });

    [Fact]
    public void A_disabled_print_spooler_offers_no_repair() => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Disabled).Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics);

        FlowResult result = WindowRun.Run(form, SetupMode.Diagnostics);
        var view = Assert.IsType<ResultView>(form.CurrentView);

        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        // Before: Repair, which was blocked the same way ("Repair cannot continue").
        Assert.False(view.CanRepair);
        Assert.True(view.CanTryAgain);
        Assert.Equal("Detecting printer: This computer cannot be set up as it is", form.Steps.Describe(StepId.Detect));
    });

    [Fact]
    public void Protected_print_mode_offers_no_repair_for_the_ticket_printer_either() => Sta.Run(() =>
    {
        // Diagnostics reads the set-up ticket printer, but Repair checks the same blocker first and would stop.
        Scenario scenario = new ScenarioBuilder().WithInstalledUsbPrinter().WithPlatform(p => p with { ProtectedPrintModeEnabled = true }).Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics);

        FlowResult result = WindowRun.Run(form, SetupMode.Diagnostics);
        var view = Assert.IsType<ResultView>(form.CurrentView);

        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.NotNull(result.Target?.Queue);
        Assert.False(view.CanRepair);
        Assert.Equal("Detecting printer: Printer found", form.Steps.Describe(StepId.Detect));
    });

    [Fact]
    public void A_result_about_no_single_printer_offers_repair_only_when_a_printer_to_ask_about_has_a_local_queue() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en", SetupMode.Diagnostics));
        var several = new Finding(FindingCode.MultipleSupportedPrinters, Severity.Warning, "2 supported printers were found and none is known to be the ticket printer.")
        {
            Data = new Dictionary<string, string> { ["choices"] = string.Join(", ", UiSamples.UsbPrinter.Key, UiSamples.NetworkPrinter.Key) },
        };
        FlowResult Among(params PrinterCandidate[] candidates) => new(FlowOutcome.NotReady)
        {
            Findings = [several],
            Report = UiSamples.Report(SetupMode.Diagnostics) with { Candidates = candidates },
        };
        bool CanRepair(FlowResult result)
        {
            form.ShowResult(result);
            return ((ResultView)form.CurrentView!).CanRepair;
        }

        Assert.True(CanRepair(Among(UiSamples.UsbPrinter, UiSamples.NetworkPrinter)));
        Assert.False(CanRepair(Among(UiSamples.UsbPrinter with { Queue = null }, UiSamples.NetworkPrinter)));
        // A connection to a print server is managed there; Repair does not change it.
        var connection = new PrintQueueInfo { Name = @"\\printsrv\Tickets", DriverName = "TA P-4030DN KX", PortName = "", ServerName = @"\\printsrv" };
        Assert.False(CanRepair(Among(UiSamples.UsbPrinter with { Queue = connection })));
        // Without the warning nothing says which printers Repair would ask about: a printer elsewhere does not count.
        Assert.False(CanRepair(Among(UiSamples.UsbPrinter) with { Findings = [] }));
        Assert.Contains(Strings.English.Text("outcome.NotReady.solve"), WindowRun.Labels(form.CurrentView!));
    });

    /// <summary>branch.json's printerAddress is 10.0.0.70: a P-4030DN there reports a descriptive identity only (probable).</summary>
    private static Scenario ProbableAt(bool withQueue)
    {
        var builder = new ScenarioBuilder()
            .WithBranch(new BranchOverride { PrinterAddress = Probable })
            .WithNetworkIdentity(new NetworkIdentity { Address = Probable, ResolvedAddress = Probable, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer" });
        if (withQueue)
        {
            builder.WithModelDriver(SampleConfig.P4030)
                .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
                .WithNetworkQueue(Probable, name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver);
        }
        return builder.Build();
    }
}
