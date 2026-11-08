using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.App.Tests.TestTicket;

public sealed class TestTicketLayoutTests
{
    private const double Tolerance = 1e-9;

    private static readonly PaperDimensions Ticket = PaperDimensions.FromUnits(3.24m, 6.70m, LengthUnit.Inch);
    private static readonly TestTicketInfo Info = new("AMWAG Ticket", "TA P-4030DN", "AMWAG Ticket Printer", new DateTimeOffset(2026, 11, 3, 14, 5, 0, TimeSpan.FromHours(2)));

    private static readonly double W = (double)Ticket.WidthMillimeters;
    private static readonly double H = (double)Ticket.HeightMillimeters;

    private static TestTicketDrawing Drawing => TestTicketLayout.Create(Ticket, Info);

    [Fact]
    public void Ticket_is_exactly_82_296_by_170_18_mm()
    {
        Assert.Equal(82_296, Ticket.WidthMicrometers);
        Assert.Equal(170_180, Ticket.HeightMicrometers);
    }

    [Fact]
    public void Everything_lies_inside_the_page()
    {
        var drawing = Drawing;
        var outside = new List<string>();
        void Check(string what, double x, double y)
        {
            if (x < -Tolerance || y < -Tolerance || x > W + Tolerance || y > H + Tolerance)
                outside.Add($"{what} at ({x}, {y})");
        }

        foreach (var line in drawing.Lines)
        {
            Check(line.Element.ToString(), line.From.X, line.From.Y);
            Check(line.Element.ToString(), line.To.X, line.To.Y);
        }
        foreach (var rectangle in drawing.Rectangles)
        {
            Check(rectangle.Element.ToString(), rectangle.Bounds.X, rectangle.Bounds.Y);
            Check(rectangle.Element.ToString(), rectangle.Bounds.Right, rectangle.Bounds.Bottom);
        }
        foreach (var polygon in drawing.Polygons)
        {
            foreach (var point in polygon.Points)
                Check(polygon.Element.ToString(), point.X, point.Y);
        }
        foreach (var text in drawing.Texts)
        {
            Check(text.Text, text.Bounds.X, text.Bounds.Y);
            Check(text.Text, text.Bounds.Right, text.Bounds.Bottom);
        }

        Assert.Empty(outside);
    }

    [Fact]
    public void Boundary_is_the_whole_sheet_and_the_safe_area_is_inset_4_mm()
    {
        var drawing = Drawing;
        var boundary = Assert.Single(drawing.Rectangles, r => r.Element == TicketElement.Boundary);
        Assert.Equal(new RectMm(0, 0, W, H), boundary.Bounds);

        var safe = Assert.Single(drawing.Rectangles, r => r.Element == TicketElement.SafeArea);
        Assert.Equal(StrokeStyle.Dashed, safe.Style);
        Assert.Equal(4, safe.Bounds.X, Tolerance);
        Assert.Equal(4, safe.Bounds.Y, Tolerance);
        Assert.Equal(W - 4, safe.Bounds.Right, Tolerance);
        Assert.Equal(H - 4, safe.Bounds.Bottom, Tolerance);
    }

    [Fact]
    public void Calibration_bars_are_exactly_50_mm_one_horizontal_one_vertical()
    {
        var bars = Drawing.Lines.Where(l => l.Element == TicketElement.CalibrationBar && Math.Abs(Length(l) - 50) < Tolerance).ToList();

        var horizontal = Assert.Single(bars, l => l.From.Y == l.To.Y);
        var vertical = Assert.Single(bars, l => l.From.X == l.To.X);
        Assert.Equal(50.0, Math.Abs(horizontal.To.X - horizontal.From.X));
        Assert.Equal(50.0, Math.Abs(vertical.To.Y - vertical.From.Y));

        Assert.Equal(2, Drawing.Texts.Count(t => t.Element == TicketElement.CalibrationLabel && t.Text == "50 mm"));
    }

