using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>One filled shape of the logo, in the coordinates of its SVG view box.</summary>
internal sealed record BrandShape(string Id, GraphicsPath Path, Color Fill);

/// <summary>
/// The AMWAG master logo, read from the SVG files in branding\ (embedded in the program) and drawn as vector paths, so it stays
/// sharp at every DPI and is never redrawn by hand. The master files only use absolute M, L, C and Z path commands and translate,
/// scale and matrix transforms; anything else fails loudly rather than drawing a wrong logo.
/// </summary>
internal sealed partial class BrandArt
{
    private static readonly Lazy<BrandArt> s_mark = new(() => Load("Brand.amwag-mark.svg"));
    private static readonly Lazy<BrandArt> s_lockup = new(() => Load("Brand.amwag-logo.svg"));

    private BrandArt(RectangleF viewBox, IReadOnlyList<BrandShape> shapes)
    {
        ViewBox = viewBox;
        Shapes = shapes;
    }

    /// <summary>The mark alone: the sky swoosh under three orange arcs.</summary>
    public static BrandArt Mark => s_mark.Value;

    /// <summary>The mark above the AMWAG wordmark and "Travel".</summary>
    public static BrandArt Lockup => s_lockup.Value;

    public RectangleF ViewBox { get; }

    public IReadOnlyList<BrandShape> Shapes { get; }

    public BrandShape Shape(string id) => Shapes.First(s => s.Id == id);

    /// <summary>Maps the view box into <paramref name="target"/>, keeping the logo's proportions and centring it.</summary>
    public Matrix Fit(RectangleF target)
    {
        float scale = Math.Min(target.Width / ViewBox.Width, target.Height / ViewBox.Height);
        float x = target.X + (target.Width - ViewBox.Width * scale) / 2;
        float y = target.Y + (target.Height - ViewBox.Height * scale) / 2;
        var matrix = new Matrix();
        matrix.Translate(x, y);
        matrix.Scale(scale, scale);
        matrix.Translate(-ViewBox.X, -ViewBox.Y);
        return matrix;
    }

    public void Draw(Graphics g, RectangleF target)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var fit = Fit(target))
            g.MultiplyTransform(fit);
        foreach (var shape in Shapes)
        {
            using var brush = new SolidBrush(shape.Fill);
            g.FillPath(brush, shape.Path);
        }
        g.Restore(state);
    }

    private static BrandArt Load(string resource)
    {
        using var stream = typeof(BrandArt).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The logo resource {resource} is missing.");
        var svg = XElement.Load(stream);
        float[] box = Numbers(svg.Attribute("viewBox")?.Value ?? throw new InvalidDataException($"{resource} has no viewBox."));
        var shapes = new List<BrandShape>();
        using (var identity = new Matrix())
            Collect(svg, identity, shapes);
        return new BrandArt(new RectangleF(box[0], box[1], box[2], box[3]), shapes);
    }

    private static void Collect(XElement parent, Matrix parentTransform, List<BrandShape> shapes)
    {
        foreach (var element in parent.Elements())
        {
            if (element.Name.LocalName is not ("g" or "path"))
                continue;

            using var transform = parentTransform.Clone();
            if (element.Attribute("transform")?.Value is { } text)
                using (var own = Transform(text))
                    transform.Multiply(own, MatrixOrder.Prepend);

            if (element.Name.LocalName == "g")
            {
                Collect(element, transform, shapes);
                continue;
            }

            var path = ParsePath(element.Attribute("d")?.Value ?? "", element.Attribute("fill-rule")?.Value == "evenodd" ? FillMode.Alternate : FillMode.Winding);
            path.Transform(transform);
            string id = element.Attribute("id")?.Value ?? $"path{shapes.Count}";
            shapes.Add(new BrandShape(id, path, ColorTranslator.FromHtml(element.Attribute("fill")?.Value ?? "#000000")));
        }
    }

    private static Matrix Transform(string text)
    {
        var result = new Matrix();
        foreach (Match op in TransformOp().Matches(text))
        {
            float[] v = Numbers(op.Groups[2].Value);
            using var step = op.Groups[1].Value switch
            {
                "matrix" when v.Length == 6 => new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]),
                "translate" => new Matrix(1, 0, 0, 1, v[0], v.Length > 1 ? v[1] : 0),
                "scale" => new Matrix(v[0], 0, 0, v.Length > 1 ? v[1] : v[0], 0, 0),
                _ => throw new InvalidDataException($"Unsupported logo transform '{op.Value}'."),
            };
            result.Multiply(step, MatrixOrder.Prepend);
        }
        return result;
    }

    /// <param name="fill">Winding is SVG's default nonzero rule; Alternate is evenodd.</param>
    private static GraphicsPath ParsePath(string data, FillMode fill)
    {
        var path = new GraphicsPath(fill);
        var tokens = PathToken().Matches(data).Select(m => m.Value).ToArray();
        char command = 'M';
        var current = PointF.Empty;
        int i = 0;
        float Next() => float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        PointF NextPoint() => new(Next(), Next());

        while (i < tokens.Length)
        {
            if (char.IsLetter(tokens[i][0]))
                command = tokens[i++][0];

            switch (command)
            {
                case 'M':
                    path.StartFigure();
                    current = NextPoint();
                    command = 'L'; // Further pairs after a move are lines.
                    break;
                case 'L':
                    var end = NextPoint();
                    path.AddLine(current, end);
                    current = end;
                    break;
                case 'C':
                    PointF c1 = NextPoint(), c2 = NextPoint(), to = NextPoint();
                    path.AddBezier(current, c1, c2, to);
                    current = to;
                    break;
                case 'Z':
                    path.CloseFigure();
                    if (i < tokens.Length && !char.IsLetter(tokens[i][0]))
                        throw new InvalidDataException("A logo path has numbers after Z.");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported logo path command '{command}'.");
            }
        }
        return path;
    }

    private static float[] Numbers(string text) =>
        Number().Matches(text).Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();

    [GeneratedRegex(@"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex PathToken();

    [GeneratedRegex(@"-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"(\w+)\s*\(([^)]*)\)")]
    private static partial Regex TransformOp();
}
