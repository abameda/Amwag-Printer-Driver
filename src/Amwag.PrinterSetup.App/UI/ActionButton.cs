using System.Drawing.Drawing2D;

namespace Amwag.PrinterSetup.App.UI;

internal enum ButtonKind
{
    /// <summary>The one main action of a view: filled amber with ink text.</summary>
    Primary,
    /// <summary>White with a thin border.</summary>
    Secondary,
    /// <summary>Text only, for low-emphasis actions (report, log folder).</summary>
    Ghost,
    /// <summary>Outlined white, for the navy header.</summary>
    OnDark,
}

/// <summary>A flat, rounded push button with hover, pressed, disabled and a clearly visible keyboard focus ring.</summary>
internal sealed class ActionButton : Button
{
    private bool _hover;
    private bool _pressed;

    public ActionButton(ButtonKind kind, UiScale scale)
    {
        Kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        UseMnemonic = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = kind == ButtonKind.Primary ? Theme.BodyStrong : Theme.Body;
        Padding = kind == ButtonKind.Ghost ? scale.Pad(8, 4, 8, 4) : scale.Pad(16, 5, 16, 5);
        MinimumSize = new Size(kind == ButtonKind.Ghost ? 0 : scale.Px(96), scale.Px(36));
        Margin = scale.Pad(0, 0, 8, 8);
        Cursor = Cursors.Hand;
    }

    public ButtonKind Kind { get; }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var (fill, border, text) = Colors();
        int radius = LogicalToDeviceUnits(5);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = Shapes.RoundedRectangle(bounds, radius))
        {
            if (fill != Color.Transparent)
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
            }
            if (border != Color.Transparent)
            {
                using var pen = new Pen(border, 1);
                g.DrawPath(pen, path);
            }
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        if (RightToLeft == RightToLeft.Yes)
            flags |= TextFormatFlags.RightToLeft;
        TextRenderer.DrawText(g, Text, Font, Rectangle.Inflate(ClientRectangle, -Padding.Horizontal / 4, 0), text, flags);

        if (Focused && ShowFocusCues)
        {
            // Inside the amber button the ring is ink, on the navy header white; elsewhere it uses the accent color.
            int inset = LogicalToDeviceUnits(Kind == ButtonKind.Primary ? 3 : 2);
            using var ring = Shapes.RoundedRectangle(Rectangle.Inflate(bounds, -inset, -inset), Math.Max(1, radius - inset / 2));
            var ringColor = Kind switch { ButtonKind.Primary => Theme.Text, ButtonKind.OnDark => Color.White, _ => Theme.Accent };
            using var pen = new Pen(ringColor, LogicalToDeviceUnits(2));
            g.DrawPath(pen, ring);
        }
    }

    private (Color Fill, Color Border, Color Text) Colors()
    {
        if (!Enabled)
        {
            return Kind == ButtonKind.Primary
                ? (Theme.Sunken, Color.Transparent, Theme.TextTertiary)
                : (Color.Transparent, Kind == ButtonKind.Ghost ? Color.Transparent : Theme.Border, Theme.Disabled);
        }

        return Kind switch
        {
            ButtonKind.Primary => (_pressed ? Theme.ActionPressed : _hover ? Theme.ActionHover : Theme.Action, Color.Transparent, Theme.Text),
            ButtonKind.Secondary => (_pressed ? Theme.Sunken : _hover ? Theme.Surface : Color.White, Theme.BorderStrong, Theme.Text),
            ButtonKind.Ghost => (_pressed ? Theme.AccentSoft : _hover ? Color.FromArgb(0xEE, 0xF2, 0xF7) : Color.Transparent, Color.Transparent, Theme.AccentText),
            _ => (_pressed ? Color.FromArgb(0x2E, 0x52, 0x80) : _hover ? Theme.NavyRaised : Color.Transparent, Color.FromArgb(0x9D, 0xB0, 0xC8), Color.White),
        };
    }
}