    [Fact]
    public void Center_lines_run_through_the_sheet_center_with_ticks_at_the_edge_midpoints()
    {
        var centers = Drawing.Lines.Where(l => l.Element == TicketElement.CenterLine).ToList();
        var horizontal = centers.Where(l => l.From.Y == l.To.Y).ToList();
        var vertical = centers.Where(l => l.From.X == l.To.X).ToList();
        Assert.Equal(centers.Count, horizontal.Count + vertical.Count);
        Assert.All(horizontal, l => Assert.Equal(H / 2, l.From.Y, Tolerance));
        Assert.All(vertical, l => Assert.Equal(W / 2, l.From.X, Tolerance));

        // From edge tick to edge tick, crossing at the exact center.
        double inner = TestTicketLayout.CornerMarkInsetMm + 3;
        Assert.Equal(inner, horizontal.Min(l => Math.Min(l.From.X, l.To.X)), Tolerance);
        Assert.Equal(W - inner, horizontal.Max(l => Math.Max(l.From.X, l.To.X)), Tolerance);
        Assert.Equal(inner, vertical.Min(l => Math.Min(l.From.Y, l.To.Y)), Tolerance);
        Assert.Equal(H - inner, vertical.Max(l => Math.Max(l.From.Y, l.To.Y)), Tolerance);
        Assert.Contains(horizontal, l => l.From.X < W / 2 && W / 2 < l.To.X);
        Assert.Contains(vertical, l => l.From.Y < H / 2 && H / 2 < l.To.Y);

        var tips = Drawing.Polygons.Where(p => p.Element == TicketElement.EdgeMidpointTick).Select(p => p.Points[0]).ToList();
        Assert.Equal(4, tips.Count);
        Assert.Contains(tips, p => Math.Abs(p.X - W / 2) < Tolerance && p.Y < H / 2);
        Assert.Contains(tips, p => Math.Abs(p.X - W / 2) < Tolerance && p.Y > H / 2);
        Assert.Contains(tips, p => Math.Abs(p.Y - H / 2) < Tolerance && p.X < W / 2);
        Assert.Contains(tips, p => Math.Abs(p.Y - H / 2) < Tolerance && p.X > W / 2);
    }

    [Fact]
    public void Center_lines_never_strike_through_text()
    {
        var drawing = Drawing;
        var centers = drawing.Lines.Where(l => l.Element == TicketElement.CenterLine).ToList();
        double clearance = TestTicketLayout.CenterLineClearanceMm - Tolerance;

        foreach (var text in drawing.Texts)
        {
            var b = text.Bounds;
            foreach (var line in centers)
            {
                bool crosses = line.From.X == line.To.X
                    ? line.From.X >= b.X && line.From.X <= b.Right && Math.Max(line.From.Y, line.To.Y) > b.Y - clearance && Math.Min(line.From.Y, line.To.Y) < b.Bottom + clearance
                    : line.From.Y >= b.Y && line.From.Y <= b.Bottom && Math.Max(line.From.X, line.To.X) > b.X - clearance && Math.Min(line.From.X, line.To.X) < b.Right + clearance;
                Assert.False(crosses, $"Center line {line.From}-{line.To} crosses \"{text.Text}\" at {b}.");
            }
        }

        // The text block and the checklist both sit on the vertical center line, so it has gaps there.
        Assert.True(centers.Count(l => l.From.X == l.To.X) >= 3);
    }

