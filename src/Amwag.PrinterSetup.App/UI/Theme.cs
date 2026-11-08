namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// Colors and fonts of the AMWAG Travel identity. Text colors keep at least 4.5:1 contrast on their backgrounds (WCAG AA).
/// Navy carries the brand, selection and progress; amber is only the fill of the one main action, always with ink text
/// (amber as text or a thin line on white is 1.8:1).
/// </summary>
internal static class Theme
{
    public static readonly Color Navy = Color.FromArgb(0x0F, 0x2A, 0x4A);
    public static readonly Color NavyRaised = Color.FromArgb(0x22, 0x49, 0x7A);
    public static readonly Color OnNavySecondary = Color.FromArgb(0xC9, 0xD5, 0xE6);
    public static readonly Color Accent = Navy;
    public static readonly Color AccentText = Navy;
    public static readonly Color AccentSoft = Color.FromArgb(0xE3, 0xEA, 0xF2);
    public static readonly Color Action = Color.FromArgb(0xF5, 0xB6, 0x42);
    public static readonly Color ActionHover = Color.FromArgb(0xE8, 0xA5, 0x2E);
    public static readonly Color ActionPressed = Color.FromArgb(0xD6, 0x94, 0x22);
    public static readonly Color Background = Color.White;
    public static readonly Color Surface = Color.FromArgb(0xF2, 0xF4, 0xF5);
    public static readonly Color Sunken = Color.FromArgb(0xE8, 0xEC, 0xEF);
    public static readonly Color Text = Color.FromArgb(0x15, 0x19, 0x1D);
    public static readonly Color TextSecondary = Color.FromArgb(0x56, 0x61, 0x6B);
    public static readonly Color TextTertiary = Color.FromArgb(0x5F, 0x6A, 0x75);
    public static readonly Color Border = Color.FromArgb(0xDC, 0xE1, 0xE5);
    public static readonly Color BorderStrong = Color.FromArgb(0x7B, 0x87, 0x92);
    public static readonly Color Disabled = Color.FromArgb(0xB4, 0xBE, 0xC8);
    public static readonly Color Neutral = BorderStrong;
    public static readonly Color Success = Color.FromArgb(0x2E, 0x7D, 0x32);
    public static readonly Color SuccessSoft = Color.FromArgb(0xE8, 0xF4, 0xEA);
    public static readonly Color Warning = Color.FromArgb(0x9A, 0x5B, 0x00);
    public static readonly Color WarningGlyph = Color.FromArgb(0xE0, 0x8A, 0x00);
    public static readonly Color WarningSoft = Color.FromArgb(0xFF, 0xF4, 0xDF);
    public static readonly Color Error = Color.FromArgb(0xC6, 0x28, 0x28);
    public static readonly Color ErrorSoft = Color.FromArgb(0xFD, 0xEC, 0xEA);

    private const string Family = "Segoe UI";
    private const string SemiboldFamily = "Segoe UI Semibold";

    public static readonly Font HeaderTitle = new(SemiboldFamily, 15f);
    public static readonly Font HeaderSubtitle = new(Family, 9.75f);
    public static readonly Font ViewTitle = new(SemiboldFamily, 14.25f);
    public static readonly Font Body = new(Family, 10f);
    public static readonly Font BodyStrong = new(SemiboldFamily, 10f);
    public static readonly Font Small = new(Family, 9f);
    public static readonly Font SmallStrong = new(SemiboldFamily, 9f);
    public static readonly Font StepTitle = new(SemiboldFamily, 9.75f);
    public static readonly Font StepStatus = new(Family, 8.75f);
    public static readonly Font Mono = new("Consolas", 9f);
}

internal static class Shapes
{
    public static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Logical (96 DPI) to device pixels for the window's DPI. All pixel sizes are scaled here; fonts are in points and
/// scale by themselves. Windows' own per-monitor rescaling then handles later DPI changes.
/// </summary>
internal readonly record struct UiScale(float Factor)
{
    public static UiScale For(Control control) => new(control.DeviceDpi / 96f);

    public int Px(int logical) => (int)Math.Round(logical * Factor);

    public Padding Pad(int all) => new(Px(all));

    public Padding Pad(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));
}

/// <summary>Small factories so every view uses the same typography and 8 px spacing grid.</summary>
internal static class Ui
{
    public static Label Heading(UiScale s) => new()
    {
        AutoSize = true,
        Font = Theme.ViewTitle,
        ForeColor = Theme.Text,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = s.Pad(0, 0, 0, 8),
        UseMnemonic = false,
    };

    public static Label Paragraph(UiScale s, Font? font = null, Color? color = null, int bottom = 16) => new()
    {
        AutoSize = true,
        Font = font ?? Theme.Body,
        ForeColor = color ?? Theme.TextSecondary,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = s.Pad(0, 0, 0, bottom),
        UseMnemonic = false,
    };

    /// <summary>A one-column table that stacks rows top to bottom and wraps its labels to the column width.</summary>
    public static TableLayoutPanel Stack(DockStyle dock = DockStyle.Top)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = dock,
            AutoSize = dock != DockStyle.Fill,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    /// <summary>A spinner at the reading start with the text it belongs to beside it.</summary>
    public static TableLayoutPanel ProgressRow(UiScale s, Spinner spinner, Control text, int bottom = 16)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = s.Pad(0, 0, 0, bottom),
            Padding = Padding.Empty,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(spinner, 0, 0);
        row.Controls.Add(text, 1, 0);
        return row;
    }

    /// <summary>Buttons flowing from the reading start (left in English, right in Arabic); wraps when the text is long.</summary>
    public static FlowLayoutPanel ButtonRow(UiScale s) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = s.Pad(0, 8, 0, 0),
        Padding = Padding.Empty,
    };
}
