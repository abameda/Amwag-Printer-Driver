using System.Reflection;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// Drives the main window through runs over a scenario's fakes as the employee would (a run, then the buttons of its result) and
/// reads what the window shows. Call from <see cref="Sta.Run"/>.
/// </summary>
internal static class WindowRun
{
    private static readonly FieldInfo LastResult = typeof(MainForm).GetField("_lastResult", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// A window over the scenario's fakes (its handle created, never shown). <paramref name="commandLine"/> is how IT started it
    /// (--address, --model, --confirm-model); by default only the language and the mode.
    /// </summary>
    public static MainForm Create(Scenario scenario, SetupMode mode, Func<FlowEnvironment, IElevationBroker>? broker = null, string language = "en",
        MemoryLog? log = null, Func<SetupOptions, SetupOptions>? options = null, CommandLineOptions? commandLine = null)
    {
        FlowEnvironment environment = scenario.Environment(mode, options);
        var runtime = new AppRuntime(environment, broker?.Invoke(environment) ?? new InProcessBroker(environment),
            commandLine ?? new CommandLineOptions { Language = language, Mode = mode }, log ?? new MemoryLog(), "1.0.0", AppContext.BaseDirectory);
        var form = new MainForm(runtime);
        _ = form.Handle;
        return form;
    }

    /// <summary>Runs <paramref name="mode"/> in the window and waits until it shows the result.</summary>
    public static FlowResult Run(MainForm form, SetupMode mode, TargetSelector? repairTarget = null)
    {
        Task run = form.RunFlowAsync(mode, repairTarget);
        PumpUntil(() => run.IsCompleted);
        Assert.IsType<ResultView>(form.CurrentView);
        return ResultOf(form)!;
    }

    /// <summary>Presses the result view's button with this text and waits until the run it starts shows its result.</summary>
    public static FlowResult Press(MainForm form, string text)
    {
        var view = Assert.IsType<ResultView>(form.CurrentView);
        Click(view, text);
        PumpUntil(() => ResultOf(form) is not null && form.CurrentView is ResultView shown && shown != view);
        return ResultOf(form)!;
    }

    /// <summary>The result the window shows (the one its buttons act on); null while a run is going on.</summary>
    public static FlowResult? ResultOf(MainForm form) => (FlowResult?)LastResult.GetValue(form);

    /// <summary>The notes as screen readers hear them ("Title. Text"), the headline first.</summary>
    public static List<string> Notes(Control root) => All(root).OfType<NotePanel>().Select(n => n.AccessibleName ?? "").ToList();

    /// <summary>Every label text (the banner's title and sentence, the technical details).</summary>
    public static List<string> Labels(Control root) => All(root).OfType<Label>().Select(l => l.Text).Where(t => t.Length > 0).ToList();

    /// <summary>Every step of the list as screen readers hear it ("Checking driver: Driver is ready").</summary>
    public static List<string> Steps(MainForm form) => Enum.GetValues<StepId>().Select(form.Steps.Describe).ToList();

    public static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    /// <summary>Presses the button with this text, as the employee would (also while the window is not shown).</summary>
    public static void Click(Control root, string text)
    {
        var button = All(root).OfType<ActionButton>().First(b => b.Text == text);
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
    }

    /// <summary>
    /// Pumps the window thread until <paramref name="condition"/> holds. Application.DoEvents on a thread without Application.Run
    /// replaces the Windows Forms synchronization context with a plain one, so a later run (Repair, Run Setup) would report its
    /// progress on thread-pool threads; the context is put back after each pump, as Application.Run keeps it in the program.
    /// </summary>
    public static void PumpUntil(Func<bool> condition)
    {
        SynchronizationContext? window = SynchronizationContext.Current;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the UI thread.");
            Application.DoEvents();
            SynchronizationContext.SetSynchronizationContext(window);
            Thread.Sleep(10);
        }
        SynchronizationContext.SetSynchronizationContext(window);
    }
}
