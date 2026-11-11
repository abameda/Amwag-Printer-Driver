using System.Drawing.Imaging;
using System.Reflection;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Support;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// Dev-only visual check: renders every view in English and Arabic to PNG files (AMWAG_UI_SHOTS, else %TEMP%\amwag-ui-shots)
/// with Control.DrawToBitmap. Windows are created but never shown. Run with: dotnet test --filter Category=Screenshots
/// </summary>
[Trait("Category", "Screenshots")]
public sealed class ScreenshotTests
{
    private static readonly string Output = Environment.GetEnvironmentVariable("AMWAG_UI_SHOTS") is { Length: > 0 } folder
        ? folder
        : Path.Combine(Path.GetTempPath(), "amwag-ui-shots");

    public static TheoryData<string> Languages => new() { "en", "ar" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void Progress(string language) => Render("progress", language, SetupMode.Setup, form =>
    {
        StartFakeRun(form, SetupMode.Setup);
        form.ApplyUpdate(Step(StepId.Detect, StepState.Succeeded, null, ProgressPhase.Completed));
        form.ApplyUpdate(new FlowUpdate(FlowEventKind.TargetSelected, Target: UiSamples.UsbPrinter));
        form.ApplyUpdate(Step(StepId.CheckDriver, StepState.Succeeded, null, ProgressPhase.Completed));
        form.ApplyUpdate(Step(StepId.InstallDriver, StepState.Running, ActionKind.InstallDriver, ProgressPhase.Installing, "InstallPrinterDriverFromPackage: TA P-4030DN KX (Windows x64)"));
        form.ApplyUpdate(new FlowUpdate(FlowEventKind.ElevationRequested));
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ChoosePrinter(string language) => Render("choose-printer", language, SetupMode.Setup, form =>
    {
        StartFakeRun(form, SetupMode.Setup);
        form.Steps.SetState(StepId.Detect, StepState.Running);
        var network = UiSamples.NetworkPrinter with { Confidence = MatchConfidence.DriverOnly, Queue = new() { Name = "TA P-4531DN (Office)", DriverName = "TA P-4531DN KX", PortName = "IP_10.0.0.5" } };
        form.AskChoosePrinter([UiSamples.UsbPrinter, network], _ => true);
        var view = (ChoosePrinterView)form.CurrentView!;
        view.InitialFocus!.Focus();
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ConfirmModel(string language) => Render("confirm-model", language, SetupMode.Setup, form =>
    {
        StartFakeRun(form, SetupMode.Setup);
        form.Steps.SetState(StepId.Detect, StepState.Running);
        form.AskConfirmModel(UiSamples.NetworkPrinter, _ => true);
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void NetworkAddress(string language) => Render("network-address", language, SetupMode.Setup, form =>
    {
        StartFakeRun(form, SetupMode.Setup);
        form.Steps.SetState(StepId.Detect, StepState.Warning);
        form.AskNetworkAddress(new NetworkAddressPrompt(true, "10.0.0.99", FindingCode.NetworkPrinterUnreachable, "TCP 9100 timed out"), _ => true);
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ChooseModel(string language) => Render("choose-model", language, SetupMode.Setup, form =>
    {
        StartFakeRun(form, SetupMode.Setup);
        form.Steps.SetState(StepId.Detect, StepState.Running);
        form.AskModel(UiSamples.UnidentifiedNetworkPrinter, TestRuntime.Config.Models, _ => true);
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultSuccess(string language) => Render("result-success", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        form.ShowResult(UiSamples.Ready());
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultWarning(string language) => Render("result-warning", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.Verify, exceptState: StepState.Warning);
        form.ShowResult(UiSamples.ReadyWithWarnings());
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultFailure(string language) => Render("result-failure", language, SetupMode.Setup, form =>
    {
        form.Steps.SetState(StepId.Detect, StepState.Succeeded);
        form.Steps.SetState(StepId.CheckDriver, StepState.Succeeded);
        form.Steps.SetState(StepId.InstallDriver, StepState.Running);
        form.ShowResult(UiSamples.Failed());
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultDeclined(string language) => Render("result-declined", language, SetupMode.Setup, form =>
    {
        form.Steps.SetState(StepId.Detect, StepState.Succeeded);
        form.Steps.SetState(StepId.CheckDriver, StepState.Succeeded);
        form.Steps.SetState(StepId.InstallDriver, StepState.Running);
        form.ShowResult(UiSamples.ElevationDeclined());
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultDiagnostics(string language) => Render("result-diagnostics", language, SetupMode.Diagnostics, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        form.ShowResult(UiSamples.Diagnostics());
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void TestPrintStatus(string language) => Render("testprint-status", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        form.StartTestPrint(UiSamples.Ready());
        ((TestPrintView)form.CurrentView!).ShowWorking(new JobObservation(JobStage.Queued, JobProblems.PaperOut, 12, "Job 12"));
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void TestPrintCheck(string language) => Render("testprint-check", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        form.StartTestPrint(UiSamples.Ready());
        var paper = new UsedPaper("AMWAG Ticket", 82.296, 170.18, false, "MP Tray", new RectangleF(4.2f, 4.2f, 73.9f, 161.8f));
        ((TestPrintView)form.CurrentView!).ShowFinished(
            new TestPrintSubmission("AMWAG Ticket Printer", "AMWAG Test Ticket 2026-11-03 14:05:00", DateTimeOffset.Now, paper),
            new JobMonitorResult(JobMonitorOutcome.SentToPrinter, JobProblems.None, 12, ""));
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void TestPrintGuidance(string language) => Render("testprint-guidance", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        form.StartTestPrint(UiSamples.Ready());
        ((TestPrintView)form.CurrentView!).ShowAnswer(false);
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void TicketCheckWatching(string language) => Render("ticketcheck-watching", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        UiSamples.StartTicketCheckWithoutWatching(form, UiSamples.Ready());
        ((TicketCheckView)form.CurrentView!).ShowWatching(new TicketCheckProgress(TimeSpan.FromSeconds(17), TimeSpan.FromMinutes(2), null));
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void TicketCheckDifferences(string language) => Render("ticketcheck-differences", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        UiSamples.StartTicketCheckWithoutWatching(form, UiSamples.Ready());
        ((TicketCheckView)form.CurrentView!).ShowResult(UiSamples.TicketCheckDifferences());
    }, height: 1000);

    [Theory]
    [MemberData(nameof(Languages))]
    public void TicketCheckMatches(string language) => Render("ticketcheck-matches", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        UiSamples.StartTicketCheckWithoutWatching(form, UiSamples.Ready());
        ((TicketCheckView)form.CurrentView!).ShowResult(UiSamples.TicketCheckMatches());
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void TicketCheckGone(string language) => Render("ticketcheck-gone", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        UiSamples.StartTicketCheckWithoutWatching(form, UiSamples.Ready());
        ((TicketCheckView)form.CurrentView!).ShowResult(UiSamples.TicketCheckGone());
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultSuccessDetails(string language) => Render("result-success-details", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        form.ShowResult(UiSamples.Ready());
        ExpandDetails(form);
    }, height: 1150);

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultWarningDetails(string language) => Render("result-warning-details", language, SetupMode.Setup, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.Verify, exceptState: StepState.Warning);
        form.ShowResult(UiSamples.ReadyWithWarnings());
        ExpandDetails(form);
    }, height: 1450);

    [Theory]
    [MemberData(nameof(Languages))]
    public void ResultDiagnosticsDetails(string language) => Render("result-diagnostics-details", language, SetupMode.Diagnostics, form =>
    {
        SetAll(form, StepState.Succeeded, except: StepId.InstallDriver, exceptState: StepState.Skipped);
        form.ShowResult(UiSamples.Diagnostics());
    }, height: 1400);

    [Fact]
    public void TestTicketPreview()
    {
        Directory.CreateDirectory(Output);
        var paper = PaperDimensions.FromUnits(3.24m, 6.70m, LengthUnit.Inch);
        var drawing = TestTicketLayout.Create(paper, new TestTicketInfo("AMWAG Ticket", "TA P-4030DN", "AMWAG Ticket Printer", new DateTimeOffset(2026, 11, 3, 14, 5, 0, TimeSpan.FromHours(2))));

        const float dpi = 300;
        using var bitmap = new Bitmap((int)Math.Ceiling((double)paper.WidthInches * dpi), (int)Math.Ceiling((double)paper.HeightInches * dpi));
        bitmap.SetResolution(dpi, dpi);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.PageUnit = GraphicsUnit.Millimeter;
            TestTicketRenderer.Draw(g, drawing);
            TestTicketRenderer.DrawPrintableArea(g, new RectangleF(4.2f, 4.2f, (float)paper.WidthMillimeters - 8.4f, (float)paper.HeightMillimeters - 8.4f));
        }
        bitmap.Save(Path.Combine(Output, "test-ticket.png"), ImageFormat.Png);
    }

    private static FlowUpdate Step(StepId step, StepState state, ActionKind? action, ProgressPhase phase, string detail = "") =>
        new(FlowEventKind.Step, new ProgressUpdate(step, state, action, phase, detail));

    private static void SetAll(MainForm form, StepState state, StepId except, StepState exceptState)
    {
        foreach (var step in Enum.GetValues<StepId>())
            form.Steps.SetState(step, step == except ? exceptState : state);
    }

    /// <summary>Puts the window in the "running" state a real run has (progress view, mode), without starting the flow.</summary>
    private static void StartFakeRun(MainForm form, SetupMode mode)
    {
        var progress = new ProgressView(ViewContextOf(form), mode);
        typeof(MainForm).GetField("_progress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, progress);
        typeof(MainForm).GetMethod("ShowView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [progress]);
    }

    private static ViewContext ViewContextOf(MainForm form) =>
        (ViewContext)typeof(MainForm).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    /// <summary>Clicks "Show details" on the result view (its handler rebuilds the details in place).</summary>
    private static void ExpandDetails(MainForm form)
    {
        static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));
        var strings = Strings.For(form.Language);
        var toggle = All(form.CurrentView!).OfType<ActionButton>().Single(b => b.Text == strings.Text("result.details.show"));
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(toggle, [EventArgs.Empty]);
    }

    private static readonly ItContact[] ItContacts =
    [
        new() { Name = "Eng. Abdelhmeed Elshorbagy", NameArabic = "م. عبدالحميد الشوربجي", WhatsApp = "201061922226" },
        new() { Name = "Eng. Abdelrhman Elshorbagy", NameArabic = "م. عبدالرحمن الشوربجي", WhatsApp = "201011955580" },
    ];

    /// <summary>The window with the Contact IT button over a failed run, and the Contact IT dialog before and after choosing.</summary>
    [Theory]
    [MemberData(nameof(Languages))]
    public void ContactIt(string language)
    {
        Render("contact-it-window", language, SetupMode.Setup, form => form.ShowResult(UiSamples.Failed()), config: TestRuntime.Config with { ItContacts = ItContacts });
        Sta.Run(() =>
        {
            Application.EnableVisualStyles();
            var strings = language == "ar" ? Strings.Arabic : Strings.English;
            using var dialog = new ContactItDialog(strings, ItContacts, (_, _) => new SupportPackage("shot.png", "logs.zip"), _ => { });
            Save(dialog, $"contact-it-choose-{language}.png");
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
                dialog.Controls[0].Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>()).OfType<ActionButton>().First(), [EventArgs.Empty]);
            Save(dialog, $"contact-it-sent-{language}.png");
        });

        static void Save(Form dialog, string file)
        {
            Directory.CreateDirectory(Output);
            typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(bool)])!.Invoke(dialog, [true]);
            dialog.PerformLayout();
            Application.DoEvents();
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            bitmap.Save(Path.Combine(Output, file), ImageFormat.Png);
        }
    }

    /// <summary>The loading screen as the logo arrives (300 ms), once it has (1000 ms), mid-wave (2300 ms) and at rest.</summary>
    [Theory]
    [MemberData(nameof(Languages))]
    public void Splash(string language) => Sta.Run(() =>
    {
        Directory.CreateDirectory(Output);
        using var splash = new StartupSplash(language == "ar" ? UiLanguage.Arabic : UiLanguage.English, "1.0.0");
        splash.Report("splash.loadingSettings", 0.6);
        foreach (var (name, ms) in new[] { ("arriving", 300.0), ("arrived", 1000.0), ("wave", 2300.0), ("still", double.MaxValue) })
        {
            using var bitmap = new Bitmap(splash.ClientSize.Width, splash.ClientSize.Height);
            using (var g = Graphics.FromImage(bitmap))
                splash.DrawFrame(g, ms, 0.6);
            bitmap.Save(Path.Combine(Output, $"splash-{name}-{language}.png"), ImageFormat.Png);
        }
    });

    /// <param name="height">Logical client height; taller than the real 636 px window to show content that otherwise scrolls (Windows caps it near the screen height).</param>
    private static void Render(string name, string language, SetupMode mode, Action<MainForm> arrange, int height = 636, DeploymentConfig? config = null) => Sta.Run(() =>
    {
        Directory.CreateDirectory(Output);
        Application.EnableVisualStyles(); // as the app does at startup, so native controls look as they will
        using var form = new MainForm(TestRuntime.Create(language, mode, config: config));
        if (height != 636)
            form.ClientSize = new Size(form.ClientSize.Width, form.LogicalToDeviceUnits(height));
        // Create every window handle (hidden): DrawToBitmap only paints children that have handles.
        typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(bool)])!.Invoke(form, [true]);
        arrange(form);
        typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(bool)])!.Invoke(form, [true]);
        form.PerformLayout();
        Application.DoEvents();

        var content = form.Controls[0];
        using var bitmap = new Bitmap(content.Width, content.Height);
        content.DrawToBitmap(bitmap, new Rectangle(Point.Empty, content.Size));
        bitmap.Save(Path.Combine(Output, $"{name}-{language}.png"), ImageFormat.Png);
    });
}
