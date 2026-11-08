using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>The line at the bottom of the window: the copyright at the reading start, the developer credit at the end.</summary>
internal sealed class FooterBar : TableLayoutPanel
{
    private readonly Label _copyright;
    private readonly Label _developedBy;
    private readonly CreditLink _developer;

    public FooterBar(UiScale s, ILog log)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Surface;
        ColumnCount = 2;
        RowCount = 1;
        Height = s.Px(36);
        Margin = Padding.Empty;
        Padding = s.Pad(24, 1, 24, 0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _copyright = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Font = Theme.Small, ForeColor = Theme.TextSecondary, Margin = Padding.Empty, UseMnemonic = false };
        _developedBy = new Label { AutoSize = true, Anchor = AnchorStyles.None, Font = Theme.Small, ForeColor = Theme.TextSecondary, Margin = Padding.Empty, UseMnemonic = false };
        _developer = new CreditLink(log) { Anchor = AnchorStyles.None, Margin = Padding.Empty };

        var credit = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.None, Margin = s.Pad(16, 0, 0, 0), Padding = Padding.Empty };
        credit.Controls.Add(_developedBy);
        credit.Controls.Add(_developer);
        Controls.Add(_copyright, 0, 0);
        Controls.Add(credit, 1, 0);
    }

    public void SetStrings(Strings strings)
    {
        _copyright.Text = strings.Text("footer.copyright");
        _developedBy.Text = strings.Text("footer.developedBy");
        _developer.Text = strings.Text("footer.developer");
        _developer.AccessibleName = strings.Format("footer.developer.accessible", CreditLink.Address);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border, 1);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

/// <summary>
/// The developer's name as a link to their site: bold navy text with an outbound arrow and no underline; hovering tints it,
/// and keyboard focus draws an outline. Enter, Space or a click opens the site in the default browser.
/// </summary>
internal sealed class CreditLink : Control
{
    public const string Address = "www.abdelhmeed.com";
    private const string Url = "https://www.abdelhmeed.com";

    private readonly ILog _log;
    private bool _hover;

    /// <remarks>Sizes come from the control's current DPI, so they follow the window to a monitor with another DPI.</remarks>
    public CreditLink(ILog log)
    {
        _log = log;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.SmallStrong;
        ForeColor = Theme.AccentText;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Link;
        AccessibleDefaultActionDescription = "Open";
    }

    private int ArrowSize => LogicalToDeviceUnits(10);
    private int Gap => LogicalToDeviceUnits(4);
    private Padding Inner => new(LogicalToDeviceUnits(6), LogicalToDeviceUnits(3), LogicalToDeviceUnits(6), LogicalToDeviceUnits(3));

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size(text.Width + Gap + ArrowSize + Inner.Horizontal, Math.Max(text.Height, ArrowSize) + Inner.Vertical);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnRightToLeftChanged(EventArgs e) { Invalidate(); base.OnRightToLeftChanged(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Open();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.Handled = true;
            Open();
        }
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    private void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true })?.Dispose();
            _log.Info("ui.credit.opened", "The developer's site was opened.", new Dictionary<string, object?> { ["url"] = Url });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Warn("ui.credit.failed", "The developer's site could not be opened.", exception: ex);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        int radius = LogicalToDeviceUnits(5);

        if (_hover)
        {
            using var tint = Shapes.RoundedRectangle(bounds, radius);
            using var brush = new SolidBrush(Theme.AccentSoft);
            g.FillPath(brush, tint);
        }

        bool rtl = RightToLeft == RightToLeft.Yes;
        var content = new Rectangle(Inner.Left, Inner.Top, Width - Inner.Horizontal, Height - Inner.Vertical);
        int textWidth = content.Width - Gap - ArrowSize;
        var textBox = new Rectangle(rtl ? content.Right - textWidth : content.Left, content.Top, textWidth, content.Height);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        if (rtl)
            flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;
        TextRenderer.DrawText(g, Text, Font, textBox, ForeColor, flags);

        // The outbound arrow points up and away from the text: up-right in English, up-left in Arabic.
        float a = ArrowSize, x = rtl ? content.Left : content.Right - a, y = content.Top + (content.Height - a) / 2f;
        float inset = a * 0.15f;
        using (var pen = new Pen(ForeColor, Math.Max(1.5f, DeviceDpi / 96f * 1.7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float tipX = rtl ? x + inset : x + a - inset, tailX = rtl ? x + a - inset : x + inset;
            float top = y + inset, bottom = y + a - inset;
            g.DrawLine(pen, tailX, bottom, tipX, top);
            g.DrawLines(pen, [new PointF(rtl ? tipX + (a - 2 * inset) * 0.75f : tipX - (a - 2 * inset) * 0.75f, top), new PointF(tipX, top), new PointF(tipX, top + (a - 2 * inset) * 0.75f)]);
        }

        if (Focused && ShowFocusCues)
        {
            int edge = LogicalToDeviceUnits(1);
            using var ring = Shapes.RoundedRectangle(Rectangle.Inflate(bounds, -edge, -edge), radius);
            using var pen = new Pen(Theme.Accent, LogicalToDeviceUnits(2));
            g.DrawPath(pen, ring);
        }
    }
}
