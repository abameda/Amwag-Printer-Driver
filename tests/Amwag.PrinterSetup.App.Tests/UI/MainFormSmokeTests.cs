using System.Reflection;
using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using FakeDevModes = Amwag.PrinterSetup.Testing.FakeDevModes;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>Builds the window with fake services (never shown, never runs the real flow) and exercises every view in both languages.</summary>
public sealed class MainFormSmokeTests
{
    [Fact]
    public void Window_is_branded_sized_and_starts_in_the_requested_language() => Sta.Run(() =>
    {
        using var english = new MainForm(TestRuntime.Create("en"));
        Assert.Equal("AMWAG Printer Setup", english.Text);
        Assert.Equal(UiLanguage.English, english.Language);
        Assert.Equal(RightToLeft.No, english.RightToLeft);
        Assert.Equal(AutoScaleMode.Dpi, english.AutoScaleMode);
        Assert.Equal(FormStartPosition.CenterScreen, english.StartPosition);
        Assert.True(english.ClientSize.Width >= 840 && english.ClientSize.Height >= 600);

        using var arabic = new MainForm(TestRuntime.Create("ar"));
        Assert.Equal(UiLanguage.Arabic, arabic.Language);
        Assert.Equal(RightToLeft.Yes, arabic.RightToLeft);
        Assert.True(arabic.RightToLeftLayout);
    });

    [Fact]
    public void Language_toggle_switches_and_mirrors_the_window() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        _ = form.Handle;

        form.SwitchLanguage();
        Assert.Equal(UiLanguage.Arabic, form.Language);
        Assert.Equal(RightToLeft.Yes, form.RightToLeft);
        Assert.True(form.RightToLeftLayout);

