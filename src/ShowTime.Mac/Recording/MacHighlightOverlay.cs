using AppKit;
using CoreGraphics;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;
using ShowTime.Core.Settings;
using ShowTime.Mac.Platform;

namespace ShowTime.Mac.Recording;

/// <summary>
/// The highlight trail over the recorded area: a transparent window that clicks go through, above every other one, and
/// on purpose not excluded from capture, unlike the red frame: ffmpeg records it with the screen. The stroke is painted
/// opaque; the window's opacity makes it translucent, so that crossings do not darken, and fades it out without
/// redrawing.
/// </summary>
internal sealed class MacHighlightOverlay : IHighlightOverlay
{
    private readonly NSWindow _window;
    private readonly TrailView _view;

    public MacHighlightOverlay(PixelRect area, INativeWindows windows)
    {
        var screen = ScreenInfo.Holding(area);
        var frame = screen.ToCocoa(area);
        Scale = screen.Frame.Scale;
        _view = new TrailView(area, Scale) { Frame = new CGRect(CGPoint.Empty, frame.Size) };
        _window = new NSWindow(frame, NSWindowStyle.Borderless, NSBackingStore.Buffered, false)
        {
            IsOpaque = false,
            BackgroundColor = NSColor.Clear,
            HasShadow = false,
            IgnoresMouseEvents = true,
            AlphaValue = (nfloat)HighlightStyle.Default.Opacity,
            ContentView = _view
        };
        _window.ReleaseWhenClosed(false);
        windows.RaiseAboveEverything(_window.Handle);
    }

    public double Scale { get; }

    public void Draw(IReadOnlyList<StrokeDisc> stroke, PixelRect changed)
    {
        _view.Stroke = stroke;
        _view.SetNeedsDisplayInRect(_view.ToView(changed));
        if (_window.IsVisible)
        {
            // Now rather than at the end of the run loop's turn: an opacity set next applies to this stroke, not the last.
            _view.DisplayIfNeeded();
            return;
        }
        // Drawn before it shows: the video captures it, and its last stroke must not flash for a frame.
        _view.Display();
        _window.OrderFrontRegardless();
    }

    public void SetOpacity(double opacity) => _window.AlphaValue = (nfloat)opacity;

    public void Hide()
    {
        _view.Stroke = [];
        _view.NeedsDisplay = true;
        _window.OrderOut(null);
    }

    public void Dispose()
    {
        _window.OrderOut(null);
        _window.Close();
        _window.Dispose();
        _view.Dispose();
    }

    /// <summary>The stroke, drawn in the window's points from ShowTime's pixels.</summary>
    private sealed class TrailView(PixelRect area, double scale) : NSView
    {
        private static readonly NSColor Ink = NSColor.FromSrgb(1f, 212f / 255, 0f, 1f);

        public IReadOnlyList<StrokeDisc> Stroke { get; set; } = [];

        /// <summary>A rectangle of screen pixels in the view's points; Cocoa's origin is at the bottom left.</summary>
        public CGRect ToView(PixelRect r) => new((r.X - area.X) / scale, (area.Bottom - r.Bottom) / scale, r.Width / scale, r.Height / scale);

        public override void DrawRect(CGRect dirtyRect)
        {
            NSColor.Clear.Set();
            NSGraphics.RectFill(dirtyRect, NSCompositingOperation.Copy);
            var stroke = Stroke;
            if (stroke.Count == 0)
                return;

            // One fill per piece: a single path of them all would have its orientations cancel out where they overlap.
            Ink.SetFill();
            foreach (var i in BrushStroke.PiecesTouching(stroke, ToPixels(dirtyRect)))
            {
                var disc = stroke[i];
                var centre = At(disc.X, disc.Y);
                var radius = disc.Radius / scale;
                using (var oval = NSBezierPath.FromOvalInRect(new CGRect(centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius)))
                    oval.Fill();
                if (i + 1 < stroke.Count && BrushStroke.Hull(disc, stroke[i + 1]) is { } hull)
                {
                    using var path = new NSBezierPath();
                    path.MoveTo(At(hull[0].X, hull[0].Y));
                    for (var j = 1; j < hull.Length; j++)
                        path.LineTo(At(hull[j].X, hull[j].Y));
                    path.ClosePath();
                    path.Fill();
                }
            }
        }

        private CGPoint At(double x, double y) => new((x - area.X) / scale, (area.Bottom - y) / scale);

        /// <summary>The screen pixels a rectangle of the view covers, rounded outwards.</summary>
        private PixelRect ToPixels(CGRect r)
        {
            var left = (int)Math.Floor(area.X + r.X * scale);
            var top = (int)Math.Floor(area.Bottom - (r.Y + r.Height) * scale);
            return new PixelRect(left, top, (int)Math.Ceiling(area.X + (r.X + r.Width) * scale) - left,
                (int)Math.Ceiling(area.Bottom - r.Y * scale) - top);
        }
    }
}

internal sealed class MacHighlightOverlayFactory(INativeWindows windows) : IHighlightOverlayFactory
{
    public IHighlightOverlay Create(PixelRect area) => new MacHighlightOverlay(area, windows);
}
