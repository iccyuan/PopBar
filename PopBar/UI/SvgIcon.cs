using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace PopBar.UI;

/// <summary>
/// One shape of an icon in the 24x24 icon space: filled, stroked (<see cref="Stroke"/> = width in icon units), or both.
/// </summary>
internal sealed record IconPart(Geometry Geometry, bool Fill, double Stroke, PenLineCap Cap = PenLineCap.Round, PenLineJoin Join = PenLineJoin.Round);

/// <summary>
/// Reads the shapes of a small SVG icon (a plugin's own icon) into <see cref="IconPart"/>s scaled to the 24x24 icon
/// space. Only the outline is kept: every paint counts as "drawn" and the icon is tinted like PopBar's own, so it
/// follows the theme. Handles what icon sets use: path, circle, ellipse, rect, line, polyline, polygon, nested g,
/// fill / stroke / stroke-width / fill-rule / linecap / linejoin (as attributes or inline style), and transforms.
/// </summary>
internal static partial class SvgIcon
{
    /// <summary>Does this icon value look like SVG markup rather than a name?</summary>
    public static bool IsMarkup(string value) => value.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);

    private sealed record Style(bool Fill, bool Stroke, double Width, FillRule Rule, PenLineCap Cap, PenLineJoin Join, Matrix Transform);

    /// <exception cref="FormatException">Not an SVG this can draw (no shapes, broken markup or path data).</exception>
    public static IconPart[] Parse(string svg)
    {
        XElement root;
        try { root = XElement.Parse(svg.Trim()); }
        catch (Exception ex) { throw new FormatException("SVG 无法解析：" + ex.Message, ex); }
        if (root.Name.LocalName != "svg") throw new FormatException("不是 <svg> 元素");

        // viewBox (else width / height, else 24) mapped onto 24x24, uniformly and centred
        var box = Numbers(Attr(root, "viewBox"));
        double vx = 0, vy = 0, vw = Number(Attr(root, "width")) ?? 24, vh = Number(Attr(root, "height")) ?? 24;
        if (box.Length == 4) (vx, vy, vw, vh) = (box[0], box[1], box[2], box[3]);
        if (vw <= 0 || vh <= 0) throw new FormatException("viewBox 无效");
        double k = 24 / Math.Max(vw, vh);
        var fit = Matrix.Identity;
        fit.Translate(-vx, -vy);
        fit.Scale(k, k);
        fit.Translate((24 - vw * k) / 2, (24 - vh * k) / 2);

        // SVG defaults: filled black, no stroke, width 1, butt caps, miter joins
        var start = Inherit(new Style(true, false, 1, FillRule.Nonzero, PenLineCap.Flat, PenLineJoin.Miter, Matrix.Identity), root);
        var parts = new List<IconPart>();
        Walk(root, start, fit, parts);
        if (parts.Count == 0) throw new FormatException("SVG 里没有可绘制的图形");
        return parts.ToArray();
    }

    private static void Walk(XElement parent, Style style, Matrix fit, List<IconPart> parts)
    {
        foreach (var e in parent.Elements())
        {
            var name = e.Name.LocalName;
            if (name is "defs" or "title" or "desc" or "style" or "metadata" or "clipPath" or "mask" or "symbol"
                or "linearGradient" or "radialGradient" or "pattern" or "filter") continue;
            var s = Inherit(style, e);
            if (name == "g" || name == "a") { Walk(e, s, fit, parts); continue; }
            var geo = Shape(e, s);
            if (geo == null || (!s.Fill && !s.Stroke)) continue;

            var m = s.Transform * fit;   // the element's own transforms first, then into icon space
            geo.Transform = new MatrixTransform(m);
            geo.Freeze();
            double scale = Math.Sqrt(Math.Abs(m.Determinant));
            parts.Add(new IconPart(geo, s.Fill, s.Stroke ? s.Width * scale : 0, s.Cap, s.Join));
        }
    }

    private static Geometry? Shape(XElement e, Style s)
    {
        double N(string a) => Number(Attr(e, a)) ?? 0;
        switch (e.Name.LocalName)
        {
            case "path":
                var d = Attr(e, "d");
                if (string.IsNullOrWhiteSpace(d)) return null;
                // the parser hands out a frozen geometry: clone it so the transform into icon space can be set
                try { return Geometry.Parse((s.Rule == FillRule.Nonzero ? "F1 " : "F0 ") + d).Clone(); }
                catch (Exception ex) { throw new FormatException("path 的 d 无法解析：" + ex.Message, ex); }
            case "circle":
                return new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r"));
            case "ellipse":
                return new EllipseGeometry(new Point(N("cx"), N("cy")), N("rx"), N("ry"));
            case "rect":
                double rx = Number(Attr(e, "rx")) ?? Number(Attr(e, "ry")) ?? 0, ry = Number(Attr(e, "ry")) ?? rx;
                return new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")), rx, ry);
            case "line":
                return new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2")));
            case "polyline":
            case "polygon":
                var p = Numbers(Attr(e, "points"));
                if (p.Length < 4) return null;
                var fig = new PathFigure { StartPoint = new Point(p[0], p[1]), IsClosed = e.Name.LocalName == "polygon" };
                for (int i = 2; i + 1 < p.Length; i += 2) fig.Segments.Add(new LineSegment(new Point(p[i], p[i + 1]), true));
                return new PathGeometry([fig]) { FillRule = s.Rule == FillRule.Nonzero ? FillRule.Nonzero : FillRule.EvenOdd };
            default:
                return null;
        }
    }

    /// <summary>The element's presentation attributes (and inline style, which wins) over what it inherits.</summary>
    private static Style Inherit(Style s, XElement e)
    {
        string? Get(string name) => StyleOf(e, name) ?? Attr(e, name);
        if (Get("fill") is { } fill) s = s with { Fill = Paints(fill) };
        if (Get("stroke") is { } stroke) s = s with { Stroke = Paints(stroke) };
        if (Number(Get("stroke-width")) is { } w) s = s with { Width = w };
        if (Get("fill-rule") is { } rule) s = s with { Rule = rule.Trim() == "evenodd" ? FillRule.EvenOdd : FillRule.Nonzero };
        if (Get("stroke-linecap") is { } cap) s = s with { Cap = cap.Trim() switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat } };
        if (Get("stroke-linejoin") is { } join) s = s with { Join = join.Trim() switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter } };
        if (Attr(e, "transform") is { } t) s = s with { Transform = ParseTransform(t) * s.Transform };
        return s;
    }

    private static bool Paints(string value) => value.Trim() is not ("none" or "transparent" or "");

    private static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static string? StyleOf(XElement e, string name)
    {
        if (Attr(e, "style") is not { } style) return null;
        foreach (var decl in style.Split(';'))
        {
            int c = decl.IndexOf(':');
            if (c > 0 && decl[..c].Trim() == name) return decl[(c + 1)..].Trim();
        }
        return null;
    }

    private static double? Number(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberRegex();

    private static double[] Numbers(string? s) => s == null ? [] :
        NumberRegex().Matches(s).Select(m => double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformRegex();

    /// <summary>An SVG transform list as one matrix (the rightmost applies first, as in SVG).</summary>
    private static Matrix ParseTransform(string list)
    {
        var total = Matrix.Identity;
        foreach (Match m in TransformRegex().Matches(list))
        {
            var a = Numbers(m.Groups[2].Value);
            double A(int i, double d = 0) => i < a.Length ? a[i] : d;
            var t = Matrix.Identity;
            switch (m.Groups[1].Value)
            {
                case "matrix": if (a.Length == 6) t = new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]); break;
                case "translate": t.Translate(A(0), A(1)); break;
                case "scale": t.Scale(A(0, 1), A(1, A(0, 1))); break;
                case "rotate": t.RotateAt(A(0), A(1), A(2)); break;
                case "skewX": t.Skew(A(0), 0); break;
                case "skewY": t.Skew(0, A(0)); break;
            }
            total = t * total;   // left-to-right list: later entries apply to the shape first
        }
        return total;
    }
}
