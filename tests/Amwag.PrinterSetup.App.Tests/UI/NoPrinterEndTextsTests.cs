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
/// Runs that end without a printer to set up or repair although printers were found never say "No ticket printer found ... connect
/// the printer": Repair's printers without a queue lead to Setup, a model nobody confirmed is asked again with Try again, nothing
/// usable at the configured address names that address, and a queue the setup leaves unchanged there is IT's to change (no Run
/// Setup, which would end the same way). Repair no longer asks to confirm a printer it cannot change, so Setup asks once.
/// </summary>
public sealed class NoPrinterEndTextsTests
{
    private const string Pooled = "10.0.0.5";
    private const string Probable = "10.0.0.70";

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Repair_whose_model_question_was_answered_no_says_so_and_asks_again(string language) => Sta.Run(() =>
    {
        // A network P-4030DN that reports only a descriptive identity, with a class-driver queue "Tickets".
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.7", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(new NetworkIdentity { Address = "10.0.0.7", RawPortReachable = true, SnmpResponded = true, SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer" })
            .Build();
        var log = new MemoryLog();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Repair, language: language, log: log);
        Strings t = Strings.For(form.Language);

        Task run = form.RunFlowAsync(SetupMode.Repair);
        WindowRun.PumpUntil(() => run.IsCompleted || form.CurrentView is ConfirmModelView);
        WindowRun.Click(Assert.IsType<ConfirmModelView>(form.CurrentView), t.Text("ui.no"));
        WindowRun.PumpUntil(() => run.IsCompleted);
        FlowResult repair = WindowRun.ResultOf(form)!;
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        scenario.AssertNothingChanged();
        Assert.Equal((FlowOutcome.NoPrinter, ExitCodes.NoSupportedPrinter), (repair.Outcome, repair.ExitCode));
        // Before: "No ticket printer found / ... Connect the printer with its USB cable ... then choose Run Setup", "Detecting
        // printer: No printer found", and only Run Setup, although the printer was found and has a queue.
        Assert.Contains(t.Text("outcome.NoPrinter.modelNotConfirmed.title"), labels);
        Assert.Contains(t.Text("outcome.NoPrinter.modelNotConfirmed.text"), labels);
        Assert.DoesNotContain(t.OutcomeTitle(FlowOutcome.NoPrinter), labels);
        Assert.DoesNotContain(t.Text("outcome.NoPrinter.runSetup"), labels);
        Assert.Equal(t.Text("finding.NoSupportedPrinterFound.modelNotConfirmed.title") + ". " + t.Text("finding.NoSupportedPrinterFound.modelNotConfirmed.text"), WindowRun.Notes(view)[0]);
        Assert.Equal(t.StepTitle(StepId.Detect) + ": " + t.Text("step.Detect.modelNotConfirmed"), form.Steps.Describe(StepId.Detect));
        Assert.True(view.CanTryAgain);
        Assert.False(view.CanRunSetup);
        Assert.Equal(t.Text("result.tryAgain"), ((Button)form.AcceptButton!).Text);
        if (language == "en")
        {
            Assert.Contains("The printer was not changed", labels);
            Assert.Contains("Its model was not confirmed, so nothing was changed.", labels);
            Assert.Equal("Detecting printer: Printer found; model not confirmed", form.Steps.Describe(StepId.Detect));
        }
        else
        {
            Assert.Equal("البحث عن الطابعة: تم العثور على الطابعة؛ لم يتم تأكيد طرازها", form.Steps.Describe(StepId.Detect));
        }

        // Try again repeats Repair, which asks the model again.
        WindowRun.Click(view, t.Text("result.tryAgain"));
        WindowRun.PumpUntil(() => form.CurrentView is ConfirmModelView || (form.CurrentView is ResultView shown && shown != view && WindowRun.ResultOf(form) is not null));
        WindowRun.Click(Assert.IsType<ConfirmModelView>(form.CurrentView), t.Text("ui.no"));
        WindowRun.PumpUntil(() => form.CurrentView is ResultView shown && shown != view && WindowRun.ResultOf(form) is not null);
        Assert.Equal(["Repair", "Repair"], log.Entries.Where(e => e.Event == "ui.run.started").Select(e => e.Data?["mode"]?.ToString()).ToList());
    });

    [Fact]
    public void Repair_next_to_printers_none_of_which_was_set_up_says_so_and_runs_setup() => Sta.Run(() =>
    {
        Scenario scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", driverBound: false)
            .WithUsbPrinter(SampleConfig.P4531, "USB002", driverBound: false, instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4531, "SECOND0001"))
            .Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Repair);
        Strings t = Strings.For(form.Language);

        FlowResult repair = WindowRun.Run(form, SetupMode.Repair);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.NoPrinter, repair.Outcome);
        // Before: "No ticket printer found ... Connect the printer", and "Detecting printer: No printer found", although both
        // printers are connected and were found.
        Assert.Contains("Nothing to repair yet", labels);
        Assert.Contains("None of the printers found has been added to Windows yet, and Repair changes only a printer that is already set up, so nothing was changed.", labels);
        Assert.DoesNotContain(t.Text("outcome.NoPrinter.runSetup"), labels);
        Assert.Equal("Printers not set up yet. Supported printers were found, but none of them has been added to Windows yet, so Repair cannot change them. "
            + "Choose Run Setup to set up the ticket printer.", WindowRun.Notes(view)[0]);
        Assert.Equal("Detecting printer: Printers found, none set up yet", form.Steps.Describe(StepId.Detect));
        Assert.True(view.CanRunSetup);
        Assert.False(view.CanTryAgain);
        form.SwitchLanguage();
        Assert.Contains(Strings.Arabic.Text("outcome.NoPrinter.printersWithoutQueue.text"), WindowRun.Labels(form.CurrentView!));
        Assert.Equal(Strings.Arabic.StepTitle(StepId.Detect) + ": " + Strings.Arabic.Text("step.Detect.noneSetUp"), form.Steps.Describe(StepId.Detect));

        // Run Setup offers the choice between the two printers.
        WindowRun.Click(form.CurrentView!, Strings.Arabic.Text("result.runSetup"));
        WindowRun.PumpUntil(() => form.CurrentView is ChoosePrinterView or NetworkAddressView or ConfirmModelView);
        WindowRun.Click(Assert.IsType<ChoosePrinterView>(form.CurrentView), Strings.Arabic.Text("ui.cancel"));
        WindowRun.PumpUntil(() => form.CurrentView is NetworkAddressView || WindowRun.ResultOf(form) is not null);
        if (form.CurrentView is NetworkAddressView)
            WindowRun.Click(form.CurrentView, Strings.Arabic.Text("ui.cancel"));
        WindowRun.PumpUntil(() => WindowRun.ResultOf(form) is not null);
        scenario.AssertNothingChanged();
    });

    [Fact]
    public void Repair_with_nothing_at_the_configured_address_names_it_and_offers_no_run_setup() => Sta.Run(() =>
    {
        // branch.json names 10.0.0.99, where nothing answers; an office USB printer is set up, and Repair does not change it.
        Scenario scenario = new ScenarioBuilder()
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.99" })
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Repair);
        Strings t = Strings.For(form.Language);

        FlowResult repair = WindowRun.Run(form, SetupMode.Repair);
        var view = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(view);

        scenario.AssertNothingChanged();
        Assert.Equal(FlowOutcome.NoPrinter, repair.Outcome);
        // Before: "Connect the printer with its USB cable ... then choose Run Setup", and Run Setup asked for the address again.
        Assert.Contains("Ticket printer not found at its address", labels);
        Assert.Contains("Nothing was changed, and no other printer was used in its place. Solve the problem shown below, then try again.", labels);
        Assert.Equal(t.Text("finding.NoSupportedPrinterFound.notAtAddress.title") + ". " + t.Format("finding.NoSupportedPrinterFound.notAtAddress.text", "10.0.0.99"), WindowRun.Notes(view)[0]);
        Assert.False(view.CanRunSetup);
        Assert.True(view.CanTryAgain);
        Assert.Equal("Detecting printer: No printer found", form.Steps.Describe(StepId.Detect));
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_queue_left_unchanged_at_the_configured_address_is_for_IT_to_change_in_every_mode(string language) => Sta.Run(() =>
    {
        // branch.json names 10.0.0.5; queue "Tickets" is pooled over IP_10.0.0.5 and IP_10.0.0.6, which the setup never configures.
        string driver = SampleConfig.DriverName(SampleConfig.P4030);
        Scenario scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithTicketForm()
            .WithTcpIpPort(Pooled)
            .WithTcpIpPort("10.0.0.6")
            .WithNetworkPrinter(Pooled, SampleConfig.P4030)
            .WithNetworkPrinter("10.0.0.6", SampleConfig.P4030)
            .WithBranch(new BranchOverride { PrinterAddress = Pooled })
            .WithQueue("Tickets", driver, "IP_10.0.0.5,IP_10.0.0.6")
            .WithCorrectDefaults("Tickets")
            .Build();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Setup, language: language);
        Strings t = Strings.For(form.Language);
        string headline = t.FindingTitle(FindingCode.QueueLeftUnchanged) + ". " + t.Format("finding.NoSupportedPrinterFound.leftUnchanged.text", "Tickets", Pooled);

        // Setup asks for the printer's address (its prompt names the queue left unchanged); the employee cancels.
        Task run = form.RunFlowAsync(SetupMode.Setup);
        WindowRun.PumpUntil(() => run.IsCompleted || form.CurrentView is NetworkAddressView);
        WindowRun.Click(Assert.IsType<NetworkAddressView>(form.CurrentView), t.Text("ui.cancel"));
        WindowRun.PumpUntil(() => run.IsCompleted);
        var setup = Assert.IsType<ResultView>(form.CurrentView);
        List<string> labels = WindowRun.Labels(setup);
        Assert.Equal(FlowOutcome.NoPrinter, WindowRun.ResultOf(form)!.Outcome);
        // Before: "No ticket printer found / Connect the printer with its USB cable or make sure it is on the network", and the
        // headline "Check that the printer is switched on and connected"; the pooled queue was named only in the English details.
        Assert.Contains(t.Text("outcome.leftUnchanged.title"), labels);
        Assert.Contains(t.Text("outcome.leftUnchanged.text"), labels);
        Assert.DoesNotContain(t.OutcomeText(FlowOutcome.NoPrinter), labels);
        Assert.Equal(headline, WindowRun.Notes(setup)[0]);
        Assert.Equal(t.StepTitle(StepId.Detect) + ": " + t.Text("step.Detect.leftUnchanged"), form.Steps.Describe(StepId.Detect));
        Assert.True(setup.CanTryAgain);
        Assert.False(setup.CanRunSetup);

        // Repair ends the same way and does not send the employee to Setup, which would end so again.
        FlowResult repair = WindowRun.Run(form, SetupMode.Repair);
        var repairView = Assert.IsType<ResultView>(form.CurrentView);
        Assert.Equal(FlowOutcome.NoPrinter, repair.Outcome);
        Assert.Contains(t.Text("outcome.leftUnchanged.text"), WindowRun.Labels(repairView));
        Assert.Equal(headline, WindowRun.Notes(repairView)[0]);
        Assert.False(repairView.CanRunSetup);
        Assert.True(repairView.CanTryAgain);

        // Diagnostics, which changes nothing, says the same without "nothing was changed".
        FlowResult diagnostics = WindowRun.Run(form, SetupMode.Diagnostics);
        var diagnosticsView = Assert.IsType<ResultView>(form.CurrentView);
        Assert.Contains(t.Text("outcome.leftUnchanged.title"), WindowRun.Labels(diagnosticsView));
        Assert.Contains(t.Text("outcome.leftUnchanged.text", SetupMode.Diagnostics), WindowRun.Labels(diagnosticsView));
        Assert.Equal(headline, WindowRun.Notes(diagnosticsView)[0]);
        Assert.False(diagnosticsView.CanRepair);
        Assert.Equal(FlowOutcome.NoPrinter, diagnostics.Outcome);
        scenario.AssertNothingChanged();

        if (language == "en")
        {
            Assert.Equal("Printer entry left unchanged. The printer entry Tickets prints to the printer at 10.0.0.5 in a way the setup cannot configure (see the "
                + "technical details below), so that printer is not set up automatically. Ask IT to change that printer entry or delete it, then run the setup again.", headline);
            Assert.Contains("The ticket printer cannot be set up automatically", labels);
            Assert.Equal("Detecting printer: Printer not set up automatically", form.Steps.Describe(StepId.Detect));
        }
        else
        {
            Assert.Contains("لا يمكن إعداد طابعة التذاكر تلقائيًا", labels);
        }
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Repair_of_a_printer_to_confirm_without_a_queue_leaves_the_question_to_setup_which_asks_once(string language) => Sta.Run(() =>
    {
        // branch.json names 10.0.0.70: a P-4030DN that reports only a descriptive identity (its model must be confirmed), no queue.
        Scenario scenario = new ScenarioBuilder()
            .WithBranch(new BranchOverride { PrinterAddress = Probable })
            .WithNetworkIdentity(new NetworkIdentity { Address = Probable, ResolvedAddress = Probable, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA Triumph-Adler P-4030DN Series Network Printer" })
            .Build();
        var log = new MemoryLog();
        using MainForm form = WindowRun.Create(scenario, SetupMode.Repair, language: language, log: log);
        Strings t = Strings.For(form.Language);

        // Before: Repair asked the model, found nothing to repair, and Setup asked the same question again.
        FlowResult repair = WindowRun.Run(form, SetupMode.Repair);
        var repairView = Assert.IsType<ResultView>(form.CurrentView);
        Assert.Equal(FlowOutcome.NoPrinter, repair.Outcome);
        Assert.DoesNotContain(log.Entries, e => e.Event == "ui.confirm_model.answered");
        Assert.Contains(t.Text("outcome.NoPrinter.needsSetup.title"), WindowRun.Labels(repairView));
        Assert.Equal(t.StepTitle(StepId.Detect) + ": " + t.Text("step.Detect.notSetUp"), form.Steps.Describe(StepId.Detect));
        Assert.True(repairView.CanRunSetup);
        scenario.AssertNothingChanged();

        WindowRun.Click(repairView, t.Text("result.runSetup"));
        WindowRun.PumpUntil(() => form.CurrentView is ConfirmModelView or NetworkAddressView or ChoosePrinterView || (form.CurrentView is ResultView shown && shown != repairView));
        WindowRun.Click(Assert.IsType<ConfirmModelView>(form.CurrentView), t.Text("ui.yes"));
        WindowRun.PumpUntil(() => form.CurrentView is ResultView shown && shown != repairView && WindowRun.ResultOf(form) is not null);
        FlowResult setup = WindowRun.ResultOf(form)!;

        Assert.Equal(1, log.Entries.Count(e => e.Event == "ui.confirm_model.answered"));
        Assert.Equal(["Repair", "Setup"], log.Entries.Where(e => e.Event == "ui.run.started").Select(e => e.Data?["mode"]?.ToString()).ToList());
        Assert.True(setup.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings, setup.Outcome.ToString());
        Assert.NotEmpty(setup.Verification!.Summary.QueueName);
    });

    [Fact]
    public void Each_end_without_a_printer_is_told_apart_by_the_reason_its_finding_gives()
    {
        static NoPrinterReason Of(params (string Key, string Value)[] data) => ResultView.ReasonOf(
            new Finding(FindingCode.NoSupportedPrinterFound, Severity.Error, "detail") { Data = data.ToDictionary(d => d.Key, d => d.Value) });

        Assert.Equal(NoPrinterReason.NoQueue, Of(("reason", "noQueue"), ("target", "usb:X"), ("suggestedMode", "setup")));
        Assert.Equal(NoPrinterReason.ModelNotConfirmed, Of(("reason", "modelNotConfirmed")));
        Assert.Equal(NoPrinterReason.PrintersWithoutQueue, Of(("reason", "printersWithoutQueue"), ("suggestedMode", "setup")));
        Assert.Equal(NoPrinterReason.NoPrinter, Of(("reason", "noPrinter"), ("suggestedMode", "setup")));
        // The configured address, in every mode (Repair also says "address" or "leftUnchanged").
        Assert.Equal(NoPrinterReason.Address, Of(("address", "10.0.0.99")));
        Assert.Equal(NoPrinterReason.Address, Of(("reason", "address"), ("address", "10.0.0.99")));
        Assert.Equal(NoPrinterReason.LeftUnchanged, Of(("address", Pooled), ("leftUnchanged", "Tickets")));
        Assert.Equal(NoPrinterReason.LeftUnchanged, Of(("reason", "leftUnchanged"), ("address", Pooled), ("leftUnchanged", "Tickets")));
        // The read-back's "not found again" gives no reason, nor does a warning.
        Assert.Equal(NoPrinterReason.None, Of());
        Assert.Equal(NoPrinterReason.None, ResultView.ReasonOf(new Finding(FindingCode.NoSupportedPrinterFound, Severity.Warning, "detail") { Data = new Dictionary<string, string> { ["reason"] = "noPrinter" } }));
    }
}
