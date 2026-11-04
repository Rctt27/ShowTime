using ShowTime.Core.Editor;
using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;

namespace ShowTime.Core.Tests.Recording;

public sealed class BrushStrokeTests
{
    /// <summary>Widths without the tapers at both ends.</summary>
    private static readonly HighlightStyle NoTaper = HighlightStyle.Default with { StartTaper = 0, EndTaper = 0 };

    private static List<TrailSample> Line(int count, int stepPixels, double stepMs, int x0 = 0, double t0 = 0) =>
        [.. Enumerable.Range(0, count).Select(i => new TrailSample(new PixelPoint(x0 + i * stepPixels, 0), TimeSpan.FromMilliseconds(t0 + i * stepMs)))];

    /// <summary>A circle of radius 150 drawn in 600 ms, read about every 16 ms, with a hand's and a timer's jitter.</summary>
    private static List<TrailSample> ShakyCircle()
    {
        long seed = 1;
        double Noise() => ((seed = seed * 16807 % 2147483647) / 2147483647.0 - 0.5) * 2;
        var samples = new List<TrailSample>();
        for (var t = 0.0; t <= 600; t += 16 + 5 * Noise())
        {
            var angle = 2 * Math.PI * t / 600;
            samples.Add(new TrailSample(
                new PixelPoint((int)Math.Round(400 + 150 * Math.Cos(angle) + 2 * Noise()), (int)Math.Round(300 + 150 * Math.Sin(angle) + 2 * Noise())),
                TimeSpan.FromMilliseconds(t)));
        }
        return samples;
    }