        form.SwitchLanguage();
        Assert.Equal(UiLanguage.English, form.Language);
        Assert.Equal(RightToLeft.No, form.RightToLeft);
        Assert.False(form.RightToLeftLayout);
    });

    [Fact]
    public void Every_view_survives_language_switches() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        _ = form.Handle;

        void Exercise(Action show, Type expectedView)
        {
            show();
            Assert.IsType(expectedView, form.CurrentView);
            form.SwitchLanguage();
            form.SwitchLanguage();
        }

        Exercise(() =>
        {
            form.ShowResult(UiSamples.Ready());
            form.ApplyUpdate(new FlowUpdate(FlowEventKind.Step, new ProgressUpdate(StepId.Detect, StepState.Running, null, ProgressPhase.Started, "")));
        }, typeof(ResultView));
        Exercise(() => form.AskChoosePrinter([UiSamples.UsbPrinter, UiSamples.NetworkPrinter], _ => true), typeof(ChoosePrinterView));
        Exercise(() => form.AskConfirmModel(UiSamples.NetworkPrinter, _ => true), typeof(ConfirmModelView));
        Exercise(() => form.AskNetworkAddress(new NetworkAddressPrompt(false, "10.0.0.99", FindingCode.NetworkPrinterUnreachable, "timeout"), _ => true), typeof(NetworkAddressView));
        Exercise(() => form.AskModel(UiSamples.UnidentifiedNetworkPrinter, TestRuntime.Config.Models, _ => true), typeof(ChooseModelView));
        Exercise(() => form.ShowResult(UiSamples.ReadyWithWarnings()), typeof(ResultView));
        Exercise(() => form.ShowResult(UiSamples.Failed()), typeof(ResultView));
        Exercise(() => form.ShowResult(UiSamples.ElevationDeclined()), typeof(ResultView));
        Exercise(() => form.ShowResult(new FlowResult(FlowOutcome.Cancelled)), typeof(ResultView));
        Exercise(() => form.StartTestPrint(UiSamples.Ready()), typeof(TestPrintView));

        var testPrint = (TestPrintView)form.CurrentView!;
        testPrint.ShowWorking(new JobObservation(JobStage.Queued, JobProblems.PaperOut | JobProblems.Offline, 3, ""));
        form.SwitchLanguage();
        testPrint.ShowFinished(new TestPrintSubmission("AMWAG Ticket Printer", "AMWAG Test Ticket", DateTimeOffset.Now, null), new JobMonitorResult(JobMonitorOutcome.SentToPrinter, JobProblems.None, 3, ""));
        testPrint.ShowAnswer(false);
        Assert.Equal(TestPrintStage.AnsweredProblem, testPrint.Stage);
        form.SwitchLanguage();

        Exercise(() => StartTicketCheckWithoutWatching(form, UiSamples.Ready()), typeof(TicketCheckView));
        var ticketCheck = (TicketCheckView)form.CurrentView!;
        ticketCheck.ShowWatching(new TicketCheckProgress(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), null));
        foreach (var result in UiSamples.TicketCheckResults())
        {
            ticketCheck.ShowResult(result);
            Assert.Equal(TicketCheckStage.Finished, ticketCheck.Stage);
            form.SwitchLanguage();
        }
        ticketCheck.ShowFailed("InvalidOperationException: test");
        form.SwitchLanguage();
    });

    [Fact]
    public void Check_ticket_software_is_offered_only_when_there_is_a_queue() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));

        form.ShowResult(UiSamples.Ready());
        Assert.True(((ResultView)form.CurrentView!).CanCheckTicketSoftware);
        Assert.NotNull(FindButton(form, "Check ticket software"));

        form.ShowResult(UiSamples.ReadyWithWarnings());
        Assert.True(((ResultView)form.CurrentView!).CanCheckTicketSoftware);

        form.ShowResult(UiSamples.Failed());
        Assert.False(((ResultView)form.CurrentView!).CanCheckTicketSoftware);
    });

    [Fact]
    public void Ticket_check_reads_the_new_job_and_shows_the_comparison() => Sta.Run(() =>
    {
        var log = new MemoryLog();
        var spooler = new StubSpooler();
        var job = new PrintJobInfo { JobId = 41, Document = "Ticket 41", Status = JobStatusFlags.Spooling };
        int polls = 0;
        spooler.Jobs = _ => Interlocked.Increment(ref polls) > 3 ? [job] : [];
        spooler.JobDevModes = (_, id) => id == 41 ? FakeDevModes.Create("AMWAG Ticket Printer", DmPaper.Letter, "Letter", DmOrientation.Landscape) : null;
        using var form = new MainForm(TestRuntime.Create("en", spooler: spooler, log: log));
        _ = form.Handle;

        form.StartTicketCheck(UiSamples.Ready());
        var view = Assert.IsType<TicketCheckView>(form.CurrentView);
        Assert.Equal("Stop", ((Button)form.CancelButton!).Text);
        PumpUntil(() => view.Stage == TicketCheckStage.Finished);
        PumpUntil(() => form.TicketCheckCompletion.IsCompleted);

        Assert.Equal(TicketCheckOutcome.JobCaptured, view.Result?.Outcome);
        Assert.True(view.Result!.HasDifferences);
        Assert.Equal("Check again", ((Button)form.AcceptButton!).Text);
        Assert.Contains(log.Entries, e => e.Event == "ticketcheck.result" && Equals(e.Data?["outcome"], "JobCaptured"));
    });

    [Fact]
    public void Stopping_the_ticket_check_returns_to_the_result_and_ends_the_watch() => Sta.Run(() =>
    {
        var log = new MemoryLog();
        int polls = 0;
        var spooler = new StubSpooler { Jobs = _ => { Interlocked.Increment(ref polls); return []; } };
        using var form = new MainForm(TestRuntime.Create("en", spooler: spooler, log: log));
        _ = form.Handle;

        form.StartTicketCheck(UiSamples.Ready());
        PumpUntil(() => Volatile.Read(ref polls) >= 3);
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form.CancelButton, [EventArgs.Empty]);
        PumpUntil(() => form.CurrentView is ResultView);
        PumpUntil(() => form.TicketCheckCompletion.IsCompleted);

        Assert.Equal("Cancelled", log.Entries.Single(e => e.Event == "ticketcheck.result").Data?["outcome"]);
        int pollsWhenStopped = Volatile.Read(ref polls);
        Thread.Sleep(400);
        Assert.Equal(pollsWhenStopped, Volatile.Read(ref polls));
    });

    [Fact]
    public void Open_log_folder_follows_the_log_file_of_this_run() => Sta.Run(() =>
    {
        string folder = Path.Combine(Path.GetTempPath(), "amwag-ui-tests", "AMWAG", "PrinterSetup", "Logs");
        using var withFile = new MainForm(TestRuntime.Create("en", log: new MemoryLog { FilePath = Path.Combine(folder, "20261103-140500-ui-test.jsonl") }));
        withFile.ShowResult(UiSamples.Ready());
        Assert.True(FindButton(withFile, "Open log folder")!.Enabled);

        using var withoutFile = new MainForm(TestRuntime.Create("en"));
        withoutFile.ShowResult(UiSamples.Ready());
        Assert.False(FindButton(withoutFile, "Open log folder")!.Enabled);
    });

    [Fact]
    public void Result_actions_follow_the_outcome() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));

        form.ShowResult(UiSamples.Ready());
        Assert.Equal("Print Test Ticket", ((Button)form.AcceptButton!).Text);
        Assert.Equal("Close", ((Button)form.CancelButton!).Text);

        form.ShowResult(UiSamples.Failed());
        Assert.Equal("Try again", ((Button)form.AcceptButton!).Text);

        form.ShowResult(UiSamples.ElevationDeclined());
        Assert.Equal("Try again", ((Button)form.AcceptButton!).Text);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_configuration_that_changed_while_the_window_was_open_offers_close_instead_of_try_again(string language) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create(language));
        var t = Strings.For(language == "ar" ? UiLanguage.Arabic : UiLanguage.English);
        Finding changed = WorkerHost.ConfigurationChanged(
            new ConfigurationPin(ConfigurationPin.Embedded, ConfigurationPin.NoBranch),
            new ConfigurationPin(new string('a', 64), ConfigurationPin.NoBranch))!;

        form.ShowResult(new FlowResult(FlowOutcome.Blocked) { Target = UiSamples.UsbPrinter, Findings = [changed], Report = UiSamples.Report(SetupMode.Setup) });

        // Try again would load the same (old) configuration and be refused again; only a restart helps.
        var view = Assert.IsType<ResultView>(form.CurrentView);
        Assert.False(view.CanTryAgain);
        Assert.Equal(t.Text("result.close"), ((Button)form.AcceptButton!).Text);
        string explanation = Assert.Single(Notes(view));
        Assert.Equal(t.FindingTitle(FindingCode.ConfigurationChanged) + ". " + t.FindingText(FindingCode.ConfigurationChanged), explanation);
        Assert.Contains(language == "ar" ? "أغلق البرنامج ثم شغّله مرة أخرى" : "Close the program and start it again", explanation, StringComparison.Ordinal);
    });

    [Fact]
    public void Another_run_holding_the_apply_lock_is_explained_and_can_be_tried_again() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        var notReady = UiSamples.Ready() with
        {
            Outcome = FlowOutcome.NotReady,
            Findings = [MachineApplyLock.BusyFinding()],
        };

        form.ShowResult(notReady);

        var view = Assert.IsType<ResultView>(form.CurrentView);
        Assert.True(view.CanTryAgain);
        Assert.Equal("Try again", ((Button)form.AcceptButton!).Text);
        // Before: "Something went wrong. An unexpected error occurred."
        Assert.Contains("Another setup is running. Another AMWAG Printer Setup run is changing this computer, so nothing was changed. Try again in a few minutes.", Notes(view));
    });

    [Fact]
    public void Final_step_states_match_the_outcome_and_never_claim_unverified_success() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        form.Steps.SetState(StepId.Detect, StepState.Succeeded);
        form.Steps.SetState(StepId.CheckDriver, StepState.Running);

        form.ShowResult(UiSamples.Failed());

        Assert.Equal(StepState.Succeeded, form.Steps.StateOf(StepId.Detect));
        Assert.Equal(StepState.Failed, form.Steps.StateOf(StepId.CheckDriver));
        // A step the run never reached says so; it no longer waits.
        Assert.Equal(StepState.Skipped, form.Steps.StateOf(StepId.InstallDriver));
        Assert.Equal("Installing driver: Not reached", form.Steps.Describe(StepId.InstallDriver));
        Assert.Equal(StepState.Failed, form.Steps.StateOf(StepId.Ready));
    });

    [Fact]
    public void Step_list_exposes_each_step_to_screen_readers() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        form.Steps.SetState(StepId.CheckDriver, StepState.Succeeded);
        var accessible = form.Steps.AccessibilityObject;

        Assert.Equal(AccessibleRole.List, accessible.Role);
        Assert.Equal(6, accessible.GetChildCount());
        Assert.Equal("Checking driver: Driver is ready", accessible.GetChild(1)!.Name);
        Assert.Equal(AccessibleRole.ListItem, accessible.GetChild(1)!.Role);
    });

    [Fact]
    public void Questions_from_a_background_thread_are_answered_on_the_UI_thread_and_can_be_cancelled() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en"));
        _ = form.Handle;
        var interaction = (ISetupInteraction)typeof(MainForm).GetField("_interaction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

        // Answered: the view appears after the UI thread pumps, and its Yes button completes the question.
        var confirmed = Task.Run(() => interaction.ConfirmProbableMatchAsync(UiSamples.NetworkPrinter, CancellationToken.None));
        PumpUntil(() => form.CurrentView is ConfirmModelView);
        var yes = (Button)form.AcceptButton!;
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(yes, [EventArgs.Empty]);
        PumpUntil(() => confirmed.IsCompleted);
        Assert.True(confirmed.Result);

        // Cancelled: the pending question ends as cancelled.
        using var cancellation = new CancellationTokenSource();
        var address = Task.Run(() => interaction.RequestNetworkAddressAsync(new NetworkAddressPrompt(true, null, null, null), cancellation.Token));
        PumpUntil(() => form.CurrentView is NetworkAddressView);
        cancellation.Cancel();
        PumpUntil(() => address.IsCompleted);
        Assert.True(address.IsCanceled);
    });

    private static void StartTicketCheckWithoutWatching(MainForm form, FlowResult result) => UiSamples.StartTicketCheckWithoutWatching(form, result);

    /// <summary>The notes a view shows, as screen readers hear them ("Title. Text").</summary>
    private static List<string> Notes(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => c is NotePanel note ? [note.AccessibleName ?? ""] : Notes(c)).ToList();

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
