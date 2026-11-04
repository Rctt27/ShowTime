using Microsoft.Extensions.Time.Testing;
using ShowTime.Core.Geometry;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;

namespace ShowTime.Core.Tests.Recording;

public sealed class HighlighterTests
{
    private static readonly PixelRect Area = new(0, 0, 800, 600);

    private readonly AppSettings _settings = new(SettingsDefaults.ForWindows("C:\\Out"));
    private readonly FakeHighlightInput _input = new();
    private readonly FakeHighlightOverlayFactory _overlays = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly Highlighter _highlighter;

    public HighlighterTests() =>
        _highlighter = new Highlighter(_settings, _input, _overlays, new ImmediateUiThread(), _clock, HotkeyNaming.Windows);

    private FakeHighlightOverlay Overlay => _overlays.Overlay!;

    private static TrailSample At(int x, int y, TimeSpan time) => new(new PixelPoint(x, y), time);

    private static IReadOnlyList<StrokeDisc> Stroke(bool lifted, params TrailSample[] samples) =>
        BrushStroke.Build(samples, HighlightStyle.Default, 1, lifted);

    private static bool Inside(StrokeDisc d, PixelRect r) =>
        d.X - d.Radius >= r.X && d.X + d.Radius <= r.Right && d.Y - d.Radius >= r.Y && d.Y + d.Radius <= r.Bottom;

    private void HoldAt(PixelPoint point)
    {
        _highlighter.Enable(Area);
        _input.Cursor = point;
        _input.IsHeld = true;
        _input.Press();
    }

