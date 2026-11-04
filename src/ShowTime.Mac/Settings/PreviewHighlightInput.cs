using AppKit;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Mac.Platform;
using ShowTime.Platform.MacOS;

namespace ShowTime.Mac.Settings;

/// <summary>
/// The trigger of the stroke preview: the mouse button pressed in its zone, held as long as it stays down. Whatever
/// trigger the highlighter registers, the preview has none of its own to watch.
/// </summary>
internal sealed class PreviewHighlightInput : IHighlightInput
{
    private Action? _pressed;
    private IReadOnlyList<ScreenFrame> _frames = [];
    private IReadOnlyList<PixelRect> _layout = [];

    public bool IsHeld => (NSEvent.CurrentPressedMouseButtons & 1) != 0;

    public PixelPoint Cursor
    {
        get
        {
            if (_frames.Count == 0)
                return default;
            var point = NSEvent.CurrentMouseLocation;
            return MacScreenSpace.PointToPixel(_frames, _layout, point.X, point.Y);
        }
    }

    public bool Register(HighlightTrigger trigger, Action pressed)
    {
        _pressed = pressed;
        // The screens of this stroke, read once rather than on every frame, as MacHighlightInput does.
        var screens = ScreenInfo.Current();
        _frames = [.. screens.Select(s => s.Frame)];
        _layout = [.. screens.Select(s => s.Pixels)];
        return true;
    }

    public void Unregister() => _pressed = null;

    /// <summary>Runs what <see cref="Register"/> was given, if anything.</summary>
    public void Press() => _pressed?.Invoke();
}
