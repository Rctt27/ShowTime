# Traînée « coup de pinceau » — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal :** le surlignage devient un trait de pinceau : il s'affine quand la souris accélère, ses courbes sont fluides,
ses bouts effilés et ses bords anticrénelés. Chaque utilisateur le règle dans une fenêtre d'aperçu ouverte depuis les
réglages, sur Windows comme sur macOS.

**Architecture :**
- Le cœur calcule le trait. `BrushStroke` produit des disques en pixels physiques, plus le rectangle à repeindre, à
  partir des échantillons horodatés de `HighlightTrail` et du `HighlightStyle` des réglages.
- Les overlays ne font que remplir les disques et leurs enveloppes. Celui de Windows passe à la transparence par pixel
  (`UpdateLayeredWindowIndirect`).
- La fenêtre « Trait du surlignage » fait tourner un vrai `Highlighter` sur ses propres `AppSettings`. Son entrée
  d'aperçu est le bouton gauche dans la zone de tracé.

**Tech Stack :** .NET 10, C#, WPF, WinForms, GDI+ et Win32 (Windows), AppKit (macOS), System.Text.Json en génération
de source, xunit v3, Shouldly, NSubstitute, FakeTimeProvider.

**Spec :** `docs/superpowers/specs/2026-11-04-brush-highlight-design.md`

## Global Constraints

- Branche `feature/brush-highlight`, déjà créée depuis un `main` à jour. Les commits finissent par les lignes
  d'attribution de la session. La PR est fusionnée avec un merge commit une fois la CI verte.
- Rien ne quitte la machine : aucun accès réseau, ni dans l'app ni dans le labo.
- Textes d'interface en français ; code et commentaires en anglais, avec la densité de commentaires du code voisin.
- Style par défaut : `Thickness` 7, `MinRatio` 0.32, `FastSpeed` 3900, `WidthSmoothing` 180 (ms), `Fluidity` 25 (ms),
  `StartTaper` 24, `EndTaper` 48, `TaperFloor` 0.25, `Opacity` 0.45. La couleur `#FFD400` et le fondu (300 ms, puis
  500 ms en ease-out) ne changent pas.
- Un ancien settings.json doit se charger, qu'il n'ait pas de `HighlightStyle` ou seulement un objet partiel. Ne jamais
  régénérer `tests/fixtures/legacy`.
- Les analyseurs tournent en `latest-recommended`, avertissements en erreurs, et
  `dotnet format ShowTime.slnx --verify-no-changes` doit passer. `src/ShowTime.Mac` se formate à la main : `using`
  avec `System` d'abord, puis par ordre alphabétique.
- Tests en local : `ShowTime.Core.Tests` et `ShowTime.Infrastructure.IntegrationTests` seulement, avec l'accord de
  l'utilisateur, demandé au début de l'exécution. `ShowTime.Windows.Tests` et `ShowTime.Platform.Tests` passent
  uniquement par la CI : `git push`, puis `gh workflow run CI --ref feature/brush-highlight`.
- Chaque comportement nouveau commence par un test qui échoue. La couverture des lignes du cœur reste ≥ 90 %.
- Le build Mac ne tourne que sur la CI : tout nom d'API AppKit absent du code existant se vérifie d'abord dans les
  métadonnées de `Microsoft.macOS.Ref`.

## Review Focus

1. **settings.json modifié à la main** (valeur négative, énorme, `1e400` lu comme ∞) : chaque champ revient dans sa
   plage, et une valeur non finie reprend la valeur par défaut. Test : Task 1,
   `Clamped_brings_back_out_of_range_and_non_finite_values`.
2. **Deux relevés au même instant** (minuteur regroupé) : aucune vitesse infinie, des rayons finis. Test : Task 2,
   `Samples_at_the_same_instant_give_finite_radii`.
3. **Un trait qui revient sur lui-même** (aller-retour, gribouillis) : ni NaN, ni trou. Test : Task 2,
   `A_stroke_that_doubles_back_stays_finite_and_unbroken`.
4. **Chaque curseur à ses bornes** (douceur 0, fluidité 0, effilements 0, pointes 0 %, puis tous au maximum) : un
   trait fini et visible. Test : Task 2, `Every_style_at_its_bounds_gives_a_finite_stroke`.
5. **Un trait qui sort de la zone ou de l'écran** : l'overlay ne repeint que sa surface, sans exception. Test : Task 4,
   `Points_outside_the_area_are_accepted`, adapté aux disques.

---

### Task 1 : le style dans les réglages

**Files :**
- Create : `src/ShowTime.Core/Settings/HighlightStyle.cs`
- Modify : `src/ShowTime.Core/Settings/AppSettings.cs`, `src/ShowTime.Core/Settings/SettingsEditor.cs` (`SettingsForm`),
  `src/ShowTime.Windows/Settings/SettingsWindow.xaml.cs`, `src/ShowTime.Mac/Settings/SettingsWindowController.cs`
- Test : `tests/ShowTime.Core.Tests/Settings/HighlightStyleTests.cs` (nouveau),
  `tests/ShowTime.Core.Tests/Settings/SettingsEditorTests.cs`,
  `tests/ShowTime.Infrastructure.IntegrationTests/Settings/JsonSettingsStoreTests.cs`

**Interfaces :**
- Produces :
```csharp
namespace ShowTime.Core.Settings;
public enum StyleUnit { Pixels, Percent, PixelsPerSecond, Milliseconds }
public sealed record HighlightStyleField(string Label, string Help, double Min, double Max, double Step, StyleUnit Unit,
    Func<HighlightStyle, double> Get, Func<HighlightStyle, double, HighlightStyle> With)
{
    public string Format(double value);   // "7 px", "7,5 px", "32 %", "3900 px/s", "180 ms"
}
public sealed record HighlightStyle      // init-only properties, each with its default (Global Constraints)
{
    public double Thickness { get; init; } = 7;   // … MinRatio, FastSpeed, WidthSmoothing, Fluidity, StartTaper, EndTaper, TaperFloor, Opacity
    public static HighlightStyle Default { get; }
    public static IReadOnlyList<HighlightStyleField> Fields { get; }
    public HighlightStyle Clamped();
}
// AppSettings: public HighlightStyle HighlightStyle { get; set; } = HighlightStyle.Default;
// SettingsForm: new last positional parameter `HighlightStyle HighlightStyle`
```