    [Fact]
    public void Rulers_tick_every_millimeter_longer_every_5_and_label_every_10()
    {
        var ticks = Drawing.Lines.Where(l => l.Element == TicketElement.RulerTick).ToList();
        var top = ticks.Where(l => l.From.X == l.To.X).ToList();
        var left = ticks.Where(l => l.From.Y == l.To.Y).ToList();

        Assert.Equal(83, top.Count);   // 0..82 mm across 82.296 mm
        Assert.Equal(171, left.Count); // 0..170 mm down 170.18 mm
        Assert.Equal(Enumerable.Range(0, 83).Select(i => (double)i), top.Select(l => l.From.X));
        Assert.Equal(Enumerable.Range(0, 171).Select(i => (double)i), left.Select(l => l.From.Y));

        Assert.Equal(17, top.Count(l => Length(l) > TestTicketLayout.MinorTickMm + Tolerance));  // 0, 5, ... 80
        Assert.Equal(35, left.Count(l => Length(l) > TestTicketLayout.MinorTickMm + Tolerance)); // 0, 5, ... 170
        Assert.Equal(9, top.Count(l => Math.Abs(Length(l) - TestTicketLayout.MajorTickMm) < Tolerance));

        var labels = Drawing.Texts.Where(t => t.Element == TicketElement.RulerLabel).Select(t => t.Text).ToList();
        Assert.Equal(8 + 17, labels.Count);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (i * 10).ToString()), labels.Take(8));
        Assert.Equal(Enumerable.Range(1, 17).Select(i => (i * 10).ToString()), labels.Skip(8));
    }

    [Fact]
    public void Four_L_shaped_corner_marks_with_5_mm_arms()
    {
        var marks = Drawing.Lines.Where(l => l.Element == TicketElement.CornerMark).ToList();
        Assert.Equal(8, marks.Count);
        Assert.All(marks, l => Assert.Equal(TestTicketLayout.CornerMarkArmMm, Length(l), Tolerance));

        double inset = TestTicketLayout.CornerMarkInsetMm;
        var vertices = marks.Select(l => l.From).Distinct().ToList();
        Assert.Equal(4, vertices.Count);
        Assert.Contains(new PointMm(inset, inset), vertices);
        Assert.Contains(new PointMm(W - inset, inset), vertices);
        Assert.Contains(new PointMm(inset, H - inset), vertices);
        Assert.Contains(new PointMm(W - inset, H - inset), vertices);
    }

    [Fact]
    public void Orientation_arrow_points_to_the_top_edge()
    {
        var arrow = Assert.Single(Drawing.Polygons, p => p.Element == TicketElement.OrientationArrow);
        var tip = arrow.Points.MinBy(p => p.Y);
        Assert.Equal(W / 2, tip.X, Tolerance);
        Assert.True(tip.Y < H / 4);
        Assert.Contains(Drawing.Texts, t => t.Element == TicketElement.OrientationArrow && t.Text == "TOP / FEED");
    }

    [Fact]
    public void Text_block_names_the_printer_paper_and_instructions()
    {
        var texts = Drawing.Texts.Where(t => t.Element == TicketElement.Text).Select(t => t.Text).ToList();
        string all = string.Join("\n", texts);

        Assert.Contains("AMWAG Travel", texts);
        Assert.Contains("PRINTER TEST TICKET", texts);
        Assert.Contains("تذكرة اختبار الطابعة", texts);
        Assert.Contains("Model: TA P-4030DN", texts);
        Assert.Contains("Printer: AMWAG Ticket Printer", texts);
        Assert.Contains("Paper: AMWAG Ticket\n82.30 x 170.18 mm (3.24 x 6.70 in)", all);
        Assert.Contains("Printed: 2026-11-03 14:05", texts);
        Assert.Contains("Portrait, 100% scale:\nthe bars must measure exactly 50 mm", all);
        Assert.True(Assert.Single(Drawing.Texts, t => t.Text == "تذكرة اختبار الطابعة").RightToLeft);
    }

    [Fact]
    public void Paper_description_uses_two_decimals_in_both_units()
    {
        Assert.Equal("AMWAG Ticket 82.30 x 170.18 mm (3.24 x 6.70 in)", TicketPaperText.Describe("AMWAG Ticket", Ticket));
        Assert.Equal("82.30 x 170.18 mm (3.24 x 6.70 in)", TicketPaperText.Size(Ticket));
    }

    [Fact]
    public void Refuses_paper_too_small_for_the_layout() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TestTicketLayout.Create(PaperDimensions.FromUnits(50, 100, LengthUnit.Millimeter), Info));

    private static double Length(TicketLine line) => Math.Sqrt(Math.Pow(line.To.X - line.From.X, 2) + Math.Pow(line.To.Y - line.From.Y, 2));
}
