using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;

namespace ShowTime.Core.Tests.Recording;

public sealed class HighlightTrailTests
{
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Begin_starts_a_new_trail_at_the_point()
    {
        var trail = new HighlightTrail();
        trail.Begin(new PixelPoint(0, 0), Ms(0));
        trail.Add(new PixelPoint(10, 0), Ms(16));
        trail.Begin(new PixelPoint(50, 50), Ms(100));
        trail.Samples.ShouldBe([new TrailSample(new PixelPoint(50, 50), Ms(100))]);
    }

    [Fact]
    public void Samples_keep_the_time_they_were_read_at()
    {
        var trail = new HighlightTrail();
        trail.Begin(new PixelPoint(0, 0), Ms(0));
        trail.Add(new PixelPoint(10, 0), Ms(16));
        trail.Add(new PixelPoint(20, 0), Ms(40));
        trail.Samples.Select(s => s.Time).ShouldBe([Ms(0), Ms(16), Ms(40)]);
    }

    [Fact]
    public void Points_closer_than_the_minimum_step_are_skipped()
    {
        var trail = new HighlightTrail();
        trail.Begin(new PixelPoint(0, 0), Ms(0));
        trail.Add(new PixelPoint(1, 1), Ms(16)).ShouldBeFalse();
        trail.Add(new PixelPoint(2, 0), Ms(32)).ShouldBeTrue();
        trail.Samples.Count.ShouldBe(2);
    }

    /// <summary>
    /// The oldest quarter goes at once rather than one point per frame: each drop repaints the whole overlay, so it
    /// happens every 500 points instead of on every frame once the limit is reached.
    /// </summary>
    [Fact]
    public void Oldest_points_go_by_the_quarter_beyond_the_limit()
    {
        var trail = new HighlightTrail();
        trail.Begin(new PixelPoint(0, 0), Ms(0));
        for (var i = 1; i < HighlightTrail.MaxPoints; i++)
            trail.Add(new PixelPoint(i * 2, 0), Ms(i * 16));
        trail.Samples.Count.ShouldBe(HighlightTrail.MaxPoints);

        trail.Add(new PixelPoint(HighlightTrail.MaxPoints * 2, 0), Ms(HighlightTrail.MaxPoints * 16));
        trail.Samples.Count.ShouldBe(HighlightTrail.MaxPoints - HighlightTrail.MaxPoints / 4 + 1);
        trail.Samples[0].Point.ShouldBe(new PixelPoint(HighlightTrail.MaxPoints / 4 * 2, 0));
        trail.Samples[^1].Point.ShouldBe(new PixelPoint(HighlightTrail.MaxPoints * 2, 0));
    }

    [Theory]
    [InlineData(0, 0.45)]
    [InlineData(300, 0.45)]
    [InlineData(550, 0.1125)]
    [InlineData(800, 0)]
    [InlineData(2000, 0)]
    public void The_trail_stays_300_ms_then_fades_fast_then_gently(int milliseconds, double opacity) =>
        HighlightTrail.FadeOpacity(Ms(milliseconds), 0.45).ShouldBe(opacity, 1e-9);

    [Fact]
    public void The_fade_only_decreases()
    {
        var values = Enumerable.Range(0, 33).Select(i => HighlightTrail.FadeOpacity(Ms(i * 16), 0.45)).ToList();
        values.ShouldBe([.. values.OrderByDescending(v => v)]);
    }
}
