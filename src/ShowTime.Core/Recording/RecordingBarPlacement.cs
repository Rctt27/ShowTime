using ShowTime.Core.Geometry;

namespace ShowTime.Core.Recording;

/// <summary>A screen the bar may go to: its work area, and the bar's size there (screens may differ in scale).</summary>
public readonly record struct BarScreen(PixelRect WorkArea, PixelSize Bar);

/// <summary>Where the bar goes: the screen (an index into the candidates), its top-left corner, and whether it lies on the recorded area.</summary>
public readonly record struct BarPlacement(int Screen, PixelPoint Position, bool CoversArea);

public static class RecordingBarPlacement
{
    /// <summary>Below the recorded area when there is room, otherwise above it, otherwise inside it.</summary>
    /// <param name="area">The recorded area, in physical screen pixels.</param>
    /// <param name="bar">The bar's size.</param>
    /// <param name="workArea">The work area of the monitor showing the area.</param>
    /// <param name="gap">Space between the area and the bar.</param>
    /// <returns>The bar's top-left corner.</returns>
    public static PixelPoint Place(PixelRect area, PixelSize bar, PixelRect workArea, int gap = 10)
    {
        var x = Math.Clamp(area.X + (area.Width - bar.Width) / 2, workArea.X, Math.Max(workArea.X, workArea.Right - bar.Width));
        int y;
        if (area.Bottom + gap + bar.Height <= workArea.Bottom)
            y = area.Bottom + gap;
        else if (area.Y - gap - bar.Height >= workArea.Y)
            y = area.Y - gap - bar.Height;
        else
            y = workArea.Bottom - bar.Height - gap;
        return new PixelPoint(x, y);
    }

    /// <summary>
    /// Next to the area on its own screen, as <see cref="Place(PixelRect, PixelSize, PixelRect, int)"/>; when that
    /// lands on the area (a whole screen recorded), at the bottom of another screen clear of it; failing that, inside
    /// the area, which <see cref="BarPlacement.CoversArea"/> then says.
    /// </summary>
    /// <param name="area">The recorded area, in physical screen pixels.</param>
    /// <param name="screens">The screens, the one showing the area first.</param>
    /// <param name="gap">Space between the bar and the area or the screen's edge.</param>
    public static BarPlacement Place(PixelRect area, IReadOnlyList<BarScreen> screens, int gap = 10)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
            throw new ArgumentException("At least the screen showing the area.", nameof(screens));
        var own = Place(area, screens[0].Bar, screens[0].WorkArea, gap);
        if (!Covers(area, own, screens[0].Bar))
            return new BarPlacement(0, own, CoversArea: false);
        for (var i = 1; i < screens.Count; i++)
        {
            var (workArea, bar) = screens[i];
            var position = new PixelPoint(workArea.X + Math.Max(0, (workArea.Width - bar.Width) / 2), workArea.Bottom - bar.Height - gap);
            if (!Covers(area, position, bar))
                return new BarPlacement(i, position, CoversArea: false);
        }
        return new BarPlacement(0, own, CoversArea: true);
    }

    private static bool Covers(PixelRect area, PixelPoint position, PixelSize bar) =>
        !area.Intersect(new PixelRect(position.X, position.Y, bar.Width, bar.Height)).IsEmpty;
}
