namespace Amwag.PrinterSetup.App.UI;

/// <summary>The AMWAG mark in the navy header, drawn from the master logo (<see cref="BrandArt.Mark"/>) at the window's DPI.</summary>
internal sealed class HeaderMark : Control
{
    public HeaderMark(Size size)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = size;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "AMWAG";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Navy);
        // The logo is never mirrored, also not in the right-to-left layout.
        BrandArt.Mark.Draw(e.Graphics, ClientRectangle);
    }
}
