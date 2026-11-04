using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using ShowTime.Adapters;
using ShowTime.Core.Geometry;
using ShowTime.Core.Ports;
using ShowTime.Core.Recording;

namespace ShowTime;

/// <summary>
/// The highlight trail, drawn over the recorded area. Click-through and never activated, as <see cref="RecordingFrame"/>,
/// but on purpose not excluded from capture: gdigrab copies layered windows, so the video shows the trail. The trail is
/// painted opaque and antialiased on a surface with an alpha channel, pushed with UpdateLayeredWindow; the window's
/// constant alpha makes it translucent, so that crossings do not darken, and fades it out without repainting.
/// </summary>
internal sealed unsafe class HighlightOverlay : Form, IHighlightOverlay
{
    internal static readonly Color Ink = Color.FromArgb(255, 212, 0);

    private readonly Rectangle _bounds;
    private readonly IntPtr _surfaceDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _previousBitmap;

    /// <summary>The DIB's pixels, premultiplied BGRA, as GDI+ draws on them.</summary>
    private readonly Bitmap _surface;

    private IReadOnlyList<StrokeDisc> _stroke = [];
    private double _opacity = 1;
    private bool _released;

    public HighlightOverlay(Rectangle area)
    {
        _bounds = area;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _bounds;
        ShowInTaskbar = false;
        TopMost = true;

        int width = Math.Max(1, area.Width), height = Math.Max(1, area.Height);
        _surfaceDc = Native.CreateCompatibleDC(IntPtr.Zero);
        // Top-down (negative height), so that its rows are in the order GDI+ expects.
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32
        };
        _dib = Native.CreateDIBSection(_surfaceDc, &header, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        _previousBitmap = Native.SelectObject(_surfaceDc, _dib);
        _surface = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        // Created now, on the area's monitor, so that its DPI is known before the first stroke is built.
        CreateHandle();
        Scale = DeviceDpi / 96.0;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    /// <summary>What <see cref="SetOpacity"/> last set; Form.Opacity is not used, it would replace the surface.</summary>
    internal double TrailOpacity => _opacity;

    public new double Scale { get; }

    private Rectangle Surface => new(0, 0, _surface.Width, _surface.Height);

    /// <summary>The surface's color at a point of the window, with straight alpha.</summary>
    internal Color SurfacePixel(int x, int y) => _surface.GetPixel(x, y);

    public void Draw(IReadOnlyList<StrokeDisc> stroke, PixelRect changed)
    {
        _stroke = stroke;
        var area = Rectangle.Intersect(new Rectangle(changed.X - _bounds.X, changed.Y - _bounds.Y, changed.Width, changed.Height), Surface);
        Repaint(area);
        if (!Visible)
        {
            Show();
            Bounds = _bounds;
            Push(Surface);
        }
        else
        {
            Push(area);
        }
    }

    public void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, 0, 1);
        if (!Visible)
            return;
        // Only the constant alpha changes: nothing is repainted or copied again.
        var blend = Blend();
        if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, null, null, IntPtr.Zero, null, 0, &blend, Native.ULW_ALPHA))
            Push(Surface);
    }

    void IHighlightOverlay.Hide()
    {
        _stroke = [];
        Repaint(Surface);
        Hide();
    }

    protected override void WndProc(ref Message m)
    {
        // Same reason as SelectionOverlay: the overlay is positioned in physical pixels and must not be rescaled.
        if (m.Msg == Native.WM_DPICHANGED)
            return;
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_released)
        {
            _released = true;
            if (disposing)
                _surface.Dispose();
            Native.SelectObject(_surfaceDc, _previousBitmap);
            Native.DeleteObject(_dib);
            Native.DeleteDC(_surfaceDc);
        }
        base.Dispose(disposing);
    }

    /// <summary>Clears the rectangle of the surface, then paints in it the pieces of the stroke that touch it.</summary>
    private void Repaint(Rectangle area)
    {
        if (area.IsEmpty)
            return;
        using var graphics = Graphics.FromImage(_surface);
        graphics.SetClip(area);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
            graphics.FillRectangle(clear, area);
        if (_stroke.Count == 0)
            return;

        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var ink = new SolidBrush(Ink);
        float x0 = _bounds.X, y0 = _bounds.Y;
        foreach (var i in BrushStroke.PiecesTouching(_stroke, new PixelRect(area.X + _bounds.X, area.Y + _bounds.Y, area.Width, area.Height)))
        {
            var disc = _stroke[i];
            var radius = (float)disc.Radius;
            graphics.FillEllipse(ink, (float)disc.X - x0 - radius, (float)disc.Y - y0 - radius, 2 * radius, 2 * radius);
            if (i + 1 < _stroke.Count && BrushStroke.Hull(disc, _stroke[i + 1]) is { } hull)
                graphics.FillPolygon(ink, [.. hull.Select(p => new PointF((float)p.X - x0, (float)p.Y - y0))]);
        }
    }

    /// <summary>Copies the rectangle of the surface to the screen.</summary>
    private void Push(Rectangle changed)
    {
        if (changed.IsEmpty)
            return;
        var position = new Native.POINT { X = _bounds.X, Y = _bounds.Y };
        var size = new Native.SIZE { Cx = _surface.Width, Cy = _surface.Height };
        var origin = new Native.POINT();
        var blend = Blend();
        var dirty = new Native.RECT { Left = changed.Left, Top = changed.Top, Right = changed.Right, Bottom = changed.Bottom };
        var info = new Native.UPDATELAYEREDWINDOWINFO
        {
            cbSize = (uint)sizeof(Native.UPDATELAYEREDWINDOWINFO),
            pptDst = &position,
            psize = &size,
            hdcSrc = _surfaceDc,
            pptSrc = &origin,
            pblend = &blend,
            dwFlags = Native.ULW_ALPHA,
            prcDirty = &dirty
        };
        if (!Native.UpdateLayeredWindowIndirect(Handle, &info))
        {
            // A rectangle the system refuses: the whole surface goes instead, rather than a trail that stops growing.
            info.prcDirty = null;
            Native.UpdateLayeredWindowIndirect(Handle, &info);
        }
    }

    private Native.BLENDFUNCTION Blend() => new()
    {
        BlendOp = Native.AC_SRC_OVER,
        SourceConstantAlpha = (byte)Math.Round(_opacity * 255),
        AlphaFormat = Native.AC_SRC_ALPHA
    };

}

internal sealed class HighlightOverlayFactory : IHighlightOverlayFactory
{
    public IHighlightOverlay Create(PixelRect area) => new HighlightOverlay(area.ToRectangle());
}
