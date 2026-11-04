using ShowTime.Core.Editor;
using ShowTime.Core.Geometry;
using ShowTime.Core.Settings;

namespace ShowTime.Core.Recording;

/// <summary>A mouse position read while the trigger is held, with the time since the press.</summary>
public readonly record struct TrailSample(PixelPoint Point, TimeSpan Time);

/// <summary>One disc of a brush stroke, in physical screen pixels.</summary>
public readonly record struct StrokeDisc(double X, double Y, double Radius);

/// <summary>
/// The highlight drawn as a brush stroke: thinner when the mouse is fast, eased in width and in path, tapered at its
/// ends. The stroke is the union of its discs and of the hull between each disc and the next; both apps paint them
/// opaque and make the whole window translucent, so that crossings do not darken. Tuned in tools/highlight-lab.html.
/// </summary>
public static class BrushStroke
{
    /// <summary>The longest step between two discs along the curve, in pixels at 100 %.</summary>
    public const double Step = 3;

    /// <summary>Pixels around a change, for the antialiased edge.</summary>
    public const int AntialiasMargin = 2;

    /// <param name="scale">The screen's scale: lengths and speeds of the style are in pixels at 100 %.</param>
    /// <param name="lifted">Released: the end tapers too.</param>
    public static IReadOnlyList<StrokeDisc> Build(IReadOnlyList<TrailSample> samples, HighlightStyle style, double scale, bool lifted)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(style);
        if (samples.Count == 0)
            return [];
        var times = samples.Select(s => s.Time.TotalMilliseconds).ToArray();
        var path = Smooth(samples, times, style.Fluidity);
        var line = Centerline(path, Widths(path, times, style, scale), scale);

