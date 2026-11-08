using System.Reflection;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// The window's part of a run: the Repair buttons repair the printer a result is about (never every supported printer), the
/// choice between printers says which USB printer is connected, and stopping says what it waits for.
/// </summary>
public sealed class RunFlowWindowTests
{
    [Fact]
    public void The_repair_target_is_the_printer_the_result_is_about()
    {
        TargetSelector? selector = MainForm.RepairTargetOf(UiSamples.Ready());

        // Only the printer is pinned, not the model it had: Repair goes by what the printer reports when it runs.
        Assert.Equal(new TargetSelector(UiSamples.UsbPrinter.Key), selector);
        var driverOnly = UiSamples.Ready() with { Target = UiSamples.NetworkPrinter with { Confidence = MatchConfidence.DriverOnly } };
        Assert.Equal(new TargetSelector(UiSamples.NetworkPrinter.Key), MainForm.RepairTargetOf(driverOnly));
        Assert.Null(MainForm.RepairTargetOf(new FlowResult(FlowOutcome.Failed)));
    }

    [Fact]
    public void A_model_asserted_by_the_operator_stays_pinned_for_repair()
    {
        var asserted = UiSamples.Ready() with { Target = UiSamples.UnidentifiedNetworkPrinter with { Model = TestRuntime.Config.Models[2], Confidence = MatchConfidence.UserAsserted } };

        Assert.Equal(new TargetSelector(UiSamples.UnidentifiedNetworkPrinter.Key, "P-5032DN", ModelAssertedByUser: true), MainForm.RepairTargetOf(asserted));
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public void Waiting_for_another_run_says_so_instead_of_waiting_for_windows(UiLanguage language) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create());
        var t = Strings.For(language);
        using var view = new ProgressView(new ViewContext(form, t, TestRuntime.Config), SetupMode.Setup);

        view.ShowProgress(Amwag.PrinterSetup.App.Elevation.WorkerHost.WaitingForApplyLock(new TargetSelector(UiSamples.UsbPrinter.Key)));
        Assert.Equal(t.Text("progress.waitingForOtherRun"), view.Texts.Activity);
        Assert.Contains("AMWAG Printer Setup", view.Texts.Activity, StringComparison.Ordinal);

