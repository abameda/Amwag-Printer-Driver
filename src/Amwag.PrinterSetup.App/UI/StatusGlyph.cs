using System.Drawing.Drawing2D;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI;

internal enum GlyphKind { Pending, Running, Succeeded, Skipped, Warning, Failed, Unknown }

/// <summary>State glyphs drawn with GDI+ so they stay crisp at any DPI; each one has a distinct shape, not only a color.</summary>
internal static class StatusGlyph
{
    public static GlyphKind For(StepState state) => state switch
    {
        StepState.Running => GlyphKind.Running,
        StepState.Succeeded => GlyphKind.Succeeded,
        StepState.Skipped => GlyphKind.Skipped,
        StepState.Warning => GlyphKind.Warning,
        StepState.Failed => GlyphKind.Failed,
        _ => GlyphKind.Pending,
    };

    public static GlyphKind For(CheckStatus status) => status switch
    {
        CheckStatus.Pass => GlyphKind.Succeeded,
        CheckStatus.Warning => GlyphKind.Warning,
        CheckStatus.Fail => GlyphKind.Failed,
        CheckStatus.NotApplicable => GlyphKind.Skipped,
        _ => GlyphKind.Unknown,
    };

    /// <param name="runningAngle">Start angle of the spinning arc, in degrees.</param>
    /// <param name="mirrored">Right-to-left layout: the arc spins the other way.</param>
    public static void Draw(Graphics g, RectangleF bounds, GlyphKind kind, float runningAngle = 0, bool mirrored = false)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float d = Math.Min(bounds.Width, bounds.Height);
        var r = new RectangleF(bounds.X + (bounds.Width - d) / 2, bounds.Y + (bounds.Height - d) / 2, d, d);
        float stroke = Math.Max(1.5f, d / 11f);

        switch (kind)
        {
            case GlyphKind.Pending:
                using (var pen = new Pen(Theme.Neutral, stroke))
                    g.DrawEllipse(pen, Inset(r, stroke / 2));
                break;

            case GlyphKind.Running:
                using (var track = new Pen(Theme.AccentSoft, stroke))
                using (var arc = new Pen(Theme.Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    var ring = Inset(r, stroke / 2);
                    g.DrawEllipse(track, ring);
                    g.DrawArc(arc, ring, mirrored ? -runningAngle : runningAngle, mirrored ? -100 : 100);
                }
                break;

            case GlyphKind.Succeeded:
                Fill(g, r, Theme.Success);
                using (var pen = WhitePen(stroke * 1.15f))
                    g.DrawLines(pen, [P(r, 0.28f, 0.52f), P(r, 0.44f, 0.67f), P(r, 0.73f, 0.36f)]);
                break;

            case GlyphKind.Skipped:
                Fill(g, r, Theme.Neutral);
                using (var pen = WhitePen(stroke * 1.15f))
                    g.DrawLine(pen, P(r, 0.3f, 0.5f), P(r, 0.7f, 0.5f));
                break;

            case GlyphKind.Warning:
                Fill(g, r, Theme.WarningGlyph);
                using (var pen = WhitePen(stroke * 1.2f))
                    g.DrawLine(pen, P(r, 0.5f, 0.27f), P(r, 0.5f, 0.56f));
                using (var dot = new SolidBrush(Color.White))
                    g.FillEllipse(dot, CenteredSquare(P(r, 0.5f, 0.73f), stroke * 1.35f));
                break;

            case GlyphKind.Failed:
                Fill(g, r, Theme.Error);
                using (var pen = WhitePen(stroke * 1.15f))
                {
                    g.DrawLine(pen, P(r, 0.34f, 0.34f), P(r, 0.66f, 0.66f));
                    g.DrawLine(pen, P(r, 0.66f, 0.34f), P(r, 0.34f, 0.66f));
                }
                break;

            case GlyphKind.Unknown:
                using (var pen = new Pen(Theme.Neutral, stroke))
                {
                    g.DrawEllipse(pen, Inset(r, stroke / 2));
                    g.DrawArc(pen, new RectangleF(r.X + d * 0.37f, r.Y + d * 0.22f, d * 0.26f, d * 0.26f), 180, 270);
                    g.DrawLine(pen, P(r, 0.5f, 0.48f), P(r, 0.5f, 0.6f));
                }
                using (var dot = new SolidBrush(Theme.Neutral))
                    g.FillEllipse(dot, CenteredSquare(P(r, 0.5f, 0.74f), stroke * 1.2f));
                break;
        }

        g.Restore(state);
    }

    private static void Fill(Graphics g, RectangleF r, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, r);
    }

    private static Pen WhitePen(float width) => new(Color.White, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private static PointF P(RectangleF r, float x, float y) => new(r.X + r.Width * x, r.Y + r.Height * y);

    private static RectangleF Inset(RectangleF r, float by) => new(r.X + by, r.Y + by, r.Width - 2 * by, r.Height - 2 * by);

    private static RectangleF CenteredSquare(PointF center, float size) => new(center.X - size / 2, center.Y - size / 2, size, size);
}

/// <summary>A glyph as a small control, for verification rows and banners.</summary>
internal sealed class GlyphBox : Control
{
    public GlyphBox(GlyphKind kind, int size)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Kind = kind;
        Size = new Size(size, size);
        Margin = Padding.Empty;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
    }

    public GlyphKind Kind { get; }

    protected override void OnPaint(PaintEventArgs e) =>
        StatusGlyph.Draw(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Kind);
}