        var lengths = new double[line.Count];
        for (var i = 1; i < line.Count; i++)
            lengths[i] = lengths[i - 1] + Distance(line[i - 1].At, line[i].At);
        var total = lengths[^1];
        var start = style.StartTaper * scale;
        var end = lifted ? Math.Min(style.EndTaper * scale, total / 2) : 0;
        var discs = new StrokeDisc[line.Count];
        for (var i = 0; i < line.Count; i++)
        {
            var factor = 1.0;
            if (start > 0)
                factor *= Taper(lengths[i] / start, style.TaperFloor);
            if (end > 0)
                factor *= Taper((total - lengths[i]) / end, style.TaperFloor);
            discs[i] = new StrokeDisc(line[i].At.X, line[i].At.Y, line[i].Width / 2 * factor);
        }
        return discs;
    }

    /// <summary>The quadrilateral between the outer tangents of two discs, or null when one holds the other.</summary>
    public static PointD[]? Hull(StrokeDisc a, StrokeDisc b)
    {
        var d = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (d <= Math.Abs(a.Radius - b.Radius))
            return null;
        double ux = (b.X - a.X) / d, uy = (b.Y - a.Y) / d;
        var along = (a.Radius - b.Radius) / d;
        var across = Math.Sqrt(1 - along * along);
        var m1 = new PointD(ux * along - uy * across, uy * along + ux * across);
        var m2 = new PointD(ux * along + uy * across, uy * along - ux * across);
        return
        [
            new(a.X + a.Radius * m1.X, a.Y + a.Radius * m1.Y), new(b.X + b.Radius * m1.X, b.Y + b.Radius * m1.Y),
            new(b.X + b.Radius * m2.X, b.Y + b.Radius * m2.Y), new(a.X + a.Radius * m2.X, a.Y + a.Radius * m2.Y)
        ];
    }

    /// <summary>
    /// What to repaint between two strokes: the discs from the last one they share, with their antialiased edge, or null
    /// when nothing changed. The stroke is computed the same way every time, so shared discs compare exactly.
    /// </summary>
    public static PixelRect? Changed(IReadOnlyList<StrokeDisc> previous, IReadOnlyList<StrokeDisc> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var common = 0;
        while (common < previous.Count && common < current.Count && previous[common] == current[common])
            common++;
        if (common == previous.Count && common == current.Count)
            return null;

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var discs in new[] { previous, current })
        {
            // From the disc before the first change: the hull between them changes too.
            for (var i = Math.Max(common - 1, 0); i < discs.Count; i++)
            {
                var d = discs[i];
                left = Math.Min(left, d.X - d.Radius);
                top = Math.Min(top, d.Y - d.Radius);
                right = Math.Max(right, d.X + d.Radius);
                bottom = Math.Max(bottom, d.Y + d.Radius);
            }
        }
        int x = (int)Math.Floor(left) - AntialiasMargin, y = (int)Math.Floor(top) - AntialiasMargin;
        return new PixelRect(x, y, (int)Math.Ceiling(right) + AntialiasMargin - x, (int)Math.Ceiling(bottom) + AntialiasMargin - y);
    }

    /// <summary>The indices i whose piece, disc i and its hull to disc i + 1, touches the area.</summary>
    public static IEnumerable<int> PiecesTouching(IReadOnlyList<StrokeDisc> stroke, PixelRect area)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        for (var i = 0; i < stroke.Count; i++)
        {
            var a = stroke[i];
            double left = a.X - a.Radius, top = a.Y - a.Radius, right = a.X + a.Radius, bottom = a.Y + a.Radius;
            if (i + 1 < stroke.Count)
            {
                var b = stroke[i + 1];
                left = Math.Min(left, b.X - b.Radius);
                top = Math.Min(top, b.Y - b.Radius);
                right = Math.Max(right, b.X + b.Radius);
                bottom = Math.Max(bottom, b.Y + b.Radius);
            }
            if (left < area.Right && area.X < right && top < area.Bottom && area.Y < bottom)
                yield return i;
        }
    }

    /// <summary>
    /// The path eased like a hand on a brush: each sample becomes the Gaussian-weighted mean of its neighbours in time.
    /// The samples are cut into stretches wherever nothing was read for longer than 3 sigma (a pause), and each stretch
    /// is eased on its own with a symmetric window that shrinks near its ends: the stroke still starts at the press,
    /// ends under the mouse without lag, and keeps a sharp corner where the hand stopped.
    /// </summary>
    private static PointD[] Smooth(IReadOnlyList<TrailSample> samples, double[] times, double sigma)
    {
        var points = samples.Select(s => new PointD(s.Point.X, s.Point.Y)).ToArray();
        if (sigma <= 0 || points.Length < 3)
            return points;
        var smooth = new PointD[points.Length];
        var start = 0;
        for (var end = 1; end <= points.Length; end++)
        {
            if (end < points.Length && times[end] - times[end - 1] <= 3 * sigma)
                continue;
            for (var i = start; i < end; i++)
                smooth[i] = SmoothAt(points, times, start, end, i, sigma);
            start = end;
        }
        return smooth;
    }

    private static PointD SmoothAt(PointD[] points, double[] times, int start, int end, int i, double sigma)
    {
        var t = times[i];
        var half = Math.Min(3 * sigma, Math.Min(t - times[start], times[end - 1] - t));
        double x = 0, y = 0, total = 0;
        for (var j = i; j >= start && t - times[j] <= half; j--)
            Add(j);
        for (var j = i + 1; j < end && times[j] - t <= half; j++)
            Add(j);
        return new PointD(x / total, y / total);

        void Add(int j)
        {
            var d = times[j] - t;
            var w = Math.Exp(-d * d / (2 * sigma * sigma));
            x += points[j].X * w;
            y += points[j].Y * w;
            total += w;
        }
    }

    /// <summary>The width at each point, in physical pixels: thinner when the mouse is fast, eased over time.</summary>
    private static double[] Widths(PointD[] path, double[] times, HighlightStyle style, double scale)
    {
        var max = style.Thickness * scale;
        var widths = new double[path.Length];
        widths[0] = max;
        for (var i = 1; i < path.Length; i++)
        {
            // Two readings at the same instant count as one millisecond apart: no infinite speed.
            var dt = Math.Max(times[i] - times[i - 1], 1);
            var speed = Distance(path[i - 1], path[i]) / scale / (dt / 1000);
            var target = max * (1 - (1 - style.MinRatio) * EaseOut(Math.Min(speed / style.FastSpeed, 1)));
            var k = style.WidthSmoothing > 0 ? 1 - Math.Exp(-dt / style.WidthSmoothing) : 1;
            widths[i] = widths[i - 1] + (target - widths[i - 1]) * k;
        }
        return widths;
    }

    /// <summary>The curve through the points, a step at most <see cref="Step"/> apart, with the width interpolated.</summary>
    private static List<(PointD At, double Width)> Centerline(PointD[] path, double[] widths, double scale)
    {
        var n = path.Length;
        var line = new List<(PointD At, double Width)> { (path[0], widths[0]) };
        for (var i = 0; i < n - 1; i++)
        {
            var p0 = i > 0 ? path[i - 1] : Reflect(path[0], path[1]);
            var p3 = i + 2 < n ? path[i + 2] : Reflect(path[n - 1], path[n - 2]);
            var parts = Math.Max(1, (int)Math.Ceiling(Distance(path[i], path[i + 1]) / (Step * scale)));
            for (var j = 1; j <= parts; j++)
            {
                var u = (double)j / parts;
                // The curve passes through each point exactly, the last one under the mouse included.
                var at = j == parts ? path[i + 1] : CatmullRom(p0, path[i], path[i + 1], p3, u);
                line.Add((at, widths[i] + (widths[i + 1] - widths[i]) * u));
            }
        }
        return line;
    }

    /// <summary>Centripetal Catmull-Rom (Barry and Goldman) between p1 and p2, at u in [0, 1]: no loop, no cusp.</summary>
    private static PointD CatmullRom(PointD p0, PointD p1, PointD p2, PointD p3, double u)
    {
        var t1 = Knot(p0, p1);
        var t2 = t1 + Knot(p1, p2);
        var t3 = t2 + Knot(p2, p3);
        var t = t1 + (t2 - t1) * u;
        var a1 = Lerp(p0, p1, t / t1);
        var a2 = Lerp(p1, p2, (t - t1) / (t2 - t1));
        var a3 = Lerp(p2, p3, (t - t2) / (t3 - t2));
        var b1 = Lerp(a1, a2, t / t2);
        var b2 = Lerp(a2, a3, (t - t1) / (t3 - t1));
        return Lerp(b1, b2, (t - t1) / (t2 - t1));
    }

    private static double Knot(PointD a, PointD b) => Math.Max(Math.Sqrt(Distance(a, b)), 1e-3);

    /// <summary>From the floor at 0 to the full width at 1 and beyond.</summary>
    private static double Taper(double x, double floor) => floor + (1 - floor) * EaseOut(Math.Clamp(x, 0, 1));

    private static double EaseOut(double x) => x * (2 - x);

    private static double Distance(PointD a, PointD b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static PointD Lerp(PointD a, PointD b, double s) => new(a.X + (b.X - a.X) * s, a.Y + (b.Y - a.Y) * s);

    private static PointD Reflect(PointD p, PointD q) => new(2 * p.X - q.X, 2 * p.Y - q.Y);
}
