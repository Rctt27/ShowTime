using ShowTime.Core.Geometry;
using ShowTime.Core.Hotkeys;
using ShowTime.Core.Ports;
using ShowTime.Core.Settings;

namespace ShowTime.Core.Recording;

/// <summary>
/// While a recording is under way, holding the trigger draws a brush stroke behind the mouse, which the video captures;
/// releasing it fades the stroke out. The style is read from the settings on every frame, so that the settings window's
/// preview, which runs a highlighter of its own, follows its sliders at once. Every member is called on the UI thread.
/// </summary>
public sealed class Highlighter(
    AppSettings settings, IHighlightInput input, IHighlightOverlayFactory overlays, IUiThread uiThread,
    TimeProvider clock, HotkeyNaming hotkeyNaming) : IDisposable
{
    /// <summary>About 60 frames per second, above any recording frame rate.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly HighlightTrail _trail = new();
    private IHighlightOverlay? _overlay;
    private ITimer? _frames;
    private bool _registered;
    private bool _held;
    private long _pressedAt;
    private long _releasedAt;

    /// <summary>The stroke on the overlay, from which the next one's change is measured.</summary>
    private IReadOnlyList<StrokeDisc> _drawn = [];

    /// <summary>What <see cref="_drawn"/> was built with: nothing to rebuild while both stay the same.</summary>
    private HighlightStyle? _drawnStyle;
    private bool _drawnLifted;

    private double _opacity;

    /// <summary>Registers the trigger for a recording of <paramref name="area"/>.</summary>
    /// <returns>The trigger as the bar shows it, or null when it is empty, invalid or refused.</returns>
    public string? Enable(PixelRect area)
    {
        Disable();
        if (!HighlightTrigger.TryParse(settings.HighlightTrigger, out var trigger) || !input.Register(trigger, OnPressed))
            return null;
        _registered = true;
        try
        {
            _overlay = overlays.Create(area);
        }
        catch (InvalidOperationException)
        {
            // No surface for the trail, an area too large for the memory left: the recording goes on without it.
            Disable();
            return null;
        }
        return trigger.Format(hotkeyNaming);
    }

    public void Disable()
    {
        if (_registered)
            input.Unregister();
        _registered = false;
        StopFrames();
        _overlay?.Dispose();
        _overlay = null;
        _held = false;
        _drawn = [];
        _drawnStyle = null;
    }

    public void Dispose() => Disable();

    private void OnPressed()
    {
        // Carbon may repeat the press while the key is held.
        if (_overlay is not { } overlay || _held)
            return;
        _held = true;
        _pressedAt = clock.GetTimestamp();
        var style = settings.HighlightStyle.Clamped();
        _trail.Begin(input.Cursor, TimeSpan.Zero);
        // A stroke still fading is cleared before the opacity rises: raised first, it would flash at full opacity.
        Redraw(overlay, style);
        _opacity = style.Opacity;
        overlay.SetOpacity(_opacity);
        _frames ??= clock.CreateTimer(_ => uiThread.Post(OnFrame), null, FrameInterval, FrameInterval);
    }

    private void OnFrame()
    {
        if (_overlay is not { } overlay)
            return;
        var style = settings.HighlightStyle.Clamped();
        if (_held && !input.IsHeld)
        {
            _held = false;
            _releasedAt = clock.GetTimestamp();
        }
        if (_held)
        {
            var moved = _trail.Add(input.Cursor, clock.GetElapsedTime(_pressedAt));
            SetOpacity(overlay, style.Opacity);
            // A long stroke costs a few milliseconds to build: not on every frame while the mouse stays still.
            if (moved || style != _drawnStyle)
                Redraw(overlay, style);
            return;
        }

        var opacity = HighlightTrail.FadeOpacity(clock.GetElapsedTime(_releasedAt), style.Opacity);
        if (opacity > 0)
        {
            // Once released, the end tapers: redrawn on the first frame of the fade, then only for a new style.
            if (!_drawnLifted || style != _drawnStyle)
                Redraw(overlay, style);
            SetOpacity(overlay, opacity);
            return;
        }
        overlay.Hide();
        _drawn = [];
        _drawnStyle = null;
        StopFrames();
    }

    /// <summary>Repaints what changed since the last stroke drawn, if anything did.</summary>
    private void Redraw(IHighlightOverlay overlay, HighlightStyle style)
    {
        var stroke = BrushStroke.Build(_trail.Samples, style, overlay.Scale, lifted: !_held);
        if (BrushStroke.Changed(_drawn, stroke) is { } changed)
            overlay.Draw(stroke, changed);
        _drawn = stroke;
        _drawnStyle = style;
        _drawnLifted = !_held;
    }

    private void SetOpacity(IHighlightOverlay overlay, double opacity)
    {
        if (opacity == _opacity)
            return;
        _opacity = opacity;
        overlay.SetOpacity(opacity);
    }

    private void StopFrames()
    {
        _frames?.Dispose();
        _frames = null;
    }
}
