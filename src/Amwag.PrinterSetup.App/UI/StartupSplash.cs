using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using Amwag.PrinterSetup.App.Localization;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// The loading screen shown while the window starts: the AMWAG logo arriving and then waving, the startup stage with its
/// progress, the version and the copyright. Program reports each stage; <see cref="HandOver"/> then shows the main window once
/// the logo has arrived, so the screen never just flashes. With Windows' animation effects off everything stands still.
/// </summary>
internal sealed class StartupSplash : Form
{
    // Logo motion of the AMWAG identity: the swoosh fills, the arcs follow 100 ms apart, the wordmark rises, then a 2200 ms wave.
    private const double RevealMs = 450, ArcDelayMs = 250, ArcStepMs = 100, ArcMs = 300, WordDelayMs = 560, WordMs = 380;
    private const double GhostDelayMs = 1000, GhostMs = 200, WaveDelayMs = 1200, WaveStepMs = 100, WaveMs = 2200;
    private static readonly string[] MarkIds = ["swoosh", "arc-outer", "arc-inner", "arc-right"];
    private static readonly string[] WordIds = ["wordmark", "travel"];

    private static readonly Font TitleFont = new("Segoe UI Semibold", 13.5f);

    private readonly Strings _strings;
    private readonly string _version;
    private readonly bool _still = Motion.Reduced;
    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly RectangleF _markBounds;
    private string _stageKey = "splash.loadingSettings";
    private double _target;
    private double _shown;
    private Form? _next;
    private ApplicationContext? _context;

    public StartupSplash(UiLanguage language, string version)
    {
        _strings = Strings.For(language);
        _version = version.Split('+')[0];
        var s = UiScale.For(this);

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(DeviceDpi, DeviceDpi);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        ClientSize = new Size(s.Px(560), s.Px(340));
        BackColor = Theme.Navy;
        Text = "AMWAG Printer Setup";
        Icon = MainForm.LoadApplicationIcon();
        RightToLeft = _strings.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
        AccessibleName = "AMWAG Printer Setup";

        _markBounds = MarkIds.Select(id => BrandArt.Lockup.Shape(id).Path.GetBounds()).Aggregate(RectangleF.Union);
        _timer.Tick += (_, _) => Tick();
        Report(_stageKey, 0.1);
    }

    /// <summary>The stage now running ("splash.*" text key) and how far startup will be once it is done (0..1).</summary>
    public void Report(string stageKey, double fraction)
    {
        _stageKey = stageKey;
        _target = Math.Clamp(fraction, 0, 1);
        if (_still)
            _shown = _target;
        AccessibleDescription = $"{_strings.Text(stageKey)} {Percent(_target)}";
        if (IsHandleCreated)
            Refresh(); // startup work runs on this thread before the message loop, so paint now
    }

    /// <summary>Shows the screen at once, before the rest of startup runs on this thread.</summary>
    public void ShowNow()
    {
        _clock.Start();
        Show();
        Refresh();
    }

    /// <summary>The message loop context: the splash stays until the logo has arrived, then <paramref name="next"/> takes over.</summary>
    public ApplicationContext HandOver(Form next)
    {
        _next = next;
        _target = 1;
        _context = new ApplicationContext();
        _timer.Start();
        return _context;
    }

    private void Tick()
    {
        // Ease the bar toward the reported progress rather than jumping.
        _shown = _still ? _target : _shown + (_target - _shown) * 0.18;
        if (_target - _shown < 0.004)
            _shown = _target;
        Invalidate();

        double minimum = _still ? 400 : WordDelayMs + WordMs + 500;
        if (_next is not null && _shown >= 1 && _clock.Elapsed.TotalMilliseconds >= minimum)
            Finish();
    }

    private void Finish()
    {
        ShowNext();
        Close();
    }

    private void ShowNext()
    {
        if (_next is not { } next || _context is null)
            return;
        _next = null;
        _timer.Stop();
        _context.MainForm = next;
        next.Show();
        next.Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // Closed before the hand-over (Alt+F4): go straight to the window.
        ShowNext();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounds the corners of the borderless window; earlier versions ignore it.
        int round = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e) =>
        DrawFrame(e.Graphics, _still ? double.MaxValue : _clock.Elapsed.TotalMilliseconds, _shown);