`Fields`, dans cet ordre (les textes d'aide sont ceux du tableau `Sliders` de `tools/highlight-lab.html`, à une
exception près : l'opacité dit « Plus haut : plus couvrant, moins lisible au travers. ») :

| Propriété | Libellé | Min | Max | Pas | Unité |
|---|---|---|---|---|---|
| Thickness | Largeur au repos | 3 | 40 | 0.5 | Pixels |
| MinRatio | Largeur à grande vitesse | 0.1 | 1 | 0.01 | Percent |
| FastSpeed | Vitesse « rapide » | 300 | 6000 | 100 | PixelsPerSecond |
| WidthSmoothing | Douceur des variations | 0 | 300 | 5 | Milliseconds |
| Fluidity | Fluidité des courbes | 0 | 80 | 1 | Milliseconds |
| StartTaper | Effilement du début | 0 | 120 | 1 | Pixels |
| EndTaper | Effilement de fin | 0 | 200 | 1 | Pixels |
| TaperFloor | Finesse des pointes | 0 | 1 | 0.01 | Percent |
| Opacity | Opacité | 0.1 | 1 | 0.01 | Percent |

- [ ] **Step 1 : écrire les tests qui échouent**

```csharp
public sealed class HighlightStyleTests
{
    [Fact]
    public void The_default_is_the_stroke_chosen_in_the_prototype()
    {
        var s = HighlightStyle.Default;
        (s.Thickness, s.MinRatio, s.FastSpeed, s.WidthSmoothing, s.Fluidity).ShouldBe((7, 0.32, 3900, 180, 25));
        (s.StartTaper, s.EndTaper, s.TaperFloor, s.Opacity).ShouldBe((24, 48, 0.25, 0.45));
    }

    [Fact]
    public void Each_field_reads_and_writes_its_own_property_from_a_default_within_bounds()
    {
        HighlightStyle.Fields.Count.ShouldBe(9);
        foreach (var field in HighlightStyle.Fields)
        {
            field.Get(HighlightStyle.Default).ShouldBeInRange(field.Min, field.Max);
            var changed = field.With(HighlightStyle.Default, field.Max);
            field.Get(changed).ShouldBe(field.Max);
            HighlightStyle.Fields.Count(f => f.Get(changed) != f.Get(HighlightStyle.Default)).ShouldBe(1);
        }
    }

    [Fact]
    public void Clamped_brings_back_out_of_range_and_non_finite_values()
    {
        var s = new HighlightStyle { Thickness = -4, Opacity = 5, FastSpeed = double.PositiveInfinity, Fluidity = double.NaN }.Clamped();
        s.Thickness.ShouldBe(3);
        s.Opacity.ShouldBe(1);
        s.FastSpeed.ShouldBe(HighlightStyle.Default.FastSpeed);
        s.Fluidity.ShouldBe(HighlightStyle.Default.Fluidity);
        HighlightStyle.Default.Clamped().ShouldBe(HighlightStyle.Default);
    }

    [Theory]
    [InlineData(0, 7, "7 px")]
    [InlineData(0, 7.5, "7,5 px")]
    [InlineData(1, 0.32, "32 %")]
    [InlineData(2, 3900, "3900 px/s")]
    [InlineData(3, 180, "180 ms")]
    public void Values_are_shown_with_their_unit(int field, double value, string text) =>
        HighlightStyle.Fields[field].Format(value).ShouldBe(text);
}
```

Dans `SettingsEditorTests`, ajouter `From_and_ApplyTo_carry_the_highlight_style_clamped` :
- `From` d'un réglage dont `HighlightStyle` vaut `Default with { Thickness = 12 }` donne `Thickness` 12 ;
- `ApplyTo` d'un formulaire dont `Opacity` vaut 3 écrit `Opacity` 1.

Dans `JsonSettingsStoreTests` :
- ajouter `"HighlightStyle"` aux clés attendues de
  `A_saved_file_has_the_keys_of_v0_1_0_plus_the_schema_version_the_transcription_and_the_highlight`, avec le
  commentaire `// Deliberate change: the highlight style is a new key, which older files simply lack.` ;
- ajouter `The_highlight_style_survives_a_round_trip`, avec `Default with { Thickness = 12.5, Opacity = 0.6 }` ;
- ajouter `A_file_without_the_highlight_style_or_with_part_of_it_keeps_the_defaults` :
  - `{ "Counter": 5 }` donne `HighlightStyle.Default` ;
  - `{ "HighlightStyle": { "Thickness": 12 } }` donne `Default with { Thickness = 12 }`.

- [ ] **Step 2 : lancer les tests et constater l'échec**

Run : `dotnet test --project tests/ShowTime.Core.Tests` puis
`dotnet test --project tests/ShowTime.Infrastructure.IntegrationTests`.
Expected : échec de compilation (`HighlightStyle` n'existe pas).

- [ ] **Step 3 : implémenter**

- **`Clamped()`** parcourt `Fields` : une valeur non finie reprend celle de `Default`, les autres passent par
  `Math.Clamp(Min, Max)`.
- **`Format`** :
  - pixels avec `"0.#"` en `fr-FR` ;
  - pour cent : `Math.Round(value * 100)` suivi de `" %"` ;
  - px/s et ms : `"0"`.
- **Formulaire** : `SettingsForm.From` lit `settings.HighlightStyle`, et `ApplyTo` écrit `HighlightStyle.Clamped()`.
- **Fenêtres de réglages** :
  - Windows : un champ `private HighlightStyle _highlightStyle`, initialisé depuis les réglages et passé en dernier
    argument du `new SettingsForm(...)` de `Save_Click` ;
  - macOS : la même chose dans la méthode d'enregistrement.
- **JSON** : rien à déclarer dans `SettingsJsonContext`. Le générateur découvre le type de la propriété, et le build ne
  doit émettre aucun avertissement `SYSLIB`.

- [ ] **Step 4 : lancer les tests et constater qu'ils passent**

Les deux commandes du Step 2, puis `dotnet build ShowTime.slnx -c Release`. Expected : tout passe, 0 avertissement.

- [ ] **Step 5 : commit** — `Store the highlight style in the settings`

---

### Task 2 : `BrushStroke` et le labo

**Files :**
- Create : `tools/highlight-lab.html` (depuis
  `C:\Users\renau\AppData\Local\Temp\claude\C--Users-renau-RiderProjects-ShowTime\32ee06fd-5d2b-41aa-86a2-3fcc7cfd92e1\scratchpad\brush-prototype.html`),
  `src/ShowTime.Core/Recording/BrushStroke.cs`
- Test : `tests/ShowTime.Core.Tests/Recording/BrushStrokeTests.cs`

**Interfaces :**
- Consumes : `HighlightStyle` (Task 1), `PointD` (`ShowTime.Core.Editor`), `PixelPoint`, `PixelRect`.
- Produces :
```csharp
namespace ShowTime.Core.Recording;
public readonly record struct TrailSample(PixelPoint Point, TimeSpan Time);
public readonly record struct StrokeDisc(double X, double Y, double Radius);
public static class BrushStroke
{
    public const double Step = 3;          // pixels at 100 %, between two discs along the curve
    public const int AntialiasMargin = 2;  // pixels added around a change
    public static IReadOnlyList<StrokeDisc> Build(IReadOnlyList<TrailSample> samples, HighlightStyle style, double scale, bool lifted);
    public static PointD[]? Hull(StrokeDisc a, StrokeDisc b);
    public static PixelRect? Changed(IReadOnlyList<StrokeDisc> previous, IReadOnlyList<StrokeDisc> current);
    /// <summary>The indices i whose piece, disc i and its hull to disc i + 1, touches the area.</summary>
    public static IEnumerable<int> PiecesTouching(IReadOnlyList<StrokeDisc> stroke, PixelRect area);
}
```

- [ ] **Step 1 : verser le labo dans le dépôt**

Copier le prototype vers `tools/highlight-lab.html` et y faire ces changements :
- `Defaults` reçoit le style par défaut des Global Constraints ;
- le minimum du curseur `thickness` passe à 3 ;
- la clé de stockage local devient `"highlight-lab"` ;
- un commentaire d'en-tête : `ShowTime's highlight lab: tries stroke options before they are ported. The core
  (BrushStroke, HighlightStyle) is the reference; this page may run ahead of it.` ;
- dans `smoothSamples`, le découpage en tronçons du Step 3 (une pause plus longue que 3σ termine un tronçon).

- [ ] **Step 2 : écrire les tests qui échouent**

```csharp
private static readonly HighlightStyle NoTaper = HighlightStyle.Default with { StartTaper = 0, EndTaper = 0 };

private static List<TrailSample> Line(int count, int stepPixels, double stepMs, int x0 = 0, double t0 = 0) =>
    [.. Enumerable.Range(0, count).Select(i => new TrailSample(new PixelPoint(x0 + i * stepPixels, 0), TimeSpan.FromMilliseconds(t0 + i * stepMs)))];

[Fact] public void A_slow_stroke_has_almost_the_full_width() =>          // 3 px per 16 ms ≈ 190 px/s
    BrushStroke.Build(Line(30, 3, 16), NoTaper, 1, false)[^1].Radius.ShouldBeInRange(3.1, 3.5);

[Fact] public void A_fast_stroke_is_thinner_but_never_below_MinRatio()   // 80 px per 16 ms = 5000 px/s
{
    var stroke = BrushStroke.Build(Line(40, 80, 16), NoTaper, 1, false);
    var floor = 0.32 * 7 / 2;
    stroke[^1].Radius.ShouldBeInRange(floor - 1e-9, floor * 1.1);
    stroke.ShouldAllBe(d => d.Radius >= floor - 1e-9);
}

[Fact] public void The_width_eases_into_an_acceleration()
{
    var samples = Line(10, 3, 16).Append(new TrailSample(new PixelPoint(27 + 80, 0), TimeSpan.FromMilliseconds(160))).ToList();
    var eased = BrushStroke.Build(samples, NoTaper, 1, false)[^1].Radius;
    var instant = BrushStroke.Build(samples, NoTaper with { WidthSmoothing = 0 }, 1, false)[^1].Radius;
    eased.ShouldBeGreaterThan(instant);
    eased.ShouldBeLessThan(BrushStroke.Build(Line(10, 3, 16), NoTaper, 1, false)[^1].Radius);
}

[Fact] public void The_same_gesture_gives_the_same_stroke_at_any_scale()   // doubled pixels at scale 2
{
    var at1 = BrushStroke.Build(Line(20, 7, 16), HighlightStyle.Default, 1, true);
    var at2 = BrushStroke.Build(Line(20, 14, 16), HighlightStyle.Default, 2, true);
    at2.Count.ShouldBe(at1.Count);
    for (var i = 0; i < at1.Count; i++)
    {
        at2[i].X.ShouldBe(2 * at1[i].X, 1e-6);
        at2[i].Radius.ShouldBe(2 * at1[i].Radius, 1e-6);
    }
}

[Fact] public void Fluidity_makes_a_shaky_circle_turn_steadily_and_ends_under_the_mouse()
{
    var circle = ShakyCircle();   // radius 150, 600 ms, ±2 px and ±5 ms of seeded noise (Park-Miller, seed 1)
    var smooth = BrushStroke.Build(circle, HighlightStyle.Default, 1, false);
    TurnSpread(smooth).ShouldBeLessThan(0.6 * TurnSpread(BrushStroke.Build(circle, HighlightStyle.Default with { Fluidity = 0 }, 1, false)));
    (smooth[0].X, smooth[0].Y).ShouldBe((circle[0].Point.X, circle[0].Point.Y));
    (smooth[^1].X, smooth[^1].Y).ShouldBe((circle[^1].Point.X, circle[^1].Point.Y));
}
// TurnSpread: the standard deviation, in degrees, of the angle between consecutive steps of the discs' centres.

[Fact] public void A_pause_keeps_a_sharp_corner()          // right, 300 ms still, then down
{
    var samples = Line(11, 10, 16).Concat(Enumerable.Range(1, 10).Select(i =>
        new TrailSample(new PixelPoint(100, i * 10), TimeSpan.FromMilliseconds(460 + i * 16)))).ToList();
    BrushStroke.Build(samples, HighlightStyle.Default, 1, false).ShouldContain(d => Math.Abs(d.X - 100) < 0.5 && Math.Abs(d.Y) < 0.5);
}

[Fact] public void Samples_at_the_same_instant_give_finite_radii() =>
    BrushStroke.Build([new(new(0, 0), TimeSpan.Zero), new(new(10, 0), TimeSpan.Zero), new(new(20, 0), TimeSpan.FromMilliseconds(16))],
        HighlightStyle.Default, 1, true).ShouldAllBe(d => double.IsFinite(d.Radius) && d.Radius > 0);

[Fact] public void A_stroke_that_doubles_back_stays_finite_and_unbroken()   // x: 0, 40, 0, 40… every 16 ms
{
    var stroke = BrushStroke.Build(ZigZag(20), HighlightStyle.Default, 1, true);
    stroke.ShouldAllBe(d => double.IsFinite(d.X) && double.IsFinite(d.Y) && double.IsFinite(d.Radius));
    stroke.Zip(stroke.Skip(1)).ShouldAllBe(p => Math.Hypot(p.Second.X - p.First.X, p.Second.Y - p.First.Y) <= 2 * BrushStroke.Step);
}

[Fact] public void Every_style_at_its_bounds_gives_a_finite_stroke()
{
    foreach (var bound in new Func<HighlightStyleField, double>[] { f => f.Min, f => f.Max })
    {
        var style = HighlightStyle.Fields.Aggregate(HighlightStyle.Default, (s, f) => f.With(s, bound(f)));
        var stroke = BrushStroke.Build(ShakyCircle(), style, 1, true);
        stroke.ShouldNotBeEmpty();
        stroke.ShouldAllBe(d => double.IsFinite(d.X) && double.IsFinite(d.Radius) && d.Radius >= 0);
    }
}

[Fact] public void A_press_without_moving_is_a_dot_of_TaperFloor() =>
    BrushStroke.Build([new(new(50, 60), TimeSpan.Zero)], HighlightStyle.Default, 1, false)
        .ShouldBe([new StrokeDisc(50, 60, 7 / 2.0 * 0.25)]);

[Fact] public void The_start_tapers_from_the_press_and_the_end_only_once_lifted()
{
    var held = BrushStroke.Build(Line(40, 3, 16), HighlightStyle.Default, 1, false);
    var lifted = BrushStroke.Build(Line(40, 3, 16), HighlightStyle.Default, 1, true);
    held[0].Radius.ShouldBeLessThan(0.3 * held[held.Count / 2].Radius);
    held[^1].Radius.ShouldBe(held[held.Count / 2].Radius, 0.2);
    lifted[^1].Radius.ShouldBeLessThan(0.3 * lifted[lifted.Count / 2].Radius);
}

[Fact] public void The_hull_of_equal_discs_is_a_rectangle() =>
    BrushStroke.Hull(new(0, 0, 5), new(10, 0, 5)).ShouldBe([new PointD(0, 5), new PointD(10, 5), new PointD(10, -5), new PointD(0, -5)]);

[Fact] public void There_is_no_hull_when_a_disc_holds_the_other() =>
    BrushStroke.Hull(new(0, 0, 5), new(1, 0, 2)).ShouldBeNull();

[Fact] public void The_hull_touches_both_discs()            // each corner lies on its disc's circle
{
    var (a, b) = (new StrokeDisc(0, 0, 6), new StrokeDisc(30, 10, 2));
    var hull = BrushStroke.Hull(a, b)!;
    new[] { hull[0], hull[3] }.ShouldAllBe(p => Math.Abs(Math.Hypot(p.X - a.X, p.Y - a.Y) - a.Radius) < 1e-9);
    new[] { hull[1], hull[2] }.ShouldAllBe(p => Math.Abs(Math.Hypot(p.X - b.X, p.Y - b.Y) - b.Radius) < 1e-9);
}

[Fact] public void Changed_is_null_for_the_same_stroke()
[Fact] public void Changed_from_nothing_covers_the_stroke()            // bounds of all discs, ± AntialiasMargin
[Fact] public void Changed_covers_only_the_tail_when_the_stroke_grows() // Line(41, …) vs Line(40, …): X > 50, contains the last disc
[Fact] public void Changed_covers_both_strokes_when_the_head_is_removed()
[Fact] public void PiecesTouching_finds_the_pieces_over_the_area()      // discs at x 0, 100, 200 r 5: (95..105) → [0, 1]; (300..310) → []
```

- [ ] **Step 3 : lancer les tests et constater l'échec**

Run : `dotnet test --project tests/ShowTime.Core.Tests`. Expected : échec de compilation.

- [ ] **Step 4 : implémenter `BrushStroke`**

Porter, une fonction pour une fonction, `smoothSamples`, `sampleWidths`, `catmullRom`, `centerline`, `taper`,
`buildStroke`, `hull` et `changed` du labo, en méthodes privées et en millisecondes `double`. L'algorithme est celui
de la spec ; une seule chose change par rapport au prototype.

**La fluidité lisse par tronçons.** Les échantillons sont découpés là où deux relevés consécutifs sont espacés de plus
de 3σ, puis chaque tronçon est lissé à part, avec sa fenêtre symétrique qui se resserre jusqu'à ses propres bouts. Les
deux bouts d'un tronçon restent en place : l'appui, le curseur, et les deux côtés d'une pause. C'est ce qui garde
l'angle net (`A_pause_keeps_a_sharp_corner`). Sans ce découpage, le point d'angle reculait d'environ 9 px.

Le reste :
- `Changed` compare les disques exactement : le calcul est déterministe. Son rectangle est en `floor`/`ceil`,
  ± `AntialiasMargin`.
- `PiecesTouching` teste le rectangle englobant du disque i et du disque i + 1.

- [ ] **Step 5 : lancer les tests et constater qu'ils passent** — même commande. Expected : PASS.

- [ ] **Step 6 : commit** — `Compute the highlight as a brush stroke`

---

### Task 3 : overlay Windows à transparence par pixel

**Files :**
- Modify : `src/ShowTime.Windows/Recording/HighlightOverlay.cs`, `src/ShowTime.Windows/Interop/Native.cs`,
  `src/ShowTime.Windows/ShowTime.Windows.csproj` (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, comme
  `ShowTime.Platform.Windows`)
- Test : `tests/ShowTime.Windows.Tests/Recording/HighlightOverlayTests.cs`

**Interfaces :**
- Le port ne change pas ici : `Draw(points)` dessine encore la polyligne, cette fois anticrénelée.
- Produces : `internal double TrailOpacity { get; }`, ainsi que
  `internal System.Drawing.Color SurfacePixel(int x, int y)` (couleur en alpha droit, coordonnées client).

- [ ] **Step 1 : écrire les tests**

```csharp
[Fact]
public async Task The_trail_is_antialiased_without_a_fringe() => await Sta.Run(() =>
{
    using var overlay = new HighlightOverlay(new Rectangle(0, 0, 200, 200));
    overlay.Draw([new PixelPoint(20, 30), new PixelPoint(180, 170)]);
    var pixels = (from x in Enumerable.Range(0, 200) from y in Enumerable.Range(0, 200) select overlay.SurfacePixel(x, y)).ToList();
    pixels.ShouldContain(c => c.A == 255);
    pixels.ShouldContain(c => c.A > 0 && c.A < 255);
    pixels.Where(c => c.A >= 64).ShouldAllBe(c => c.R >= 250 && Math.Abs(c.G - 212) <= 6 && c.B <= 6);
});

[Fact]
public async Task Hiding_clears_the_surface() => …   // after Draw then Hide, every SurfacePixel has A == 0
```

`Opacity` devient `TrailOpacity` dans `The_overlay_is_click_through_never_activated_and_captured` et dans
`Opacity_changes_and_hiding_hides_the_trail`.

- [ ] **Step 2 : implémenter**

**`Native`**, au style `DllImport` du fichier :
- les structures `BLENDFUNCTION`, `SIZE`, `POINT`, `BITMAPINFOHEADER` et `UPDATELAYEREDWINDOWINFO` (champs pointeurs
  en `void*`) ;
- les fonctions `CreateCompatibleDC`, `CreateDIBSection`, `SelectObject`, `DeleteObject`, `DeleteDC`,
  `UpdateLayeredWindow` et `UpdateLayeredWindowIndirect` ;
- les constantes `AC_SRC_OVER` = 0, `AC_SRC_ALPHA` = 1 et `ULW_ALPHA` = 2.

**`HighlightOverlay`** :
- Supprimer `BackColor`, `TransparencyKey`, `DoubleBuffered` et `OnPaint`, et ne jamais toucher `Form.Opacity` :
  tous passent par `SetLayeredWindowAttributes`.
- Dans le constructeur, créer la surface : un DC mémoire, puis une DIB 32 bits de haut en bas (`biHeight` négatif) de
  la taille de la zone, sélectionnée dans ce DC et enveloppée par `new Bitmap(w, h, w * 4, Format32bppPArgb, bits)`.
- `Draw` :
  - garde le calcul du rectangle actuel : la queue si le trait grandit, sinon tout ;
  - efface ce rectangle (`CompositingMode.SourceCopy`, transparent) ;
  - redessine la polyligne avec un clip, en `SmoothingMode.AntiAlias`, en jaune opaque, avec bouts et jointures
    arrondis ;
  - pousse ce rectangle par `UpdateLayeredWindowIndirect`, avec `prcDirty`, la position de la fenêtre et une
    `SourceConstantAlpha` qui vaut `round(opacité × 255)`.
- La première fois, `Show()` passe avant la poussée de la surface entière.
- `SetOpacity` mémorise la valeur ; si la fenêtre est visible, elle appelle `UpdateLayeredWindow` avec `hdcSrc` nul
  et le seul `BLENDFUNCTION`. Si cet appel échoue, elle repousse la surface entière.
- `Hide` vide la surface. `Dispose` libère le `Graphics`, le `Bitmap`, la DIB et le DC.

- [ ] **Step 3 : vérifier**

`dotnet build ShowTime.slnx -c Release` puis `dotnet format ShowTime.slnx --verify-no-changes`. Les tests Windows
tournent sur la CI à la fin de la Task 4.

- [ ] **Step 4 : commit** — `Draw the highlight with per-pixel alpha on Windows`

---

### Task 4 : le trait de pinceau à l'écran (cœur, puis les deux overlays)

**Files :**
- Modify : `src/ShowTime.Core/Recording/HighlightTrail.cs`, `src/ShowTime.Core/Recording/Highlighter.cs`,
  `src/ShowTime.Core/Ports/IHighlightPorts.cs`, `src/ShowTime.Windows/Recording/HighlightOverlay.cs`,
  `src/ShowTime.Mac/Recording/MacHighlightOverlay.cs`, `tests/ShowTime.Core.Tests/Fakes.cs`
- Test : `tests/ShowTime.Core.Tests/Recording/HighlightTrailTests.cs`,
  `tests/ShowTime.Core.Tests/Recording/HighlighterTests.cs`, `tests/ShowTime.Windows.Tests/Recording/HighlightOverlayTests.cs`

**Interfaces :**
- Consumes : `BrushStroke`, `TrailSample`, `StrokeDisc` (Task 2), `HighlightStyle` (Task 1), la surface Windows
  (Task 3).
- Produces :
```csharp
// HighlightTrail: FullOpacity is removed; MinStep, MaxPoints, FadeDelay and FadeDuration are unchanged.
public IReadOnlyList<TrailSample> Samples { get; }
public void Begin(PixelPoint at, TimeSpan time);
public bool Add(PixelPoint at, TimeSpan time);
public static double FadeOpacity(TimeSpan sinceRelease, double fullOpacity);

// IHighlightOverlay
/// <summary>The scale of the screen holding the area: 1 at 100 %.</summary>
double Scale { get; }
/// <summary>Shows the overlay if it is hidden and repaints <paramref name="changed"/>, in screen pixels, from the stroke.</summary>
void Draw(IReadOnlyList<StrokeDisc> stroke, PixelRect changed);
void SetOpacity(double opacity);
/// <summary>Hides the overlay and clears what it drew.</summary>
void Hide();
```
- Le constructeur de `Highlighter` ne change pas. **À chaque image**, il lit `settings.HighlightStyle.Clamped()`,
  construit `BrushStroke.Build(Samples, style, overlay.Scale, lifted: !held)` et n'appelle `Draw` que si `Changed`
  (comparé au dernier trait dessiné) n'est pas null.
- Il remet l'opacité à jour quand celle du style change pendant l'appui. Il horodate chaque échantillon depuis l'appui
  (`clock.GetElapsedTime`). Il vide le dernier trait dessiné au moment de `Hide`.

- [ ] **Step 1 : écrire les tests qui échouent (cœur)**

**`Fakes.cs`** :
- `FakeHighlightOverlay` reçoit `Scale { get; set; } = 1`, et `Draws` devient
  `List<(IReadOnlyList<StrokeDisc> Stroke, PixelRect Changed)>` ;
- `FakeHighlightOverlayFactory` reçoit `Scale`, qu'elle donne aux overlays qu'elle crée.

**`HighlightTrailTests`** :
- les tests de décimation et de plafond passent aux `Samples` (`.Point`) ;
- `The_trail_is_light_enough_to_read_through` est supprimé ;
- ajouter `Samples_keep_the_time_they_were_read_at` ;
- la théorie du fondu prend `fullOpacity` 0.45 : (0, 0.45), (300, 0.45), (550, 0.1125), (800, 0) et (2000, 0).

**`HighlighterTests`** : `FullOpacity` devient `HighlightStyle.Default.Opacity` ; les tests existants lisent
`Draws[^1].Stroke`. Ajouter :
- `Holding_draws_the_brush_stroke_of_the_samples` :
  - après l'appui, `Draws[^1].Stroke` vaut
    `BrushStroke.Build([new(new(100, 100), TimeSpan.Zero)], HighlightStyle.Default, 1, false)` ;
  - après un déplacement à (110, 100) et une image, il vaut le `Build` de ces deux échantillons, le second au temps
    `Highlighter.FrameInterval`.
- `The_stroke_is_built_at_the_overlay_scale` : avec une fabrique à `Scale` 2, `Build(…, 2, false)`.
- `Releasing_redraws_once_with_the_lifted_end` : `Draws` grandit d'un élément, qui vaut `Build(samples, …, lifted: true)`.
- `A_style_changed_while_held_restyles_without_moving` : `_settings.HighlightStyle = Default with { Thickness = 20,
  Opacity = 0.8 }`, puis une image : un nouveau `Draw` au trait du nouveau style, et `Overlay.Opacity` à 0.8.
- `The_fade_starts_from_the_style_opacity` : avec `Opacity` 0.6, l'opacité est entre 0.14 et 0.16 au bout de 550 ms.
- `An_out_of_range_style_is_clamped` : avec `Opacity` 5, `Overlay.Opacity` vaut 1.
- `A_new_press_during_the_fade_repaints_over_the_old_stroke` : le premier `Changed` du nouveau trait contient le
  rectangle de l'ancien.
- `After_hiding_a_new_stroke_repaints_only_itself` : une fois le fondu fini, l'appui à (500, 500) donne un `Changed`
  dont `X` est ≥ 490.

- [ ] **Step 2 : lancer les tests et constater l'échec**

Run : `dotnet test --project tests/ShowTime.Core.Tests`. Expected : échec de compilation.

- [ ] **Step 3 : implémenter le cœur, puis lancer les tests jusqu'au vert**

`HighlightTrail` stocke des `TrailSample`. Le port change, puis `Highlighter` suit la règle des Interfaces.

- [ ] **Step 4 : overlay Windows**

- `Scale => DeviceDpi / 96.0` ; la constante `Thickness` disparaît.
- `Draw(stroke, changed)` :
  - ramène `changed` en coordonnées client, intersecté avec la surface ;
  - l'efface ;
  - pour chaque `i` de `BrushStroke.PiecesTouching(stroke, changed)`, remplit le disque `i` (`FillEllipse`) et
    `Hull(i, i + 1)` (`FillPolygon`), en jaune opaque, avec un clip ;
  - pousse le rectangle.
- Mise à jour des tests :
  - le premier test vérifie `overlay.Scale` au lieu de `Thickness` ;
  - le test d'anticrénelage dessine `BrushStroke.Build` d'une diagonale avec `Default with { Thickness = 20 }` ;
  - `Points_outside_the_area_are_accepted` dessine le trait de (−500, −500) à (5000, 5000), avec un `changed` qui
    déborde ;
  - le test vidéo dessine le trait de (20, 60) à (150, 60), avec `Thickness` 14 et `HighlightStyle.Default.Opacity`.
    Ses seuils tiennent : 45 % de `#FFD400` sur du noir donne environ (115, 95, 0).

- [ ] **Step 5 : overlay macOS**

- `Scale` est `screen.Frame.Scale`, et `TrailView` garde le `Stroke`.
- `Draw` convertit `changed` en points dans la vue, puis appelle `SetNeedsDisplayInRect` :
  - x = (X − aire.X) / scale ;
  - y = (aire.Bottom − Bottom) / scale ;
  - largeur et hauteur divisées par scale.
  
  À la première apparition, elle appelle `Display()` puis `OrderFrontRegardless`, comme aujourd'hui.
- `DrawRect(dirty)` :
  - efface `dirty` (`NSColor.Clear.Set()`, puis `NSGraphics.RectFill(dirty, NSCompositingOperation.Copy)`, binding à
    vérifier) ;
  - reconvertit `dirty` en pixels ;
  - remplit séparément chaque disque (ovale) et chaque enveloppe (polygone) de `PiecesTouching`.
- `Hide` vide le `Stroke`, invalide toute la vue, puis appelle `OrderOut`.

- [ ] **Step 6 : vérifier**

1. Build et format.
2. `git push -u origin feature/brush-highlight`, puis `gh workflow run CI --ref feature/brush-highlight`.
3. Expected : la CI est verte sur Windows (dont `HighlightOverlayTests`) et sur macOS (build de l'app). Sur un échec de
   `MacAudioPipeTests` seul : `gh run rerun <id> --failed`.

- [ ] **Step 7 : commit** — `Draw the highlight as a brush stroke` (avant le push du Step 6)

---

### Task 5 : la fenêtre « Trait du surlignage » (Windows)

**Files :**
- Create : `src/ShowTime.Windows/Settings/HighlightStyleWindow.xaml` et `.xaml.cs`,
  `src/ShowTime.Windows/Settings/PreviewHighlightInput.cs`
- Modify : `src/ShowTime.Windows/Settings/SettingsWindow.xaml` (une ligne 10 sous l'avertissement),
  `src/ShowTime.Windows/Settings/SettingsWindow.xaml.cs` (`SettingsContext` et le bouton)
- Test : `tests/ShowTime.Windows.Tests/Settings/HighlightStyleWindowTests.cs`

**Interfaces :**
- Consumes : `Highlighter`, `IHighlightOverlayFactory`, `HighlightStyle.Fields` et `Format`.
- Produces :
```csharp
internal sealed class PreviewHighlightInput : IHighlightInput
{
    // Register keeps `pressed` and returns true, whatever the trigger; Unregister forgets it.
    public void Press();                                                          // runs `pressed`, if registered
    public bool IsHeld => (Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0;
    public PixelPoint Cursor => new(Forms.Cursor.Position.X, Forms.Cursor.Position.Y);
}
public partial class HighlightStyleWindow : Window
{
    internal HighlightStyleWindow(HighlightStyle style, SettingsDefaults defaults, IHighlightOverlayFactory overlays, IUiThread uiThread, TimeProvider clock);
    internal HighlightStyle Style { get; }            // the draft, to read after ShowDialog() == true
    internal IReadOnlyList<Slider> Sliders { get; }   // one per HighlightStyle.Fields, in order
    internal void ResetToDefaults();                  // the "Valeurs par défaut" button
}
// SettingsContext gains: IHighlightOverlayFactory Overlays, IUiThread UiThread, TimeProvider Clock, IRecordingStatus Recording
```

**Comportement :**
- **La fenêtre** : titre « ShowTime — Trait du surlignage », `SizeToContent`, `NoResize`.
- **Les curseurs** :
  - chacun prend le `Min`, le `Max` et le `TickFrequency` (`Step`) de son champ, avec `IsSnapToTickEnabled` ;
  - au-dessus, son libellé et `Format(valeur)` ; en dessous, l'aide ;
  - `ValueChanged` fait `Style = field.With(Style, valeur)` et `_preview.HighlightStyle = Style`.
- **L'aperçu** :
  - `_preview = new AppSettings(defaults) { HighlightStyle = style }` ;
  - `_highlighter = new Highlighter(_preview, _input, overlays, uiThread, clock, HotkeyNaming.Windows)` ;
  - un appui gauche dans la zone calcule l'aire en pixels physiques (`PointToScreen` des deux coins), appelle
    `_highlighter.Enable(area)` puis `_input.Press()`, et capture la souris, relâchée au bouton levé ;
  - `Closed` appelle `_highlighter.Dispose()`.
- **La zone** : deux `Border` côte à côte, `#FFFFFF` et `#1D2028`, chacun avec un titre, deux lignes de texte et un
  faux bouton, de la taille de la zone du labo.
- **Les boutons** : « Valeurs par défaut » à gauche, « Annuler » (`IsCancel`), « OK » (`IsDefault`, qui met
  `DialogResult` à true).
- **Dans les réglages** :
  - un `Button` « Personnaliser le trait… », activé si `!Recording.IsBusy` ;
  - son clic ouvre `new HighlightStyleWindow(_highlightStyle, …) { Owner = this }`, et `ShowDialog() == true` donne
    `_highlightStyle = window.Style`.
  - `SettingsContext` reste résolu par l'injection de dépendances : ses nouveaux paramètres y sont déjà enregistrés.

- [ ] **Step 1 : écrire les tests** (STA, sans `Show`, avec `new DispatcherUiThread(Dispatcher.CurrentDispatcher)`,
  `new HighlightOverlayFactory()` et `TimeProvider.System`)

- `Each_field_gets_a_slider_with_its_bounds_and_value` : sur `Default with { Thickness = 12 }`, il y a
  `Fields.Count` curseurs, et `Sliders[0]` a `Minimum` 3, `Maximum` 40 et `Value` 12.
- `Moving_a_slider_changes_the_style` : `Sliders[8].Value = 0.7` donne un `Style.Opacity` de 0.7.
- `Defaults_restore_the_default_style` : après un changement, `ResetToDefaults()` donne `Style == HighlightStyle.Default`
  et `Sliders[0].Value == 7`.

- [ ] **Step 2 : implémenter**, puis `dotnet build` et `dotnet format`.

- [ ] **Step 3 : vérifier par la CI** (`gh workflow run CI --ref feature/brush-highlight`). Expected : vert, y compris
  `CompositionTests`.

- [ ] **Step 4 : commit** — `Tune the highlight stroke from the settings on Windows`

---

### Task 6 : la fenêtre « Trait du surlignage » (macOS)

**Files :**
- Create : `src/ShowTime.Mac/Settings/HighlightStyleWindowController.cs`,
  `src/ShowTime.Mac/Settings/PreviewHighlightInput.cs`
- Modify : `src/ShowTime.Mac/Settings/SettingsWindowController.cs` (`SettingsContext`, la ligne, la feuille)

**Interfaces :**
- Consumes : les mêmes que la Task 5.
- Produces : `HighlightStyleWindowController(HighlightStyle style, SettingsDefaults defaults, IHighlightOverlayFactory
  overlays, IUiThread uiThread, TimeProvider clock)` et sa propriété `Style`. `SettingsContext` reçoit les mêmes quatre
  services que sous Windows.

**Comportement :**
- Le même que la Task 5, avec `HotkeyNaming.MacOS`.
- **Les curseurs** : `NSSlider`, avec `MinValue`, `MaxValue` et la valeur arrondie au `Step` dans le gestionnaire.
- **La zone** : une sous-classe de `NSView` qui dessine ses deux moitiés et lève `Pressed` sur `MouseDown`.
- **L'aire** : le cadre de la zone passe en écran (`ConvertRectToView(…, null)`, puis `ConvertRectToScreen`), puis en
  pixels par `MacScreenSpace.FromCocoa`, comme le fait `SelectionOverlay`.
- **La fenêtre s'ouvre en feuille** (`BeginSheet`) de la fenêtre des réglages, et non par `RunModal` : les images du
  `Highlighter` doivent continuer de tourner. « OK » met fin à la feuille et met à jour `_highlightStyle`.
- **`PreviewHighlightInput`** :
  - `IsHeld` vaut `(NSEvent.CurrentPressedMouseButtons & 1) != 0` ;
  - `Cursor` passe par `MacScreenSpace.PointToPixel`, avec les écrans de `ScreenInfo.Current()` lus dans `Register`
    (comme `MacHighlightInput`).
- **Bindings** : vérifier `NSEvent.CurrentPressedMouseButtons`, `NSWindow.BeginSheet` et `NSWindow.EndSheet` dans
  `Microsoft.macOS.Ref`.

- [ ] **Step 1 : implémenter** (l'app Mac n'a pas de tests automatisés ; ce qui est testable est déjà dans le cœur).
- [ ] **Step 2 : vérifier par la CI**. Expected : le build macOS est vert.
- [ ] **Step 3 : commit** — `Tune the highlight stroke from the settings on macOS`

---

### Task 7 : documentation

**Files :** `CLAUDE.md`, `README.md`, `docs/test-checklist.md`

- [ ] **Step 1 : CLAUDE.md**, puce « Highlight » de la section Video, à réécrire :
  - **le trait** : `BrushStroke` produit des disques à partir des échantillons horodatés. La largeur suit la vitesse
    (en pixels « à 100 % »), elle est adoucie dans le temps, la fluidité lisse par tronçons séparés par les pauses, la
    courbe est une Catmull-Rom centripète et les bouts s'effilent. `Changed` limite chaque repeinte ;
  - **le style** : `HighlightStyle` vit dans settings.json, et `Fields` décrit les curseurs des deux apps ;
  - **l'overlay Windows** : alpha par pixel (DIB et `UpdateLayeredWindowIndirect`), le fondu par `SourceConstantAlpha`,
    sans couleur clé ;
  - **la fenêtre d'aperçu** : un vrai `Highlighter` sur un `AppSettings` à elle, ouverte en feuille sur Mac ;
  - **le labo** : `tools/highlight-lab.html`.
- [ ] **Step 2 : README.md** : une phrase après le tableau des raccourcis, « Le trait du surlignage (largeur, finesse
  à grande vitesse, fluidité, effilements, opacité) se règle dans Réglages › Personnaliser le trait…, avec un
  aperçu. »
- [ ] **Step 3 : `docs/test-checklist.md`**. Mettre à jour les lignes 59, 62 et 63, et 168 et 169 :
  - un trait lent est large et un trait rapide fin ;
  - un cercle est fluide et ses bords lisses, sans frange rose ;
  - les croisements ne foncent pas et le texte reste lisible ;
  - à 150 %, l'épaisseur apparente est la même.

  Ajouter sur chaque plateforme :
  - « Personnaliser le trait… » : tracer dans l'aperçu, bouger un curseur pendant le tracé, « Valeurs par défaut »,
    « Annuler » sans effet, « OK » puis « Enregistrer » ;
  - un enregistrement suivant porte le style choisi ;
  - le bouton est grisé pendant un enregistrement.
- [ ] **Step 4 : commit** — `Document the brush highlight`

---

### Task 8 : vérification finale et PR

- [ ] **Step 1 : build et format.** `dotnet build ShowTime.slnx -c Release` et
  `dotnet format ShowTime.slnx --verify-no-changes`. Expected : 0 avertissement, aucun changement.
- [ ] **Step 2 : tests locaux et couverture.** Lancer :
  - `dotnet test --project tests/ShowTime.Core.Tests --coverage --coverage-output-format cobertura --coverage-output core.cobertura.xml` ;
  - `python build/check-coverage.py TestResults/core.cobertura.xml 90` ;
  - `dotnet test --project tests/ShowTime.Infrastructure.IntegrationTests`.

  Expected : vert, couverture ≥ 90 %.
- [ ] **Step 3 : CI.** `git push`, puis `gh workflow run CI --ref feature/brush-highlight`. Expected : vert sur
  Windows et macOS.
- [ ] **Step 4 : vérification à la main, par l'utilisateur** (Windows, puis macOS avec l'artefact `ShowTime-macos`).
  Les points de la checklist, puis `ffmpeg -i video.mp4 -vf fps=10 images/%03d.png` pour voir le trait, ses largeurs
  et le fondu dans la vidéo.
- [ ] **Step 5 : PR.**
  - `gh pr create` : résumé, captures si l'utilisateur en fournit, lignes d'attribution.
  - Après la CI verte et l'accord de l'utilisateur : `gh pr merge --merge --delete-branch`, puis suppression de la
    branche locale.
  - La version (0.6.0) se décide à la release, par l'utilisateur.
