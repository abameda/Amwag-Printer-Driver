using System.Globalization;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.App.TestTicket;

public readonly record struct PointMm(double X, double Y);

public readonly record struct RectMm(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public enum TicketElement
{
    Boundary,
    CornerMark,
    SafeArea,
    CenterLine,
    EdgeMidpointTick,
    RulerTick,
    RulerLabel,
    CalibrationBar,
    CalibrationLabel,
    OrientationArrow,
    Text,
}

public enum StrokeStyle { Solid, Dashed }

public enum TicketTextAlignment { Start, Center, End }

public sealed record TicketLine(TicketElement Element, PointMm From, PointMm To, double WidthMm);

public sealed record TicketRectangle(TicketElement Element, RectMm Bounds, double WidthMm, StrokeStyle Style);

/// <summary>A filled polygon (arrow heads).</summary>
public sealed record TicketPolygon(TicketElement Element, IReadOnlyList<PointMm> Points);

/// <summary>One line of text, kept on one line (ellipsis when too long) inside <see cref="Bounds"/>.</summary>
public sealed record TicketText(TicketElement Element, string Text, RectMm Bounds, float SizePoints, bool Bold, TicketTextAlignment Alignment, bool RightToLeft = false);

public sealed record TestTicketDrawing(
    PaperDimensions Paper,
    IReadOnlyList<TicketLine> Lines,
    IReadOnlyList<TicketRectangle> Rectangles,
    IReadOnlyList<TicketPolygon> Polygons,
    IReadOnlyList<TicketText> Texts);

/// <summary>What the ticket says about the printer it was printed on.</summary>
public sealed record TestTicketInfo(string FormName, string ModelName, string PrinterName, DateTimeOffset PrintedAt);

/// <summary>
/// The test ticket as drawing primitives in millimeters from the physical top-left corner of the sheet.
/// Marks that must be visible sit at least 6 mm from the edges because laser printers cannot print the outer ~4.2 mm;
/// the outer boundary and the first ruler ticks fall into that margin on purpose, so what is missing shows the real margin.
/// </summary>
public static class TestTicketLayout
{
    public const double CalibrationLengthMm = 50;
    public const double SafeAreaInsetMm = 4;
    public const double CornerMarkInsetMm = 6;
    public const double CornerMarkArmMm = 5;
    public const double RulerBaselineMm = 14;
    public const double MinorTickMm = 1.5;
    public const double MidTickMm = 2.5;
    public const double MajorTickMm = 3.5;
    public const double MinimumWidthMm = 75;
    public const double MinimumHeightMm = 150;

    /// <summary>Space the center lines leave between themselves and any text they would cross.</summary>
    public const double CenterLineClearanceMm = 1;

    private const double MinimumSegmentMm = 0.5;
    private const double EdgeTickDepthMm = 3;
    private const double EdgeTickHalfWidthMm = 1.5;
    private const double ContentLeftMm = 20;
    private const double ContentRightInsetMm = 8;
    private const double RulerLabelGapMm = 0.6;
    private const double RulerLabelHeightMm = 2.6;
    private const float RulerLabelPoints = 5f;
    private const float BodyPoints = 6.5f;
    private const double BodyLineMm = 3.6;

    public static TestTicketDrawing Create(PaperDimensions paper, TestTicketInfo info)
    {
        double w = (double)paper.WidthMillimeters;
        double h = (double)paper.HeightMillimeters;
        if (w < MinimumWidthMm || h < MinimumHeightMm)
            throw new ArgumentOutOfRangeException(nameof(paper), paper, $"The test ticket needs at least {MinimumWidthMm} x {MinimumHeightMm} mm.");

        var lines = new List<TicketLine>();
        var rectangles = new List<TicketRectangle>();
        var polygons = new List<TicketPolygon>();
        var texts = new List<TicketText>();

        rectangles.Add(new(TicketElement.Boundary, new RectMm(0, 0, w, h), 0.3, StrokeStyle.Solid));
        rectangles.Add(new(TicketElement.SafeArea, new RectMm(SafeAreaInsetMm, SafeAreaInsetMm, w - 2 * SafeAreaInsetMm, h - 2 * SafeAreaInsetMm), 0.2, StrokeStyle.Dashed));
        AddCornerMarks(lines, w, h);
        AddRulers(lines, texts, w, h);
        AddCalibrationBars(lines, texts, w, h);
        AddOrientationArrow(polygons, texts, w);
        AddTextBlock(texts, paper, info, w);
        AddChecklist(texts, w, h);
        AddCenterLines(lines, polygons, texts, w, h);

        return new TestTicketDrawing(paper, lines, rectangles, polygons, texts);
    }

    private static void AddCornerMarks(List<TicketLine> lines, double w, double h)
    {
        // Each L has its vertex nearest to the sheet corner and both arms pointing along the edges.
        foreach (var (x, y, dx, dy) in new[] { (CornerMarkInsetMm, CornerMarkInsetMm, 1, 1), (w - CornerMarkInsetMm, CornerMarkInsetMm, -1, 1), (CornerMarkInsetMm, h - CornerMarkInsetMm, 1, -1), (w - CornerMarkInsetMm, h - CornerMarkInsetMm, -1, -1) })
        {
            lines.Add(new(TicketElement.CornerMark, new(x, y), new(x + dx * CornerMarkArmMm, y), 0.4));
            lines.Add(new(TicketElement.CornerMark, new(x, y), new(x, y + dy * CornerMarkArmMm), 0.4));
        }
    }

    /// <summary>
    /// The crosshair through the sheet center, from edge tick to edge tick. It stops <see cref="CenterLineClearanceMm"/>
    /// before any text it would cross and continues after it, so it never strikes through the text block yet still lines up
    /// with the edge midpoints and crosses at the exact center.
    /// </summary>
    private static void AddCenterLines(List<TicketLine> lines, List<TicketPolygon> polygons, List<TicketText> texts, double w, double h)
    {
        double inner = CornerMarkInsetMm + EdgeTickDepthMm;
        double cx = w / 2;
        double cy = h / 2;

        var acrossGaps = texts.Where(t => t.Bounds.Y <= cy && cy <= t.Bounds.Bottom).Select(t => (t.Bounds.X, t.Bounds.Right));
        foreach (var (from, to) in Segments(inner, w - inner, acrossGaps))
            lines.Add(new(TicketElement.CenterLine, new(from, cy), new(to, cy), 0.15));

        var downGaps = texts.Where(t => t.Bounds.X <= cx && cx <= t.Bounds.Right).Select(t => (t.Bounds.Y, t.Bounds.Bottom));
        foreach (var (from, to) in Segments(inner, h - inner, downGaps))
            lines.Add(new(TicketElement.CenterLine, new(cx, from), new(cx, to), 0.15));

        // Triangles whose tips point at the four edge midpoints.
        double tip = CornerMarkInsetMm;
        polygons.Add(new(TicketElement.EdgeMidpointTick, [new(w / 2, tip), new(w / 2 + EdgeTickHalfWidthMm, inner), new(w / 2 - EdgeTickHalfWidthMm, inner)]));
        polygons.Add(new(TicketElement.EdgeMidpointTick, [new(w / 2, h - tip), new(w / 2 - EdgeTickHalfWidthMm, h - inner), new(w / 2 + EdgeTickHalfWidthMm, h - inner)]));
        polygons.Add(new(TicketElement.EdgeMidpointTick, [new(tip, h / 2), new(inner, h / 2 - EdgeTickHalfWidthMm), new(inner, h / 2 + EdgeTickHalfWidthMm)]));
        polygons.Add(new(TicketElement.EdgeMidpointTick, [new(w - tip, h / 2), new(w - inner, h / 2 + EdgeTickHalfWidthMm), new(w - inner, h / 2 - EdgeTickHalfWidthMm)]));
    }

    /// <summary>The parts of [from, to] outside the given intervals, each widened by the clearance; slivers are dropped.</summary>
    private static List<(double From, double To)> Segments(double from, double to, IEnumerable<(double Start, double End)> gaps)
    {
        var segments = new List<(double From, double To)>();
        double position = from;
        foreach (var (start, end) in gaps.Select(g => (Start: g.Start - CenterLineClearanceMm, End: g.End + CenterLineClearanceMm)).OrderBy(g => g.Start))
        {
            if (end <= position)
                continue;
            if (start - position >= MinimumSegmentMm)
                segments.Add((position, Math.Min(start, to)));
            position = Math.Max(position, end);
            if (position >= to)
                break;
        }

        if (to - position >= MinimumSegmentMm)
            segments.Add((position, to));
        return segments;
    }

    private static void AddRulers(List<TicketLine> lines, List<TicketText> texts, double w, double h)
    {
        // Ticks mark absolute distances from the sheet edge and run from the baseline toward that edge.
        for (int mm = 0; mm <= (int)Math.Floor(w); mm++)
        {
            lines.Add(new(TicketElement.RulerTick, new(mm, RulerBaselineMm - TickLength(mm)), new(mm, RulerBaselineMm), 0.12));
            if (mm > 0 && mm % 10 == 0)
            {
                const double labelWidth = 4.4;
                double x = Math.Clamp(mm - labelWidth / 2, 0, w - labelWidth);
                texts.Add(new(TicketElement.RulerLabel, Number(mm), new RectMm(x, RulerBaselineMm + RulerLabelGapMm, labelWidth, RulerLabelHeightMm), RulerLabelPoints, false, TicketTextAlignment.Center));
            }
        }

        for (int mm = 0; mm <= (int)Math.Floor(h); mm++)
        {
            lines.Add(new(TicketElement.RulerTick, new(RulerBaselineMm - TickLength(mm), mm), new(RulerBaselineMm, mm), 0.12));
            if (mm > 0 && mm % 10 == 0)
            {
                double y = Math.Clamp(mm - RulerLabelHeightMm / 2, 0, h - RulerLabelHeightMm);
                texts.Add(new(TicketElement.RulerLabel, Number(mm), new RectMm(RulerBaselineMm + RulerLabelGapMm, y, 4.8, RulerLabelHeightMm), RulerLabelPoints, false, TicketTextAlignment.Start));
            }
        }
    }

    private static double TickLength(int mm) => mm % 10 == 0 ? MajorTickMm : mm % 5 == 0 ? MidTickMm : MinorTickMm;

    private static void AddCalibrationBars(List<TicketLine> lines, List<TicketText> texts, double w, double h)
    {
        const double cap = 2;
        double y = h / 2 + 10;
        double x0 = ContentLeftMm;
        lines.Add(new(TicketElement.CalibrationBar, new(x0, y), new(x0 + CalibrationLengthMm, y), 0.8));
        lines.Add(new(TicketElement.CalibrationBar, new(x0, y - cap), new(x0, y + cap), 0.25));
        lines.Add(new(TicketElement.CalibrationBar, new(x0 + CalibrationLengthMm, y - cap), new(x0 + CalibrationLengthMm, y + cap), 0.25));
        texts.Add(new(TicketElement.CalibrationLabel, "50 mm", new RectMm(x0, y - cap - 3.6, CalibrationLengthMm, 3.2), 6.5f, true, TicketTextAlignment.Center));

        double x = w - 14;
        // Whole millimeters keep both ends exact in floating point, so the bar is exactly 50 mm long.
        double y0 = Math.Floor(h - 20 - CalibrationLengthMm);
        lines.Add(new(TicketElement.CalibrationBar, new(x, y0), new(x, y0 + CalibrationLengthMm), 0.8));
        lines.Add(new(TicketElement.CalibrationBar, new(x - cap, y0), new(x + cap, y0), 0.25));
        lines.Add(new(TicketElement.CalibrationBar, new(x - cap, y0 + CalibrationLengthMm), new(x + cap, y0 + CalibrationLengthMm), 0.25));
        texts.Add(new(TicketElement.CalibrationLabel, "50 mm", new RectMm(x - cap - 14, y0 + CalibrationLengthMm / 2 - 1.6, 13, 3.2), 6.5f, true, TicketTextAlignment.End));
    }

    private static void AddOrientationArrow(List<TicketPolygon> polygons, List<TicketText> texts, double w)
    {
        double cx = w / 2;
        polygons.Add(new(TicketElement.OrientationArrow,
        [
            new(cx, 19), new(cx + 2.5, 23), new(cx + 0.8, 23), new(cx + 0.8, 28),
            new(cx - 0.8, 28), new(cx - 0.8, 23), new(cx - 2.5, 23),
        ]));
        texts.Add(new(TicketElement.OrientationArrow, "TOP / FEED", new RectMm(cx + 4, 20.2, w - ContentRightInsetMm - cx - 4, 3.6), 7f, true, TicketTextAlignment.Start));
    }

    private static void AddTextBlock(List<TicketText> texts, PaperDimensions paper, TestTicketInfo info, double w)
    {
        double width = w - ContentRightInsetMm - ContentLeftMm;
        double y = 31;

        void Add(string text, float points, bool bold, double height, TicketTextAlignment alignment = TicketTextAlignment.Start, bool rightToLeft = false)
        {
            texts.Add(new(TicketElement.Text, text, new RectMm(ContentLeftMm, y, width, height), points, bold, alignment, rightToLeft));
            y += height;
        }

        Add("AMWAG Travel", 11f, true, 5.6, TicketTextAlignment.Center);
        Add("PRINTER TEST TICKET", 8f, true, 4.4, TicketTextAlignment.Center);
        Add("تذكرة اختبار الطابعة", 8.5f, true, 5.0, TicketTextAlignment.Center, rightToLeft: true);
        y += 1.4;
        Add("Model: " + info.ModelName, BodyPoints, false, BodyLineMm);
        Add("Printer: " + info.PrinterName, BodyPoints, false, BodyLineMm);
        Add("Paper: " + info.FormName, BodyPoints, false, BodyLineMm);
        Add(TicketPaperText.Size(paper), BodyPoints, false, BodyLineMm);
        Add("Printed: " + info.PrintedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), BodyPoints, false, BodyLineMm);
        y += 1.0;
        Add("Portrait, 100% scale:", BodyPoints, true, BodyLineMm);
        Add("the bars must measure exactly 50 mm", BodyPoints, true, BodyLineMm);
    }

    private static void AddChecklist(List<TicketText> texts, double w, double h)
    {
        string[] items =
        [
            "Check:",
            "1  All four corner marks printed",
            "2  Both bars measure 50 mm",
            "3  Text upright, nothing cut off",
            "4  Fed from the multipurpose tray",
        ];

        double y = h / 2 + 17;
        double width = w - 14 - 2 - 4 - ContentLeftMm;
        for (int i = 0; i < items.Length; i++)
        {
            texts.Add(new(TicketElement.Text, items[i], new RectMm(ContentLeftMm, y, width, 3.3), 6f, i == 0, TicketTextAlignment.Start));
            y += 3.3;
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public static class TicketPaperText
{
    /// <summary>"82.30 x 170.18 mm (3.24 x 6.70 in)".</summary>
    public static string Size(PaperDimensions size) =>
        FormattableString.Invariant($"{size.WidthMillimeters:0.00} x {size.HeightMillimeters:0.00} mm ({size.WidthInches:0.00} x {size.HeightInches:0.00} in)");

    /// <summary>"AMWAG Ticket 82.30 x 170.18 mm (3.24 x 6.70 in)".</summary>
    public static string Describe(string formName, PaperDimensions size) => formName + " " + Size(size);
}
