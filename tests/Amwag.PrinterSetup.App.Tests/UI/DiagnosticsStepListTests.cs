using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// Diagnostics checks every printer, but its step list (which screen readers read too) ends on the ticket printer, as the banner
/// and the Ready step do: never on an office printer that happens to be checked last. Without a single ticket printer it says so.
/// </summary>
public sealed class DiagnosticsStepListTests
{
    private const string Ticket = "10.0.0.5";

    public static TheoryData<bool, bool, string> Printers => new()
    {
        { true, false, "en" },
        { true, false, "ar" },
        { false, true, "en" },
    };

    [Theory]
    [MemberData(nameof(Printers))]
    public void The_step_list_ends_on_the_ticket_printer_not_on_the_office_printer_checked_last(bool ticketReady, bool officeReady, string language) => Sta.Run(() =>
    {
        // The ticket printer is the network P-4030DN at branch.json's printerAddress; an office USB P-4531DN is set up as well.
        // "net:" sorts before "usb:", so the office printer is checked last.
        Scenario scenario = TicketOnNetworkOfficeOnUsb(ticketReady, officeReady);
        using MainForm form = WindowRun.Create(scenario, SetupMode.Diagnostics, language: language);
        Strings t = Strings.For(form.Language);

        FlowResult result = WindowRun.Run(form, SetupMode.Diagnostics);

        scenario.AssertNothingChanged();
        Assert.Equal("Tickets", result.Verification!.Summary.QueueName);
        Assert.Equal("Office", result.Verifications[^1].Summary.QueueName);
        Assert.Equal(ticketReady ? FlowOutcome.Ready : FlowOutcome.NotReady, result.Outcome);
        // Before: the office printer's read-back ("Some settings are wrong" next to "Ready to print tickets", and the reverse).
        StepState verified = ticketReady ? StepState.Succeeded : StepState.Failed;
        Assert.Equal(verified, form.Steps.StateOf(StepId.Verify));
        Assert.Equal(t.StepTitle(StepId.Verify) + ": " + t.StepStatus(StepId.Verify, verified), form.Steps.Describe(StepId.Verify));
        Assert.Equal(t.StepTitle(StepId.CheckDriver) + ": " + t.StepStatus(StepId.CheckDriver, StepState.Succeeded), form.Steps.Describe(StepId.CheckDriver));
        Assert.Equal(ticketReady ? StepState.Succeeded : StepState.Failed, form.Steps.StateOf(StepId.Ready));
        if (language == "en")
            Assert.Equal(ticketReady ? "Verification: All settings verified" : "Verification: Some settings are wrong", form.Steps.Describe(StepId.Verify));
        else
            Assert.Equal("التحقق: تم التحقق من جميع الإعدادات", form.Steps.Describe(StepId.Verify));
    });

    [Theory]
    [InlineData(StepState.Warning, FindingCode.MultipleSupportedPrinters, "Checked; ticket printer not known", "تم الفحص؛ طابعة التذاكر غير معروفة")]
    [InlineData(StepState.Skipped, FindingCode.ProbableMatchRequiresConfirmation, "Not checked", "لم يتم الفحص")]
    [InlineData(StepState.Skipped, FindingCode.NoSupportedPrinterFound, "Not checked", "لم يتم الفحص")]
    public void Without_a_single_ticket_printer_the_driver_check_and_the_verification_say_so(StepState state, FindingCode code, string english, string arabic) => Sta.Run(() =>
    {
        using var steps = new StepList(Strings.English);
        foreach (StepId step in new[] { StepId.CheckDriver, StepId.Verify })
        {
            // The printer checked last first ("All settings verified"), then the flow's final word about the ticket printer.
            steps.SetState(step, StepState.Succeeded);
            var final = new ProgressUpdate(step, state, null, ProgressPhase.Completed, "") { Code = code };
            steps.SetState(step, state, MainForm.StatusKeyFor(final, SetupMode.Diagnostics, actionsBegun: false));
        }

        Assert.Equal("Checking driver: " + english, steps.Describe(StepId.CheckDriver));
        Assert.Equal("Verification: " + english, steps.Describe(StepId.Verify));
        Assert.Equal(state, steps.StateOf(StepId.Verify));
        steps.SetStrings(Strings.Arabic);
        Assert.Equal(Strings.Arabic.StepTitle(StepId.Verify) + ": " + arabic, steps.Describe(StepId.Verify));
    });

    private static Scenario TicketOnNetworkOfficeOnUsb(bool ticketReady, bool officeReady)
    {
        var builder = new ScenarioBuilder()
            .WithBranch(new BranchOverride { PrinterAddress = Ticket })
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue(Ticket, SampleConfig.P4030, name: "Tickets")
            .WithNetworkPrinter(Ticket, SampleConfig.P4030)
            .WithTicketForm()
            .WithUsbPrinter(SampleConfig.P4531, "USB002", instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4531, "OFFICE0001"))
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB002", name: "Office");
        if (ticketReady)
            builder.WithCorrectDefaults("Tickets");
        if (officeReady)
            builder.WithCorrectDefaults("Office");
        return builder.Build();
    }
}