        // A wait inside an action (Windows creating the Plug and Play queue) keeps its own text.
        view.ShowProgress(new ProgressUpdate(StepId.InstallDriver, StepState.Running, ActionKind.BindUsbDevice, ProgressPhase.Waiting, "Waiting for Windows to create the printer queue."));
        Assert.Equal(t.Text("phase.Waiting"), view.Texts.Activity);
    });

    [Fact]
    public void The_result_repair_button_repairs_the_printer_of_the_result() => Sta.Run(() =>
    {
        var log = new MemoryLog();
        using var form = new MainForm(TestRuntime.Create("en", log: log));
        _ = form.Handle;
        form.ShowResult(UiSamples.Ready());

        Click(FindButton(form, "Repair")!);
        PumpUntil(() => log.Entries.Any(e => e.Event == "ui.run.finished"));

        AssertRepairOf(log, UiSamples.UsbPrinter.Key);
    });

    [Fact]
    public void The_test_print_repair_button_repairs_the_tested_printer() => Sta.Run(() =>
    {
        var log = new MemoryLog();
        using var form = new MainForm(TestRuntime.Create("en", log: log));
        _ = form.Handle;
        form.StartTestPrint(UiSamples.Ready());
        var testPrint = Assert.IsType<TestPrintView>(form.CurrentView);
        testPrint.ShowAnswer(false);

        Click((Button)form.AcceptButton!);
        PumpUntil(() => log.Entries.Any(e => e.Event == "ui.run.finished"));

        AssertRepairOf(log, UiSamples.UsbPrinter.Key);
    });

    [Theory]
    [InlineData(UiLanguage.English, "Status", "Connected", "Not connected", "The printer reported its exact model", "Known from an earlier connection")]
    [InlineData(UiLanguage.Arabic, "الحالة", "موصولة", "غير موصولة", "أبلغت الطابعة عن طرازها بدقة", "معروفة من اتصال سابق")]
    public void A_usb_printer_choice_says_whether_it_is_connected_now(UiLanguage language, string status, string connected, string notConnected, string exact, string earlier) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create());
        var context = new ViewContext(form, Strings.For(language), TestRuntime.Config);
        var t = context.Strings;
        var earlierUnit = UiSamples.UsbPrinter with { Device = UiSamples.UsbPrinter.Device! with { IsPresent = false }, PhysicallyAvailable = false };

        using CardPanel present = PrinterCard.Create(context, context.Scale, UiSamples.UsbPrinter, PrinterCardDetail.Choice, selectable: true);
        using CardPanel absent = PrinterCard.Create(context, context.Scale, earlierUnit, PrinterCardDetail.Choice, selectable: true);
        using CardPanel network = PrinterCard.Create(context, context.Scale, UiSamples.NetworkPrinter, PrinterCardDetail.Choice, selectable: true);
        using CardPanel progress = PrinterCard.Create(context, context.Scale, earlierUnit, PrinterCardDetail.Progress);

        Assert.Contains($"{status}: {connected}", present.AccessibleName, StringComparison.Ordinal);
        Assert.Contains($"{t.Text("card.identifiedBy")}: {exact}", present.AccessibleName, StringComparison.Ordinal);
        Assert.Contains($"{status}: {notConnected}", absent.AccessibleName, StringComparison.Ordinal);
        Assert.Contains($"{t.Text("card.identifiedBy")}: {earlier}", absent.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain(exact, absent.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain($"{status}:", network.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain($"{status}:", progress.AccessibleName, StringComparison.Ordinal);
    });

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public void Stopping_says_that_a_running_administrator_step_finishes_first(UiLanguage language) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create());
        var t = Strings.For(language);
        using var view = new ProgressView(new ViewContext(form, t, TestRuntime.Config), SetupMode.Setup);
        view.ShowElevationNote(true);
        Assert.Equal(t.Text("progress.elevation"), view.Texts.Note);

        view.ShowCancelling();
        // Progress that still arrives while stopping (the read-back) keeps the stopping texts.
        view.ShowProgress(new ProgressUpdate(StepId.Verify, StepState.Running, null, ProgressPhase.Started, "Reading back the printer settings"));

        Assert.Equal(t.Text("progress.cancelling"), view.Texts.Activity);
        Assert.Equal(t.Text("progress.cancelling.note"), view.Texts.Note);
    });

    [Fact]
    public void The_close_prompt_and_the_stopping_texts_say_what_stopping_does()
    {
        foreach (string key in new[] { "close.text", "progress.cancelling.note" })
        {
            string english = Strings.English.Text(key);
            Assert.Contains("administrator rights finishes first", english, StringComparison.Ordinal);
            Assert.Contains("later steps are not started", english, StringComparison.Ordinal);
            Assert.Contains("window closes when the setup has stopped", english, StringComparison.Ordinal);
            Assert.Contains("بصلاحيات المسؤول", Strings.Arabic.Text(key), StringComparison.Ordinal);
        }
    }

    private static void AssertRepairOf(MemoryLog log, string targetKey)
    {
        var started = log.Entries.Single(e => e.Event == "ui.run.started");
        Assert.Equal(nameof(SetupMode.Repair), started.Data?["mode"]);
        Assert.Equal(targetKey, started.Data?["repairTarget"]);
        Assert.Equal(targetKey, log.Entries.Single(e => e.Event == "flow.start").Data?["repairTarget"]);
    }

    private static void Click(Button button) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);

    private static ActionButton? FindButton(Control root, string text) =>
        root.Controls.Cast<Control>().Select(c => c is ActionButton { } b && b.Text == text ? b : FindButton(c, text)).FirstOrDefault(b => b is not null);

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the UI thread.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
