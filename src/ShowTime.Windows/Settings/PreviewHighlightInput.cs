using System;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using Forms = System.Windows.Forms;

namespace ShowTime;

/// <summary>
/// The trigger of the stroke preview: the left button pressed in its zone, held as long as it stays down. Whatever
/// trigger the highlighter registers, the preview has none of its own to watch.
/// </summary>
internal sealed class PreviewHighlightInput : IHighlightInput
{
    private Action? _pressed;

    public bool IsHeld => (Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0;

    /// <summary>In physical pixels, the process being Per-Monitor-V2 aware.</summary>
    public PixelPoint Cursor => new(Forms.Cursor.Position.X, Forms.Cursor.Position.Y);

    public bool Register(HighlightTrigger trigger, Action pressed)
    {
        _pressed = pressed;
        return true;
    }

    public void Unregister() => _pressed = null;

    /// <summary>Runs what <see cref="Register"/> was given, if anything.</summary>
    public void Press() => _pressed?.Invoke();
}
