using System.Drawing.Drawing2D;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// A rounded, bordered one-column container. A selectable card behaves like a radio button: focusing it (Tab or a click)
/// selects it, so Enter then confirms the view; screen readers see a radio button with a checked state.
/// </summary>
internal sealed class CardPanel : TableLayoutPanel
{
    private static readonly Color SelectedBackground = Color.FromArgb(0xF3, 0xF6, 0xFA);

    private readonly bool _selectable;
    private bool _selected;

    public CardPanel(UiScale scale, bool selectable = false)
    {
        _selectable = selectable;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, selectable);
        TabStop = selectable;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        BackColor = Theme.Background;
        Padding = scale.Pad(16, 12, 16, 12);
        Margin = scale.Pad(0, 0, 0, 12);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        if (selectable)
        {
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.RadioButton;
        }
    }

    public bool IsSelected => _selected;

    public void SetSelected(bool selected)
    {
        if (_selected == selected)
            return;

        _selected = selected;
        BackColor = selected ? SelectedBackground : Theme.Background;
        Invalidate(true);
        AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        SelectedChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SelectedChanged;

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (_selectable && e.Control is { } child)
            ForwardClicks(child);
    }

    private void ForwardClicks(Control child)
    {
        child.Click += (_, _) => Focus();
        child.Cursor = Cursors.Hand;
        foreach (Control grandChild in child.Controls)
            ForwardClicks(grandChild);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (_selectable)
            Focus();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_selectable)
            SetSelected(true);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int radius = LogicalToDeviceUnits(8);
        int borderWidth = _selected ? LogicalToDeviceUnits(2) : 1;
        var bounds = new Rectangle(borderWidth / 2, borderWidth / 2, Width - borderWidth, Height - borderWidth);
        using (var path = Shapes.RoundedRectangle(bounds, radius))
        {
            using var brush = new SolidBrush(BackColor);
            g.FillPath(brush, path);
            using var pen = new Pen(_selected ? Theme.Accent : Theme.Border, borderWidth);
            g.DrawPath(pen, path);
        }

        if (Focused && ShowFocusCues)
        {
            int inset = LogicalToDeviceUnits(4);
            using var ring = Shapes.RoundedRectangle(Rectangle.Inflate(bounds, -inset, -inset), Math.Max(1, radius - inset));
            using var pen = new Pen(Theme.Accent, 1) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, ring);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new CardAccessibleObject(this);

    private sealed class CardAccessibleObject(CardPanel owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            owner._selectable && owner._selected ? base.State | AccessibleStates.Checked : base.State;
    }
}

internal enum NoteTone { Accent, Success, Warning, Error }

/// <summary>A tinted, rounded note with a leading glyph (the UAC shield for permission notes) and wrapped text.</summary>
internal sealed class NotePanel : TableLayoutPanel
{
    private readonly NoteTone _tone;
    private readonly Label _title;
    private readonly Label _text;
    private readonly Bitmap? _shield;

    public NotePanel(UiScale scale, NoteTone tone)
    {
        _tone = tone;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ColumnCount = 2;
        RowCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Padding = scale.Pad(16, 12, 16, 12);
        Margin = scale.Pad(0, 0, 0, 16);
        BackColor = tone switch
        {
            NoteTone.Success => Theme.SuccessSoft,
            NoteTone.Warning => Theme.WarningSoft,
            NoteTone.Error => Theme.ErrorSoft,
            _ => Theme.AccentSoft,
        };

        if (tone == NoteTone.Accent)
        {
            using var shieldIcon = SystemIcons.GetStockIcon(StockIconId.Shield, StockIconOptions.SmallIcon);
            _shield = shieldIcon.ToBitmap();
        }

        Control icon = _shield is not null
            ? new PictureBox
            {
                Image = _shield,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(scale.Px(20), scale.Px(20)),
                BackColor = Color.Transparent,
            }
            : new GlyphBox(tone switch { NoteTone.Success => GlyphKind.Succeeded, NoteTone.Warning => GlyphKind.Warning, _ => GlyphKind.Failed }, scale.Px(20));
        icon.Margin = scale.Pad(0, 1, 12, 0);
        icon.AccessibleRole = AccessibleRole.Graphic;

        _title = new Label { AutoSize = true, Font = Theme.BodyStrong, ForeColor = Theme.Text, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = scale.Pad(0, 0, 0, 2), UseMnemonic = false, BackColor = Color.Transparent, Visible = false };
        _text = new Label { AutoSize = true, Font = Theme.Body, ForeColor = Theme.Text, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty, UseMnemonic = false, BackColor = Color.Transparent };

        Controls.Add(icon, 0, 0);
        SetRowSpan(icon, 2);
        Controls.Add(_title, 1, 0);
        Controls.Add(_text, 1, 1);
    }

    public void SetText(string text, string? title = null)
    {
        _text.Text = text;
        _title.Text = title ?? "";
        _title.Visible = title is not null;
        AccessibleName = title is null ? text : title + ". " + text;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Shapes.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), LogicalToDeviceUnits(8));
        using var brush = new SolidBrush(BackColor);
        g.FillPath(brush, path);

        // A thin bar on the reading-start edge carries the tone in addition to the glyph.
        var color = _tone switch { NoteTone.Success => Theme.Success, NoteTone.Warning => Theme.WarningGlyph, NoteTone.Error => Theme.Error, _ => Theme.Accent };
        int bar = LogicalToDeviceUnits(4);
        var state = g.Save();
        g.SetClip(path);
        using var barBrush = new SolidBrush(color);
        g.FillRectangle(barBrush, RightToLeft == RightToLeft.Yes ? Width - bar : 0, 0, bar, Height);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _shield?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Two-column "label: value" rows (mirrored automatically in right-to-left layout).</summary>
internal static class InfoTable
{
    public static TableLayoutPanel Create(UiScale scale, IEnumerable<(string Label, string Value)> rows)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (label, value) in rows)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Font = Theme.Small,
                ForeColor = Theme.TextSecondary,
                Margin = scale.Pad(0, 4, 16, 3),
                UseMnemonic = false,
                BackColor = Color.Transparent,
            });
            table.Controls.Add(new Label
            {
                Text = value,
                AutoSize = true,
                Font = Theme.Body,
                ForeColor = Theme.Text,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = scale.Pad(0, 2, 0, 2),
                UseMnemonic = false,
                BackColor = Color.Transparent,
            });
        }

        return table;
    }
}
