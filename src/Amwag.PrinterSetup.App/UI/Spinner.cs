using System.Drawing.Drawing2D;
using System.Globalization;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// A round progress indicator in the brand navy: a turning arc while the duration is unknown, or a ring that fills with the
/// percentage inside once the fraction is known. Turns and fills counter-clockwise in right-to-left layout, and stands still
/// when Windows' animation effects are off. (The native progress bar ignores colors under visual styles and does not mirror.)
/// </summary>
internal sealed class Spinner : Control
{
    // Points, so it scales with the DPI like the other fonts.
    private static readonly Font PercentFont = new("Segoe UI Semibold", 7f);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private readonly bool _still = Motion.Reduced;
    private double? _fraction;
    private float _angle;

    public Spinner(UiScale scale)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(scale.Px(36), scale.Px(36));
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + 11) % 360; // about one turn a second
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

    private void UpdateTimer() => _timer.Enabled = _fraction is null && !_still && Visible && Parent is not null && !IsDisposed;

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

    protected override void OnRightToLeftChanged(EventArgs e)
    {
        base.OnRightToLeftChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float stroke = Width * 0.11f;
        var ring = new RectangleF(stroke / 2, stroke / 2, Width - stroke - 1, Height - stroke - 1);
        using (var track = new Pen(Theme.AccentSoft, stroke))
            g.DrawEllipse(track, ring);

        // Clockwise from the top in English; mirrored in Arabic.
        float direction = RightToLeft == RightToLeft.Yes ? -1 : 1;
        float start, sweep;
        if (_fraction is double fraction)
        {
            start = -90;
            sweep = (float)(360 * fraction);
        }
        else
        {
            start = -90 + _angle * direction;
            sweep = 90;
        }

        if (sweep > 0.5f)
        {
            using var arc = new Pen(Theme.Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, ring, start, Math.Min(sweep, 359.9f) * direction);
        }

        if (_fraction is double known)
        {
            string text = Percent(known);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, text, PercentFont, ClientRectangle, Theme.Text, flags);
        }
    }

    private static string Percent(double fraction) => ((int)Math.Round(fraction * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    protected override AccessibleObject CreateAccessibilityInstance() => new SpinnerAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class SpinnerAccessibleObject(Spinner owner) : ControlAccessibleObject(owner)
    {
        public override string? Value => owner._fraction is double fraction ? fraction.ToString("P0", CultureInfo.CurrentCulture) : null;
    }
}