    /// <summary>The screen <paramref name="t"/> ms after it appeared, with the bar at <paramref name="shown"/> (tests render frames with it).</summary>
    internal void DrawFrame(Graphics g, double t, double shown)
    {
        g.Clear(Theme.Navy);
        float u = DeviceDpi / 96f;
        bool rtl = RightToLeft == RightToLeft.Yes;

        DrawLogo(g, new RectangleF(160 * u, 46 * u, 240 * u, 140 * u), t);

        var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        var direction = rtl ? TextFormatFlags.RightToLeft : 0;
        TextRenderer.DrawText(g, _strings.Text("splash.title"), TitleFont, Rect(u, 0, 196, 560, 28), Color.White, center | direction);

        // The stage at the reading start and the percentage at the end, above the bar.
        var row = Rect(u, 140, 240, 280, 20);
        var line = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | direction;
        TextRenderer.DrawText(g, _strings.Text(_stageKey), Theme.HeaderSubtitle, row, Theme.OnNavySecondary, line | (rtl ? TextFormatFlags.Right : TextFormatFlags.Left));
        // Digits and the percent sign keep their left-to-right order in Arabic too ("60%").
        TextRenderer.DrawText(g, Percent(shown), Theme.HeaderSubtitle, row, Theme.OnNavySecondary, (line & ~TextFormatFlags.RightToLeft) | (rtl ? TextFormatFlags.Left : TextFormatFlags.Right));

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = Rect(u, 140, 268, 280, 4);
        using (var path = Shapes.RoundedRectangle(track, track.Height / 2))
        using (var brush = new SolidBrush(Theme.NavyRaised))
            g.FillPath(brush, path);
        int fill = (int)Math.Round(track.Width * shown);
        if (fill >= track.Height)
        {
            var bar = new Rectangle(rtl ? track.Right - fill : track.X, track.Y, fill, track.Height);
            using var path = Shapes.RoundedRectangle(bar, track.Height / 2);
            using var brush = new SolidBrush(Color.White);
            g.FillPath(brush, path);
        }
        g.SmoothingMode = SmoothingMode.None;

        using (var pen = new Pen(Theme.NavyRaised, 1))
            g.DrawLine(pen, 0, 299 * u, Width, 299 * u);
        var strip = Rect(u, 20, 311, 520, 18);
        TextRenderer.DrawText(g, _strings.Format("splash.version", _version), Theme.Small, strip, Theme.OnNavySecondary, line | (rtl ? TextFormatFlags.Right : TextFormatFlags.Left));
        TextRenderer.DrawText(g, _strings.Text("footer.copyright"), Theme.Small, strip, Theme.OnNavySecondary, line | (rtl ? TextFormatFlags.Left : TextFormatFlags.Right));
    }

    /// <summary>The master lockup, animated by clipping and shifting its own shapes; the logo itself is never redrawn or mirrored.</summary>
    private void DrawLogo(Graphics g, RectangleF target, double t)
    {
        var logo = BrandArt.Lockup;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var fit = logo.Fit(target))
            g.MultiplyTransform(fit);

        float left = _markBounds.Left, width = _markBounds.Width;
        var tall = new RectangleF(left - width, logo.ViewBox.Top, width, logo.ViewBox.Height);

        // A faint copy shows where the wave has passed.
        double ghost = _still ? 0 : Progress(t, GhostDelayMs, GhostMs) * 0.28;
        if (ghost > 0)
            foreach (var id in MarkIds)
                Fill(g, logo.Shape(id), ghost);

        for (int i = 0; i < MarkIds.Length; i++)
        {
            var inner = g.Save();
            double arrive = i == 0 ? 1 : EaseOut(Progress(t, ArcDelayMs + (i - 1) * ArcStepMs, ArcMs));
            if (i > 0)
                g.TranslateTransform((float)((arrive - 1) * width * 0.035), 0);

            // The wave: a mark-wide window slides right out of the mark while the next one slides in after it.
            double wave = EaseInOut(Cycle(t, WaveDelayMs + i * WaveStepMs, WaveMs));
            float offset = (float)(wave * 2 * width);
            g.SetClip(tall with { X = left + offset });
            g.SetClip(tall with { X = left + offset - 2 * width }, CombineMode.Union);
            if (i == 0)
                g.SetClip(tall with { X = left, Width = (float)(width * EaseOut(Progress(t, 0, RevealMs))) }, CombineMode.Intersect);

            Fill(g, logo.Shape(MarkIds[i]), i == 0 ? 1 : arrive);
            g.Restore(inner);
        }

        double rise = EaseOut(Progress(t, WordDelayMs, WordMs));
        g.SetClip(logo.ViewBox);
        g.TranslateTransform(0, (float)((1 - rise) * logo.ViewBox.Height * 0.067));
        foreach (var id in WordIds)
            Fill(g, logo.Shape(id), rise);
        g.Restore(state);
    }

    private static void Fill(Graphics g, BrandShape shape, double opacity)
    {
        if (opacity <= 0)
            return;
        using var brush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * Math.Min(opacity, 1)), shape.Fill));
        g.FillPath(brush, shape.Path);
    }

    private static double Progress(double t, double delay, double duration) => Math.Clamp((t - delay) / duration, 0, 1);

    /// <summary>0 until the loop starts, then 0..1 every <paramref name="period"/>; reduced motion stays at the full logo.</summary>
    private static double Cycle(double t, double delay, double period) =>
        t == double.MaxValue || t < delay ? 0 : (t - delay) % period / period;

    private static double EaseOut(double p) => 1 - Math.Pow(1 - p, 3);

    private static double EaseInOut(double p) => p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

    private static Rectangle Rect(float u, float x, float y, float w, float h) =>
        Rectangle.Round(new RectangleF(x * u, y * u, w * u, h * u));

    private static string Percent(double fraction) => ((int)Math.Round(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
