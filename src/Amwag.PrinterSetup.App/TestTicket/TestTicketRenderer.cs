using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Amwag.PrinterSetup.App.TestTicket;

/// <summary>Draws a <see cref="TestTicketDrawing"/> on a Graphics whose page unit is millimeters and whose origin is the sheet corner.</summary>
public static class TestTicketRenderer
{
    private const string FontFamily = "Segoe UI";

    public static void Draw(Graphics g, TestTicketDrawing drawing)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        foreach (var rectangle in drawing.Rectangles)
        {
            using var pen = CreatePen(rectangle.WidthMm, rectangle.Style == StrokeStyle.Dashed ? [3f, 2f] : null);
            var b = rectangle.Bounds;
            g.DrawRectangle(pen, (float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);
        }

        foreach (var line in drawing.Lines)
        {
            using var pen = CreatePen(line.WidthMm, null);
            g.DrawLine(pen, (float)line.From.X, (float)line.From.Y, (float)line.To.X, (float)line.To.Y);
        }

        foreach (var polygon in drawing.Polygons)
            g.FillPolygon(Brushes.Black, polygon.Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray());

        foreach (var text in drawing.Texts)
            DrawText(g, text);
    }

    /// <summary>The area the driver says it can print (PageSettings.PrintableArea, in millimeters), drawn with a short dash and labelled.</summary>
    public static void DrawPrintableArea(Graphics g, RectangleF areaMm)
    {
        using var pen = CreatePen(0.2, [1f, 1f]);
        g.DrawRectangle(pen, areaMm.X, areaMm.Y, areaMm.Width, areaMm.Height);
        DrawText(g, new TicketText(TicketElement.Text, "Printable area (driver)", new RectMm(areaMm.X + 10, areaMm.Y + 0.5, 30, 2.4), 4.5f, false, TicketTextAlignment.Start));
    }

    private static Pen CreatePen(double widthMm, float[]? dashPattern)
    {
        var pen = new Pen(Color.Black, (float)widthMm);
        if (dashPattern is not null)
            pen.DashPattern = dashPattern;
        return pen;
    }

    private static void DrawText(Graphics g, TicketText text)
    {
        using var font = new Font(FontFamily, text.SizePoints, text.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = text.Alignment switch
            {
                TicketTextAlignment.Center => StringAlignment.Center,
                TicketTextAlignment.End => StringAlignment.Far,
                _ => StringAlignment.Near,
            },
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        if (text.RightToLeft)
            format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;

        var box = new RectangleF((float)text.Bounds.X, (float)text.Bounds.Y, (float)text.Bounds.Width, (float)text.Bounds.Height);

        // Blank out lines running underneath (center lines), but only behind the glyphs, not the whole box.
        SizeF size = g.MeasureString(text.Text, font, box.Size, format);
        float width = Math.Min(size.Width, box.Width);
        float x = format.Alignment switch
        {
            StringAlignment.Center => box.X + (box.Width - width) / 2,
            StringAlignment.Far => text.RightToLeft ? box.X : box.Right - width,
            _ => text.RightToLeft ? box.Right - width : box.X,
        };
        float height = Math.Min(size.Height, box.Height);
        g.FillRectangle(Brushes.White, x, box.Y + (box.Height - height) / 2, width, height);
        g.DrawString(text.Text, font, Brushes.Black, box, format);
    }
}
