using System.Globalization;

namespace ShowTime.Core.Settings;

/// <summary>How a value of the stroke is shown beside its slider.</summary>
public enum StyleUnit
{
    Pixels,
    Percent,
    PixelsPerSecond,
    Milliseconds
}

/// <summary>One value of the stroke as both settings windows show it: its slider's bounds, and how to read and change it.</summary>
public sealed record HighlightStyleField(
    string Label, string Help, double Min, double Max, double Step, StyleUnit Unit,
    Func<HighlightStyle, double> Get, Func<HighlightStyle, double, HighlightStyle> With)
{
    // A decimal comma without culture data, which a trimmed app may lack.
    private static readonly NumberFormatInfo French = new() { NumberDecimalSeparator = "," };

    /// <summary>
    /// The value on the slider's nearest step, within its bounds, rounded to the step's decimals: a slider's arithmetic
    /// does not leave 0.7000000000000001 in settings.json.
    /// </summary>
    public double Snap(double value)
    {
        var decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(Step)));
        var steps = Math.Round((Math.Clamp(value, Min, Max) - Min) / Step);
        return Math.Round(Math.Min(Min + steps * Step, Max), decimals);
    }

    public string Format(double value) => Unit switch
    {
        StyleUnit.Pixels => value.ToString("0.#", French) + " px",
        StyleUnit.Percent => Math.Round(value * 100).ToString(French) + " %",
        StyleUnit.PixelsPerSecond => value.ToString("0", French) + " px/s",
        _ => value.ToString("0", French) + " ms"
    };
}

/// <summary>
/// The highlight's brush stroke as each user tunes it. Stored in settings.json as one object: a value missing from it
/// keeps its default, which is why the defaults are the constructor's (the JSON metadata reads them there, whereas it
/// would set a missing init-only property to 0). Lengths and speeds are in pixels at 100 %, durations in milliseconds.
/// </summary>
/// <param name="Thickness">The width while the mouse moves slowly.</param>
/// <param name="MinRatio">The share of <paramref name="Thickness"/> left when the mouse is fast.</param>
/// <param name="FastSpeed">The speed, per second, at which the stroke is at its thinnest.</param>
/// <param name="WidthSmoothing">How long the width takes to follow the speed.</param>
/// <param name="Fluidity">How long a stretch of the path is eased over, so that each change of direction is gradual.</param>
/// <param name="StartTaper">The length over which the stroke widens from the press.</param>
/// <param name="EndTaper">The length over which the stroke thins once released.</param>
/// <param name="TaperFloor">The width of a tapered end, as a share of the stroke's.</param>
/// <param name="Opacity">The trail's opacity while held, where its fade starts.</param>
public sealed record HighlightStyle(
    double Thickness = 7,
    double MinRatio = 0.32,
    double FastSpeed = 3900,
    double WidthSmoothing = 180,
    double Fluidity = 25,
    double StartTaper = 24,
    double EndTaper = 48,
    double TaperFloor = 0.25,
    double Opacity = 0.45)
{
    public static HighlightStyle Default { get; } = new();

    /// <summary>The sliders of the settings windows, in their order.</summary>
    public static IReadOnlyList<HighlightStyleField> Fields { get; } =
    [
        new("Largeur au repos", "Largeur quand la souris va lentement, à 100 %.",
            3, 40, 0.5, StyleUnit.Pixels, s => s.Thickness, (s, v) => s with { Thickness = v }),
        new("Largeur à grande vitesse", "Part de la largeur au repos qui reste quand la souris va vite. 100 % : pas d'affinement.",
            0.1, 1, 0.01, StyleUnit.Percent, s => s.MinRatio, (s, v) => s with { MinRatio = v }),
        new("Vitesse « rapide »", "Vitesse à laquelle le trait atteint sa largeur minimale. Plus bas : il s'affine plus tôt.",
            300, 6000, 100, StyleUnit.PixelsPerSecond, s => s.FastSpeed, (s, v) => s with { FastSpeed = v }),
        new("Douceur des variations", "Temps que met la largeur à suivre la vitesse. 0 : elle suit à chaque relevé.",
            0, 300, 5, StyleUnit.Milliseconds, s => s.WidthSmoothing, (s, v) => s with { WidthSmoothing = v }),
        new("Fluidité des courbes", "Lisse la trajectoire : chaque changement de direction devient progressif, comme la main sur un " +
            "pinceau. Le bout reste sous la souris. 0 : le trait passe exactement par chaque relevé.",
            0, 80, 1, StyleUnit.Milliseconds, s => s.Fluidity, (s, v) => s with { Fluidity = v }),
        new("Effilement du début", "Longueur sur laquelle le trait s'élargit en partant, comme un pinceau qui se pose. 0 : aucun.",
            0, 120, 1, StyleUnit.Pixels, s => s.StartTaper, (s, v) => s with { StartTaper = v }),
        new("Effilement de fin", "Longueur sur laquelle le trait s'affine au relâchement, comme un pinceau qui se lève. 0 : aucun.",
            0, 200, 1, StyleUnit.Pixels, s => s.EndTaper, (s, v) => s with { EndTaper = v }),
        new("Finesse des pointes", "Largeur des bouts effilés, en part de la largeur. 0 % : une pointe fine.",
            0, 1, 0.01, StyleUnit.Percent, s => s.TaperFloor, (s, v) => s with { TaperFloor = v }),
        new("Opacité", "Plus haut : plus couvrant, moins lisible au travers.",
            0.1, 1, 0.01, StyleUnit.Percent, s => s.Opacity, (s, v) => s with { Opacity = v })
    ];

    /// <summary>Each value within its slider's bounds: a file edited by hand cannot break the stroke.</summary>
    public HighlightStyle Clamped() => Fields.Aggregate(this, (style, field) =>
    {
        var value = field.Get(style);
        return field.With(style, double.IsFinite(value) ? Math.Clamp(value, field.Min, field.Max) : field.Get(Default));
    });
}