    private void Advance(TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Highlighter.FrameInterval)
            _clock.Advance(Highlighter.FrameInterval);
    }

    private void Release()
    {
        _input.IsHeld = false;
        _clock.Advance(Highlighter.FrameInterval);
    }

    [Fact]
    public void Enabling_registers_the_trigger_and_returns_how_the_bar_shows_it()
    {
        _highlighter.Enable(Area).ShouldBe("Ctrl+Space");
        _input.Registered.ShouldBe(HighlightTrigger.FromKey(new Hotkey(HotkeyModifiers.Control, KeyCode.Space)));
        _overlays.Area.ShouldBe(Area);
        Overlay.Visible.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Foo")]
    public void An_empty_or_invalid_trigger_enables_nothing(string text)
    {
        _settings.HighlightTrigger = text;
        _highlighter.Enable(Area).ShouldBeNull();
        _input.Registered.ShouldBeNull();
        _overlays.Overlay.ShouldBeNull();
    }

    [Fact]
    public void A_refused_trigger_enables_nothing()
    {
        _input.Refused = true;
        _highlighter.Enable(Area).ShouldBeNull();
        _overlays.Overlay.ShouldBeNull();
    }

    [Fact]
    public void A_side_button_is_shown_by_its_French_name()
    {
        _settings.HighlightTrigger = "XButton1";
        _highlighter.Enable(Area).ShouldBe("Souris 4");
    }

    [Fact]
    public void Holding_draws_the_brush_stroke_of_the_samples()
    {
        HoldAt(new PixelPoint(100, 100));
        Overlay.Visible.ShouldBeTrue();
        Overlay.Opacity.ShouldBe(HighlightStyle.Default.Opacity);
        Overlay.Draws[^1].Stroke.ShouldBe(Stroke(false, At(100, 100, TimeSpan.Zero)));

        _input.Cursor = new PixelPoint(110, 100);
        _clock.Advance(Highlighter.FrameInterval);
        Overlay.Draws[^1].Stroke.ShouldBe(Stroke(false, At(100, 100, TimeSpan.Zero), At(110, 100, Highlighter.FrameInterval)));
    }

    [Fact]
    public void The_stroke_is_built_at_the_overlay_scale()
    {
        _overlays.Scale = 2;
        HoldAt(new PixelPoint(100, 100));
        Overlay.Draws[^1].Stroke.ShouldBe(BrushStroke.Build([At(100, 100, TimeSpan.Zero)], HighlightStyle.Default, 2, false));
    }

    [Fact]
    public void A_still_pointer_draws_nothing_new()
    {
        HoldAt(new PixelPoint(100, 100));
        var draws = Overlay.Draws.Count;
        Advance(TimeSpan.FromMilliseconds(200));
        Overlay.Draws.Count.ShouldBe(draws);
    }

    [Fact]
    public void Releasing_redraws_once_with_the_lifted_end()
    {
        HoldAt(new PixelPoint(100, 100));
        _input.Cursor = new PixelPoint(160, 100);
        _clock.Advance(Highlighter.FrameInterval);
        var draws = Overlay.Draws.Count;

        Release();
        Advance(TimeSpan.FromMilliseconds(200));
        Overlay.Draws.Count.ShouldBe(draws + 1);
        Overlay.Draws[^1].Stroke.ShouldBe(Stroke(true, At(100, 100, TimeSpan.Zero), At(160, 100, Highlighter.FrameInterval)));
    }

    [Fact]
    public void Releasing_fades_the_trail_then_hides_it_and_stops_the_frames()
    {
        HoldAt(new PixelPoint(100, 100));
        Release();

        // The trail stays 300 ms as it is, then fades out in 500 ms.
        Advance(TimeSpan.FromMilliseconds(250));
        Overlay.Opacity.ShouldBe(HighlightStyle.Default.Opacity);
        Overlay.Visible.ShouldBeTrue();

        // 16 frames, then 19: 560 ms since the release.
        Advance(TimeSpan.FromMilliseconds(300));
        Overlay.Opacity.ShouldBe(HighlightTrail.FadeOpacity(TimeSpan.FromMilliseconds(560), HighlightStyle.Default.Opacity), 1e-9);
        Overlay.Visible.ShouldBeTrue();

        Advance(TimeSpan.FromMilliseconds(300));
        Overlay.Visible.ShouldBeFalse();
        var changes = Overlay.OpacityChanges;
        Advance(TimeSpan.FromSeconds(1));
        Overlay.OpacityChanges.ShouldBe(changes);
    }

    [Fact]
    public void The_fade_starts_from_the_style_opacity()
    {
        _settings.HighlightStyle = HighlightStyle.Default with { Opacity = 0.6 };
        HoldAt(new PixelPoint(100, 100));
        Overlay.Opacity.ShouldBe(0.6);

        Release();
        Advance(TimeSpan.FromMilliseconds(550));
        // 35 frames: 560 ms since the release.
        Overlay.Opacity.ShouldBe(HighlightTrail.FadeOpacity(TimeSpan.FromMilliseconds(560), 0.6), 1e-9);
    }

    [Fact]
    public void A_style_changed_while_held_restyles_without_moving()
    {
        HoldAt(new PixelPoint(100, 100));
        var draws = Overlay.Draws.Count;

        var thicker = HighlightStyle.Default with { Thickness = 20, Opacity = 0.8 };
        _settings.HighlightStyle = thicker;
        _clock.Advance(Highlighter.FrameInterval);

        Overlay.Draws.Count.ShouldBe(draws + 1);
        Overlay.Draws[^1].Stroke.ShouldBe(BrushStroke.Build([At(100, 100, TimeSpan.Zero)], thicker, 1, false));
        Overlay.Opacity.ShouldBe(0.8);
    }

    [Fact]
    public void An_out_of_range_style_is_clamped()
    {
        _settings.HighlightStyle = HighlightStyle.Default with { Opacity = 5 };
        HoldAt(new PixelPoint(100, 100));
        Overlay.Opacity.ShouldBe(1);
    }

    [Fact]
    public void Pressing_again_during_the_fade_starts_a_new_trail_at_full_opacity()
    {
        HoldAt(new PixelPoint(100, 100));
        Release();
        Advance(TimeSpan.FromMilliseconds(400));

        _input.Cursor = new PixelPoint(300, 300);
        _input.IsHeld = true;
        _input.Press();
        Overlay.Opacity.ShouldBe(HighlightStyle.Default.Opacity);
        Overlay.Draws[^1].Stroke.ShouldBe(Stroke(false, At(300, 300, TimeSpan.Zero)));

        // The fade is over: the new trail keeps following the pointer.
        _input.Cursor = new PixelPoint(320, 300);
        Advance(TimeSpan.FromMilliseconds(600));
        Overlay.Visible.ShouldBeTrue();
        Overlay.Draws[^1].Stroke.ShouldBe(Stroke(false, At(300, 300, TimeSpan.Zero), At(320, 300, Highlighter.FrameInterval)));
    }

    [Fact]
    public void A_new_press_during_the_fade_repaints_over_the_old_stroke()
    {
        HoldAt(new PixelPoint(100, 100));
        _input.Cursor = new PixelPoint(300, 100);
        _clock.Advance(Highlighter.FrameInterval);
        Release();
        Advance(TimeSpan.FromMilliseconds(100));
        var old = Overlay.Draws[^1].Stroke;

        _input.Cursor = new PixelPoint(500, 500);
        _input.IsHeld = true;
        _input.Press();
        var changed = Overlay.Draws[^1].Changed;
        old.ShouldAllBe(d => Inside(d, changed));
    }

    [Fact]
    public void After_hiding_a_new_stroke_repaints_only_itself()
    {
        HoldAt(new PixelPoint(100, 100));
        Release();
        Advance(TimeSpan.FromSeconds(1));
        Overlay.Visible.ShouldBeFalse();

        _input.Cursor = new PixelPoint(500, 500);
        _input.IsHeld = true;
        _input.Press();
        Overlay.Draws[^1].Changed.X.ShouldBeGreaterThanOrEqualTo(490);
    }

    [Fact]
    public void A_repeated_press_while_held_keeps_the_trail()
    {
        HoldAt(new PixelPoint(100, 100));
        _input.Cursor = new PixelPoint(120, 100);
        _clock.Advance(Highlighter.FrameInterval);
        _input.Press();
        Overlay.Draws[^1].Stroke.ShouldBe(Stroke(false, At(100, 100, TimeSpan.Zero), At(120, 100, Highlighter.FrameInterval)));
    }

    [Fact]
    public void Disabling_while_held_closes_the_overlay_and_stops_the_frames()
    {
        HoldAt(new PixelPoint(100, 100));
        var draws = Overlay.Draws.Count;

        _highlighter.Disable();
        Overlay.Disposed.ShouldBeTrue();
        _input.Unregistrations.ShouldBe(1);

        _input.Cursor = new PixelPoint(200, 200);
        Advance(TimeSpan.FromSeconds(1));
        Overlay.Draws.Count.ShouldBe(draws);
    }

    [Fact]
    public void Enabling_again_replaces_the_previous_overlay()
    {
        _highlighter.Enable(Area);
        var first = Overlay;
        _highlighter.Enable(new PixelRect(10, 10, 100, 100));
        first.Disposed.ShouldBeTrue();
        _overlays.Overlay.ShouldNotBeSameAs(first);
    }

    [Fact]
    public void Disposing_disables()
    {
        _highlighter.Enable(Area);
        _highlighter.Dispose();
        Overlay.Disposed.ShouldBeTrue();
    }
}
