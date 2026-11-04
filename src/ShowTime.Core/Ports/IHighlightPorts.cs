using ShowTime.Core.Geometry;
using ShowTime.Core.Recording;

namespace ShowTime.Core.Ports;

/// <summary>The highlight trigger and the mouse, watched while a recording is under way.</summary>
public interface IHighlightInput
{
    /// <summary>Starts watching the trigger. <paramref name="pressed"/> runs on the UI thread.</summary>
    /// <returns>False when the system refused it, usually because another application holds it.</returns>
    bool Register(HighlightTrigger trigger, Action pressed);

    void Unregister();

    /// <summary>Whether the registered trigger is still held down, modifiers included.</summary>
    bool IsHeld { get; }

    /// <summary>The mouse position, in physical screen pixels.</summary>
    PixelPoint Cursor { get; }
}

/// <summary>
/// A click-through window over the recorded area that draws the trail. Unlike the recording frame, it is not excluded
/// from capture: the video shows it.
/// </summary>
public interface IHighlightOverlay : IDisposable
{
    /// <summary>The scale of the screen holding the area: 1 at 100 %.</summary>
    double Scale { get; }

    /// <summary>
    /// Shows the overlay if it is hidden and repaints <paramref name="changed"/>, in screen pixels, from the stroke: its
    /// discs and the hull between each disc and the next (<see cref="BrushStroke.Hull"/>), opaque.
    /// </summary>
    void Draw(IReadOnlyList<StrokeDisc> stroke, PixelRect changed);

    void SetOpacity(double opacity);

    /// <summary>Hides the overlay and clears what it drew.</summary>
    void Hide();
}

public interface IHighlightOverlayFactory
{
    /// <summary>A hidden overlay over the recorded area, captured with it.</summary>
    /// <exception cref="InvalidOperationException">The system cannot give it a surface: the highlight stays off.</exception>
    IHighlightOverlay Create(PixelRect area);
}
