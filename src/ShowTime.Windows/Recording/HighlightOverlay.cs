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

    /// <summary>The stroke width at 100 %, scaled by the monitor's DPI.</summary>
    internal const float Thickness = 14.4f;

    private readonly Rectangle _bounds;
    private readonly IntPtr _surfaceDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _previousBitmap;

    /// <summary>The DIB's pixels, premultiplied BGRA, as GDI+ draws on them.</summary>
    private readonly Bitmap _surface;

    private Point[] _points = [];
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

    private float StrokeWidth => Thickness * DeviceDpi / 96f;

    private Rectangle Surface => new(0, 0, _surface.Width, _surface.Height);

    /// <summary>The surface's color at a point of the window, with straight alpha.</summary>
    internal Color SurfacePixel(int x, int y) => _surface.GetPixel(x, y);

    public void Draw(IReadOnlyList<PixelPoint> points)
    {
        var previous = _points;
        _points = [.. points.Select(p => new Point(p.X - _bounds.X, p.Y - _bounds.Y))];
        // While the trail grows, only its new end needs repainting; a new trail repaints everything.
        var grown = previous.Length > 0 && _points.Length > previous.Length && _points[0] == previous[0];
        var changed = Rectangle.Intersect(grown ? Around(_points[(previous.Length - 1)..]) : Surface, Surface);
        Repaint(changed);
        if (!Visible)
        {
            Show();
            Bounds = _bounds;
            Push(Surface);
        }
        else
        {
            Push(changed);
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
        _points = [];
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

    /// <summary>Clears the rectangle of the surface, then draws in it what of the trail falls there.</summary>
    private void Repaint(Rectangle area)
    {
        if (area.IsEmpty)
            return;
        using var graphics = Graphics.FromImage(_surface);
        graphics.SetClip(area);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
            graphics.FillRectangle(clear, area);
        if (_points.Length == 0)
            return;

        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var width = StrokeWidth;
        if (_points.Length == 1)
        {
            using var brush = new SolidBrush(Ink);
            graphics.FillEllipse(brush, _points[0].X - width / 2, _points[0].Y - width / 2, width, width);
            return;
        }
        using var pen = new Pen(Ink, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLines(pen, _points);
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
        Native.UpdateLayeredWindowIndirect(Handle, &info);
    }

    private Native.BLENDFUNCTION Blend() => new()
    {
        BlendOp = Native.AC_SRC_OVER,
        SourceConstantAlpha = (byte)Math.Round(_opacity * 255),
        AlphaFormat = Native.AC_SRC_ALPHA
    };

    private Rectangle Around(Point[] points)
    {
        int left = points.Min(p => p.X), top = points.Min(p => p.Y), right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        var margin = (int)Math.Ceiling(StrokeWidth);
        return Rectangle.FromLTRB(left - margin, top - margin, right + margin, bottom + margin);
    }
}

internal sealed class HighlightOverlayFactory : IHighlightOverlayFactory
{
    public IHighlightOverlay Create(PixelRect area) => new HighlightOverlay(area.ToRectangle());
}
