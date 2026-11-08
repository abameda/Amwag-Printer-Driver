using System.Drawing.Drawing2D;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// The six setup steps with a drawn state glyph, a title and a one-line status each. Mirrors itself in right-to-left
/// layout and exposes every step to screen readers as a list item ("Checking driver: Driver is ready").
/// </summary>
internal sealed class StepList : Control
{
    private static readonly StepId[] Steps = Enum.GetValues<StepId>();

    private readonly StepState[] _states = new StepState[Steps.Length];
    // A status text other than the state's own, for example "Not started" for a step a refusal never began.
    private readonly string?[] _statusKeys = new string?[Steps.Length];
    private readonly System.Windows.Forms.Timer _spinner = new() { Interval = 40 };
    private Strings _strings;
    private Font _statusFont;
    private float _angle;

    public StepList(Strings strings)
    {
        _strings = strings;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // Titles use Font (which Windows rescales on DPI changes); the status font is derived from it in OnFontChanged.
        _statusFont = CreateStatusFont();
        Font = Theme.StepTitle;
        BackColor = Theme.Surface;
        TabStop = false;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = strings.Text("steps.accessible");
        _spinner.Tick += (_, _) =>
        {
            _angle = (_angle + 12) % 360;
            Invalidate();
        };
    }

    public StepState StateOf(StepId step) => _states[(int)step];

    /// <summary>The text key a step shows instead of its state's own status text, or null.</summary>
    public string? StatusKeyOf(StepId step) => _statusKeys[(int)step];

    /// <param name="statusKey">A text key shown instead of the state's own status text; null for the state's text.</param>
    public void SetState(StepId step, StepState state, string? statusKey = null)
    {
        if (_states[(int)step] == state && _statusKeys[(int)step] == statusKey)
            return;

        _states[(int)step] = state;
        _statusKeys[(int)step] = statusKey;
        _spinner.Enabled = _states.Contains(StepState.Running);
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.NameChange, (int)step);
    }

    public void Reset()
    {
        Array.Fill(_states, StepState.Pending);
        Array.Fill(_statusKeys, null);
        _spinner.Enabled = false;
        Invalidate();
    }

    public void SetStrings(Strings strings)
    {
        _strings = strings;
        AccessibleName = strings.Text("steps.accessible");
        Invalidate();
    }

    internal string Describe(StepId step) => _strings.StepTitle(step) + ": " + Status((int)step);

    private string Status(int index) => _statusKeys[index] is { } key ? _strings.Text(key) : _strings.StepStatus(Steps[index], _states[index]);

    /// <summary>Bounds of one step's row in client coordinates (already mirrored for right-to-left).</summary>
    internal Rectangle RowBounds(StepId step)
    {
        int top = LogicalToDeviceUnits(24) + (int)step * RowHeight;
        return new Rectangle(0, top, Width, RowHeight);
    }

    private int RowHeight =>
        Math.Max(LogicalToDeviceUnits(GlyphSize), Font.Height + _statusFont.Height + LogicalToDeviceUnits(2)) + LogicalToDeviceUnits(20);

    private Font CreateStatusFont() => new(Theme.StepStatus.FontFamily, Font.SizeInPoints * Theme.StepStatus.SizeInPoints / Theme.StepTitle.SizeInPoints);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        var old = _statusFont;
        _statusFont = CreateStatusFont();
        old.Dispose();
        Invalidate();
    }

    private const int GlyphSize = 22;

    public override Size GetPreferredSize(Size proposedSize) =>
        new(LogicalToDeviceUnits(232), LogicalToDeviceUnits(24) * 2 + RowHeight * Steps.Length);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool rtl = RightToLeft == RightToLeft.Yes;
        int side = LogicalToDeviceUnits(20);
        int glyph = LogicalToDeviceUnits(GlyphSize);
        int gap = LogicalToDeviceUnits(12);
        int glyphX = rtl ? Width - side - glyph : side;

        for (int i = 0; i < Steps.Length; i++)
        {
            var row = RowBounds(Steps[i]);
            var glyphRect = new Rectangle(glyphX, row.Y, glyph, glyph);

            if (i < Steps.Length - 1)
            {
                // Connector to the next step; solid accent once this step is behind us.
                bool done = _states[i] is StepState.Succeeded or StepState.Skipped or StepState.Warning;
                using var pen = new Pen(done ? Theme.Accent : Theme.Border, LogicalToDeviceUnits(2));
                int x = glyphRect.X + glyph / 2;
                g.DrawLine(pen, x, glyphRect.Bottom + LogicalToDeviceUnits(4), x, row.Bottom - LogicalToDeviceUnits(4));
            }

            StatusGlyph.Draw(g, glyphRect, StatusGlyph.For(_states[i]), _angle, rtl);

            int textLeft = rtl ? side : glyphRect.Right + gap;
            int textWidth = Width - side - glyph - gap - side;
            var titleRect = new Rectangle(textLeft, row.Y, textWidth, Font.Height);
            var statusRect = new Rectangle(textLeft, titleRect.Bottom + LogicalToDeviceUnits(2), textWidth, _statusFont.Height);

            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            var titleColor = _states[i] == StepState.Pending ? Theme.TextSecondary : Theme.Text;
            TextRenderer.DrawText(g, _strings.StepTitle(Steps[i]), Font, titleRect, titleColor, flags);
            TextRenderer.DrawText(g, Status(i), _statusFont, statusRect, StatusColor(_states[i]), flags);
        }
    }

    private static Color StatusColor(StepState state) => state switch
    {
        StepState.Running => Theme.AccentText,
        StepState.Succeeded => Theme.Success,
        StepState.Warning => Theme.Warning,
        StepState.Failed => Theme.Error,
        _ => Theme.TextSecondary,
    };

    protected override void OnRightToLeftChanged(EventArgs e)
    {
        base.OnRightToLeftChanged(e);
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new StepListAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinner.Dispose();
            _statusFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class StepListAccessibleObject(StepList owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.List;

        public override int GetChildCount() => Steps.Length;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < Steps.Length ? new StepAccessibleObject(owner, Steps[index], this) : null;
    }

    private sealed class StepAccessibleObject(StepList owner, StepId step, AccessibleObject parent) : AccessibleObject
    {
        public override string? Name => owner.Describe(step);

        public override AccessibleRole Role => AccessibleRole.ListItem;

        public override AccessibleStates State => AccessibleStates.ReadOnly;

        public override AccessibleObject Parent => parent;

        public override Rectangle Bounds => owner.IsHandleCreated ? owner.RectangleToScreen(owner.RowBounds(step)) : Rectangle.Empty;
    }
}
