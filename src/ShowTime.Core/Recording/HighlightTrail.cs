using ShowTime.Core.Geometry;

namespace ShowTime.Core.Recording;

/// <summary>
/// The mouse positions read while the highlight trigger is held, with the time each was read at, and how the trail fades
/// out once released. <see cref="BrushStroke"/> turns them into the stroke.
/// </summary>
public sealed class HighlightTrail
{
    /// <summary>About half a minute of continuous movement; beyond that the oldest quarter goes.</summary>
    public const int MaxPoints = 2000;

    /// <summary>Pixels the mouse must move before a point is added.</summary>
    public const int MinStep = 2;

    /// <summary>How long the trail stays as it is once released, before it fades.</summary>
    public static readonly TimeSpan FadeDelay = TimeSpan.FromMilliseconds(300);

    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(500);

    private readonly List<TrailSample> _samples = [];

    public IReadOnlyList<TrailSample> Samples => _samples;

    /// <param name="time">Since the press.</param>
    public void Begin(PixelPoint at, TimeSpan time)
    {
        _samples.Clear();
        _samples.Add(new TrailSample(at, time));
    }

    /// <returns>False when the point is too close to the last one to change the drawing.</returns>
    public bool Add(PixelPoint at, TimeSpan time)
    {
        if (_samples.Count > 0)
        {
            var last = _samples[^1].Point;
            long dx = at.X - last.X, dy = at.Y - last.Y;
            if (dx * dx + dy * dy < MinStep * MinStep)
                return false;
        }
        _samples.Add(new TrailSample(at, time));
        // A quarter at once: dropping the head repaints the whole overlay, which must stay rare.
        if (_samples.Count > MaxPoints)
            _samples.RemoveRange(0, MaxPoints / 4);
        return true;
    }

    /// <summary>
    /// <paramref name="fullOpacity"/> for <see cref="FadeDelay"/>, then fast at first and gentle at the end: an ease-out
    /// to 0 over <see cref="FadeDuration"/>.
    /// </summary>
    public static double FadeOpacity(TimeSpan sinceRelease, double fullOpacity)
    {
        var remaining = 1 - Math.Clamp((sinceRelease - FadeDelay) / FadeDuration, 0, 1);
        return fullOpacity * remaining * remaining;
    }
}
