using System.Drawing.Drawing2D;
using System.Globalization;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// A thin progress line in the accent color: the known fraction, or a sliding segment while the duration is unknown.
/// (The native progress bar ignores colors under visual styles and does not mirror in right-to-left layout.)
/// </summary>
internal sealed class ProgressLine : Control
{
    private static readonly Color Track = Theme.AccentSoft;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private double? _fraction;
    private float _phase;

    public ProgressLine()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.012f) % 1.3f;
            Invalidate();
        };
        UpdateTimer();
    }

    /// <summary>0..1, or null while the duration is unknown.</summary>
    public void SetFraction(double? fraction)
    {
        _fraction = fraction is double value ? Math.Clamp(value, 0, 1) : null;
        UpdateTimer();
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    private void UpdateTimer() => _timer.Enabled = _fraction is null && Visible && !IsDisposed;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateTimer();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        UpdateTimer();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int radius = Math.Max(1, Height / 2);
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        using var trackPath = Shapes.RoundedRectangle(track, radius);
        using (var brush = new SolidBrush(Track))
            g.FillPath(brush, trackPath);

        float x, width;
        if (_fraction is double fraction)
        {
            x = 0;
            width = (float)(Width * fraction);
        }
        else
        {
            width = Width * 0.3f;
            x = (_phase - 0.3f) * Width;
        }
        if (RightToLeft == RightToLeft.Yes)
            x = Width - x - width;
        if (width < 1)
            return;

        var state = g.Save();
        g.SetClip(trackPath);
        using (var bar = Shapes.RoundedRectangle(Rectangle.Round(new RectangleF(x, 0, width, Height - 1)), radius))
        using (var brush = new SolidBrush(Theme.Accent))
            g.FillPath(brush, bar);
        g.Restore(state);
    }

    protected override void OnRightToLeftChanged(EventArgs e)
    {
        base.OnRightToLeftChanged(e);
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ProgressLineAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ProgressLineAccessibleObject(ProgressLine owner) : ControlAccessibleObject(owner)
    {
        public override string? Value => owner._fraction is double fraction ? fraction.ToString("P0", CultureInfo.CurrentCulture) : null;
    }
}
