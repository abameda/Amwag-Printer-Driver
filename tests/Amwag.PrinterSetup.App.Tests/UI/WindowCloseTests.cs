using System.Reflection;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// Closing the window during a run: the "Stop the setup?" question is asked once, the window stays open until the run has
/// stopped and read back what changed, a run that ended while the question was open simply lets the window close, and an
/// elevated window (which runs the machine-wide steps itself) never exits in the middle of one.
/// </summary>
public sealed class WindowCloseTests
{
    private static readonly MethodInfo OnFormClosing = typeof(MainForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void A_run_that_ends_while_the_stop_question_is_open_lets_the_window_close() => Sta.Run(() =>
    {
        Exception? unhandled = null;
        Application.ThreadException += (_, e) => unhandled ??= e.Exception;
        var log = new MemoryLog();
        using var detecting = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var spooler = new StubSpooler { Queues = () => Block(detecting, release) };
        using var form = new MainForm(TestRuntime.Create("en", SetupMode.Diagnostics, spooler, log))
        {
            // The employee is still reading the question when the run finishes on its own, then answers Yes.
            ConfirmStop = () =>
            {
                release.Set();
                PumpUntil(() => log.Entries.Any(e => e.Event == "ui.run.finished"));
                return true;
            },
        };
        _ = form.Handle;
        bool closed = false;
        form.FormClosed += (_, _) => closed = true;
        Task run = form.RunFlowAsync(SetupMode.Diagnostics);
        PumpUntil(() => detecting.IsSet);

        form.Close();

        Assert.Null(unhandled); // before: a NullReferenceException in OnFormClosing, "Something went wrong" and exit code 10
        Assert.True(closed, "The run had ended, so the window closes.");
        Assert.True(run.IsCompleted);
        Assert.DoesNotContain(log.Entries, e => e.Event == "ui.close.during_run");
    });

    [Fact]
    public void Closing_again_while_the_run_stops_keeps_the_window_open_until_the_result_was_read_back() => Sta.Run(() =>
    {
        Exception? unhandled = null;
        Application.ThreadException += (_, e) => unhandled ??= e.Exception;
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, "USB001").WithTicketForm().Build();
        var broker = new BlockingBroker();
        var log = new MemoryLog();
        int asked = 0;
        using var form = new MainForm(new AppRuntime(scenario.Environment(), broker, new CommandLineOptions { Language = "en" }, log, "1.0.0", AppContext.BaseDirectory))
        {
            ConfirmStop = () => ++asked > 0,
        };
        _ = form.Handle;
        bool closed = false;
        bool verifiedBeforeClose = false;
        form.FormClosed += (_, _) =>
        {
            closed = true;
            verifiedBeforeClose = scenario.Log.Contains("flow.verified");
        };
        Task run = form.RunFlowAsync(SetupMode.Setup);
        PumpUntil(() => broker.Entered.IsSet); // the step with administrator rights is running

        form.Close();
        Assert.False(closed, "The first close waits for the running step.");
        form.Close(); // the employee clicks X again while "Stopping after the current step…" is shown

        Assert.False(closed, "A second close must not leave the running step and the read-back behind.");
        Assert.Equal(1, asked);
        Assert.Equal(Strings.English.Text("progress.cancelling"), Assert.IsType<ProgressView>(form.CurrentView).Texts.Activity);
        Assert.Contains(log.Entries, e => e.Event == "ui.close.while_stopping");

        broker.Release.Set();
        PumpUntil(() => closed);

        Assert.True(verifiedBeforeClose, "The result is read back before the window closes.");
        Assert.Contains(scenario.Log.Entries, e => e.EventName == "flow.result" && Equals(e.Data?["outcome"], nameof(FlowOutcome.Cancelled)));
        Assert.Null(unhandled);
        PumpUntil(() => run.IsCompleted);
    });