    /// <summary>Back and forth between x = 0 and x = 40, every 16 ms.</summary>
    private static List<TrailSample> ZigZag(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new TrailSample(new PixelPoint(i % 2 * 40, i % 3), TimeSpan.FromMilliseconds(i * 16)))];

    /// <summary>How unevenly the path turns: the standard deviation, in degrees, of the turn between consecutive steps.</summary>
    private static double TurnSpread(IReadOnlyList<StrokeDisc> stroke)
    {
        var turns = new List<double>();
        for (var i = 2; i < stroke.Count; i++)
        {
            var before = Math.Atan2(stroke[i - 1].Y - stroke[i - 2].Y, stroke[i - 1].X - stroke[i - 2].X);
            var after = Math.Atan2(stroke[i].Y - stroke[i - 1].Y, stroke[i].X - stroke[i - 1].X);
            turns.Add(Math.IEEERemainder((after - before) * 180 / Math.PI, 360));
        }
        var mean = turns.Average();
        return Math.Sqrt(turns.Average(t => (t - mean) * (t - mean)));
    }

    private static bool Inside(StrokeDisc d, PixelRect r) =>
        d.X - d.Radius >= r.X && d.X + d.Radius <= r.Right && d.Y - d.Radius >= r.Y && d.Y + d.Radius <= r.Bottom;

    [Fact]
    public void A_slow_stroke_has_almost_the_full_width() =>
        // 3 pixels per 16 ms: about 190 pixels per second.
        BrushStroke.Build(Line(30, 3, 16), NoTaper, 1, false)[^1].Radius.ShouldBeInRange(3.1, 3.5);

    [Fact]
    public void A_fast_stroke_is_thinner_but_never_below_MinRatio()
    {
        // 80 pixels per 16 ms: 5000 pixels per second, beyond FastSpeed.
        var stroke = BrushStroke.Build(Line(40, 80, 16), NoTaper, 1, false);
        var floor = 0.32 * 7 / 2;
        stroke[^1].Radius.ShouldBeInRange(floor - 1e-9, floor * 1.1);
        stroke.ShouldAllBe(d => d.Radius >= floor - 1e-9);
    }

    [Fact]
    public void The_width_eases_into_an_acceleration()
    {
        var slow = Line(10, 3, 16);
        var samples = slow.Append(new TrailSample(new PixelPoint(27 + 80, 0), TimeSpan.FromMilliseconds(160))).ToList();
        var eased = BrushStroke.Build(samples, NoTaper, 1, false)[^1].Radius;
        var instant = BrushStroke.Build(samples, NoTaper with { WidthSmoothing = 0 }, 1, false)[^1].Radius;
        eased.ShouldBeGreaterThan(instant);
        eased.ShouldBeLessThan(BrushStroke.Build(slow, NoTaper, 1, false)[^1].Radius);
    }

    [Fact]
    public void The_same_gesture_gives_the_same_stroke_at_any_scale()
    {
        // At 200 %, the same hand movement covers twice the pixels.
        var at1 = BrushStroke.Build(Line(20, 7, 16), HighlightStyle.Default, 1, true);
        var at2 = BrushStroke.Build(Line(20, 14, 16), HighlightStyle.Default, 2, true);
        at2.Count.ShouldBe(at1.Count);
        for (var i = 0; i < at1.Count; i++)
        {
            at2[i].X.ShouldBe(2 * at1[i].X, 1e-6);
            at2[i].Radius.ShouldBe(2 * at1[i].Radius, 1e-6);
        }
    }

    [Fact]
    public void Fluidity_makes_a_shaky_circle_turn_steadily_and_ends_under_the_mouse()
    {
        var circle = ShakyCircle();
        var smooth = BrushStroke.Build(circle, HighlightStyle.Default, 1, false);
        var raw = BrushStroke.Build(circle, HighlightStyle.Default with { Fluidity = 0 }, 1, false);
        TurnSpread(smooth).ShouldBeLessThan(0.6 * TurnSpread(raw));
        (smooth[0].X, smooth[0].Y).ShouldBe((circle[0].Point.X, circle[0].Point.Y));
        (smooth[^1].X, smooth[^1].Y).ShouldBe((circle[^1].Point.X, circle[^1].Point.Y));
    }

    [Fact]
    public void A_pause_keeps_a_sharp_corner()
    {
        // Right, 300 ms without moving, then down.
        var samples = Line(11, 10, 16).Concat(Enumerable.Range(1, 10).Select(i =>
            new TrailSample(new PixelPoint(100, i * 10), TimeSpan.FromMilliseconds(460 + i * 16)))).ToList();
        BrushStroke.Build(samples, HighlightStyle.Default, 1, false).ShouldContain(d => Math.Abs(d.X - 100) < 0.5 && Math.Abs(d.Y) < 0.5);
    }

    [Fact]
    public void Samples_at_the_same_instant_give_finite_radii() =>
        BrushStroke.Build(
                [new(new(0, 0), TimeSpan.Zero), new(new(10, 0), TimeSpan.Zero), new(new(20, 0), TimeSpan.FromMilliseconds(16))],
                HighlightStyle.Default, 1, true)
            .ShouldAllBe(d => double.IsFinite(d.Radius) && d.Radius > 0);

    [Fact]
    public void A_stroke_that_doubles_back_stays_finite_and_unbroken()
    {
        var stroke = BrushStroke.Build(ZigZag(20), HighlightStyle.Default, 1, true);
        stroke.ShouldAllBe(d => double.IsFinite(d.X) && double.IsFinite(d.Y) && double.IsFinite(d.Radius));
        stroke.Zip(stroke.Skip(1)).ShouldAllBe(p => double.Hypot(p.Second.X - p.First.X, p.Second.Y - p.First.Y) <= 2 * BrushStroke.Step);
    }

    [Fact]
    public void Every_style_at_its_bounds_gives_a_finite_stroke()
    {
        foreach (var bound in new Func<HighlightStyleField, double>[] { f => f.Min, f => f.Max })
        {
            var style = HighlightStyle.Fields.Aggregate(HighlightStyle.Default, (s, f) => f.With(s, bound(f)));
            var stroke = BrushStroke.Build(ShakyCircle(), style, 1, true);
            stroke.ShouldNotBeEmpty();
            stroke.ShouldAllBe(d => double.IsFinite(d.X) && double.IsFinite(d.Y) && double.IsFinite(d.Radius) && d.Radius >= 0);
        }
    }

    [Fact]
    public void A_press_without_moving_is_a_dot_of_TaperFloor() =>
        BrushStroke.Build([new(new(50, 60), TimeSpan.Zero)], HighlightStyle.Default, 1, false)
            .ShouldBe([new StrokeDisc(50, 60, 7 / 2.0 * 0.25)]);

    [Fact]
    public void Nothing_read_gives_no_stroke() =>
        BrushStroke.Build([], HighlightStyle.Default, 1, true).ShouldBeEmpty();

    [Fact]
    public void The_start_tapers_from_the_press_and_the_end_only_once_lifted()
    {
        var held = BrushStroke.Build(Line(40, 3, 16), HighlightStyle.Default, 1, false);
        var lifted = BrushStroke.Build(Line(40, 3, 16), HighlightStyle.Default, 1, true);
        held[0].Radius.ShouldBeLessThan(0.3 * held[held.Count / 2].Radius);
        held[^1].Radius.ShouldBe(held[held.Count / 2].Radius, 0.2);
        lifted[^1].Radius.ShouldBeLessThan(0.3 * lifted[lifted.Count / 2].Radius);
    }

    [Fact]
    public void The_hull_of_equal_discs_is_a_rectangle() =>
        BrushStroke.Hull(new(0, 0, 5), new(10, 0, 5)).ShouldBe([new PointD(0, 5), new PointD(10, 5), new PointD(10, -5), new PointD(0, -5)]);

    [Fact]
    public void There_is_no_hull_when_a_disc_holds_the_other() =>
        BrushStroke.Hull(new(0, 0, 5), new(1, 0, 2)).ShouldBeNull();

    [Fact]
    public void The_hull_touches_both_discs()
    {
        var (a, b) = (new StrokeDisc(0, 0, 6), new StrokeDisc(30, 10, 2));
        var hull = BrushStroke.Hull(a, b)!;
        new[] { hull[0], hull[3] }.ShouldAllBe(p => Math.Abs(double.Hypot(p.X - a.X, p.Y - a.Y) - a.Radius) < 1e-9);
        new[] { hull[1], hull[2] }.ShouldAllBe(p => Math.Abs(double.Hypot(p.X - b.X, p.Y - b.Y) - b.Radius) < 1e-9);
    }

    [Fact]
    public void Changed_is_null_for_the_same_stroke() =>
        BrushStroke.Changed(BrushStroke.Build(Line(10, 5, 16), HighlightStyle.Default, 1, false),
            BrushStroke.Build(Line(10, 5, 16), HighlightStyle.Default, 1, false)).ShouldBeNull();

    [Fact]
    public void Changed_from_nothing_covers_the_stroke_and_its_antialiased_edge()
    {
        var stroke = BrushStroke.Build(Line(10, 5, 16, x0: 20), HighlightStyle.Default, 1, false);
        var changed = BrushStroke.Changed([], stroke)!.Value;
        stroke.ShouldAllBe(d => Inside(d, changed));
        changed.X.ShouldBe((int)Math.Floor(stroke.Min(d => d.X - d.Radius)) - BrushStroke.AntialiasMargin);
    }

    [Fact]
    public void Changed_covers_only_the_tail_when_the_stroke_grows()
    {
        var before = BrushStroke.Build(Line(40, 5, 16), HighlightStyle.Default, 1, false);
        var after = BrushStroke.Build(Line(41, 5, 16), HighlightStyle.Default, 1, false);
        var changed = BrushStroke.Changed(before, after)!.Value;
        changed.X.ShouldBeGreaterThan(100);
        Inside(after[^1], changed).ShouldBeTrue();
        Inside(before[^1], changed).ShouldBeTrue();
    }

    [Fact]
    public void Changed_covers_both_strokes_when_the_head_is_removed()
    {
        var before = BrushStroke.Build(Line(40, 5, 16), HighlightStyle.Default, 1, false);
        var after = BrushStroke.Build(Line(30, 5, 16, x0: 50, t0: 160), HighlightStyle.Default, 1, false);
        var changed = BrushStroke.Changed(before, after)!.Value;
        before.ShouldAllBe(d => Inside(d, changed));
        after.ShouldAllBe(d => Inside(d, changed));
    }

    [Fact]
    public void PiecesTouching_finds_the_pieces_over_the_area()
    {
        StrokeDisc[] stroke = [new(0, 0, 5), new(100, 0, 5), new(200, 0, 5)];
        BrushStroke.PiecesTouching(stroke, new PixelRect(95, -5, 10, 10)).ShouldBe([0, 1]);
        BrushStroke.PiecesTouching(stroke, new PixelRect(300, 0, 10, 10)).ShouldBeEmpty();
    }
}