    [Theory]
    [InlineData(CloseReason.WindowsShutDown)]
    [InlineData(CloseReason.TaskManagerClosing)]
    public void An_elevated_window_does_not_exit_in_the_middle_of_a_run_it_runs_itself(CloseReason reason) => Sta.Run(() =>
    {
        var log = new MemoryLog();
        using var detecting = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var spooler = new StubSpooler { Queues = () => Block(detecting, release) };
        using var form = new MainForm(TestRuntime.Create("en", SetupMode.Diagnostics, spooler, log) with { Elevation = new ElevatedProcess() })
        {
            ConfirmStop = () => throw new Xunit.Sdk.XunitException("Nobody is asked while Windows closes the window."),
        };
        _ = form.Handle;
        bool closed = false;
        form.FormClosed += (_, _) => closed = true;
        Task run = form.RunFlowAsync(SetupMode.Diagnostics);
        PumpUntil(() => detecting.IsSet);

        var closing = new FormClosingEventArgs(reason, cancel: false);
        OnFormClosing.Invoke(form, [closing]);

        Assert.True(closing.Cancel, "The window waits for the run it is running itself.");
        Assert.Contains(log.Entries, e => e.Event == "ui.close.during_run" && Equals(e.Data?["reason"], reason.ToString()));
        Assert.Equal(Strings.English.Text("progress.cancelling"), Assert.IsType<ProgressView>(form.CurrentView).Texts.Activity);
        PumpUntil(() => false, TimeSpan.FromMilliseconds(200));
        Assert.False(closed);

        release.Set();
        PumpUntil(() => closed); // the run has stopped, and then the window closes
        Assert.True(run.IsCompleted);
        Assert.Contains(log.Entries, e => e.Event == "ui.run.finished");
    });

    [Fact]
    public void A_window_whose_machine_steps_run_in_the_worker_may_close_at_once_when_windows_shuts_down() => Sta.Run(() =>
    {
        var log = new MemoryLog();
        using var detecting = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var spooler = new StubSpooler { Queues = () => Block(detecting, release) };
        using var form = new MainForm(TestRuntime.Create("en", SetupMode.Diagnostics, spooler, log));
        _ = form.Handle;
        Task run = form.RunFlowAsync(SetupMode.Diagnostics);
        PumpUntil(() => detecting.IsSet);

        var closing = new FormClosingEventArgs(CloseReason.WindowsShutDown, cancel: false);
        OnFormClosing.Invoke(form, [closing]);

        // The elevated worker notices the closed pipe and stops at its next action boundary on its own.
        Assert.False(closing.Cancel);
        Assert.DoesNotContain(log.Entries, e => e.Event == "ui.close.during_run");
        release.Set();
        PumpUntil(() => run.IsCompleted);
    });

    [Fact]
    public void A_new_run_after_a_stop_whose_close_was_cancelled_shows_its_result() => Sta.Run(() =>
    {
        var log = new MemoryLog();
        using var detecting = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var spooler = new StubSpooler { Queues = () => Block(detecting, release) };
        using var form = new MainForm(TestRuntime.Create("en", SetupMode.Diagnostics, spooler, log)) { ConfirmStop = () => true };
        _ = form.Handle;
        bool closed = false;
        int closings = 0;
        // Something else keeps the window open when the stopped run closes it.
        form.FormClosing += (_, e) => e.Cancel = ++closings == 2;
        form.FormClosed += (_, _) => closed = true;
        Task first = form.RunFlowAsync(SetupMode.Diagnostics);
        PumpUntil(() => detecting.IsSet);
        form.Close();
        release.Set();
        PumpUntil(() => first.IsCompleted && closings == 2);
        Assert.False(closed);

        Task second = form.RunFlowAsync(SetupMode.Diagnostics);
        PumpUntil(() => second.IsCompleted);

        Assert.False(closed, "The second run was not stopped, so its result is shown and the window stays open.");
        Assert.IsType<ResultView>(form.CurrentView);
    });

    /// <summary>Detection blocks in GetQueues until <paramref name="release"/> is set.</summary>
    private static IReadOnlyList<PrintQueueInfo> Block(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        entered.Set();
        release.Wait(TimeSpan.FromSeconds(30));
        return [];
    }

    /// <summary>Pumps the UI thread until <paramref name="condition"/> holds; with <paramref name="duration"/>, pumps that long and stops.</summary>
    private static void PumpUntil(Func<bool> condition, TimeSpan? duration = null)
    {
        var deadline = DateTime.UtcNow + (duration ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.True(duration is not null, "Timed out waiting for the UI thread.");
                return;
            }
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Like <see cref="Amwag.PrinterSetup.App.Elevation.ElevationBroker"/> once the worker has started: a cancelled run waits for
    /// the worker to finish its step (until <see cref="Release"/>) and then returns Cancelled.
    /// </summary>
    private sealed class BlockingBroker : IElevationBroker
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool IsElevated => false;

        public async Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
        {
            Entered.Set();
            await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(60)), CancellationToken.None);
            return cancellationToken.IsCancellationRequested
                ? new ElevatedRunResult(ElevationOutcome.Cancelled, ExitCodes.Cancelled)
                : new ElevatedRunResult(ElevationOutcome.WorkerFailed, ExitCodes.InternalError) { Error = "not stopped" };
        }
    }

    /// <summary>An administrator's window: it would run the machine-wide steps in its own process.</summary>
    private sealed class ElevatedProcess : IElevationBroker
    {
        public bool IsElevated => true;

        public Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An elevated window starts no worker.");
    }
}
