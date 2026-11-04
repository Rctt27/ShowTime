using System;
using System.Runtime.InteropServices;

namespace ShowTime;

/// <summary>
/// The Win32 calls that belong to the app's own windows (overlay, frame, bar); the system ports are in
/// ShowTime.Platform.Windows.
/// </summary>
internal static class Native
{
    public const int WM_DPICHANGED = 0x02E0;

    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOACTIVATE = 0x8000000;

    public const uint SWP_NOSIZE = 0x1;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;

    public const uint DIB_RGB_COLORS = 0;
    public const byte AC_SRC_OVER = 0;
    public const byte AC_SRC_ALPHA = 1;
    public const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int Cx, Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct UPDATELAYEREDWINDOWINFO
    {
        public uint cbSize;
        public IntPtr hdcDst;
        public POINT* pptDst;
        public SIZE* psize;
        public IntPtr hdcSrc;
        public POINT* pptSrc;
        public uint crKey;
        public BLENDFUNCTION* pblend;
        public uint dwFlags;
        public RECT* prcDirty;
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern unsafe IntPtr CreateDIBSection(IntPtr hdc, BITMAPINFOHEADER* header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    public static extern unsafe bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, POINT* pptDst, SIZE* psize, IntPtr hdcSrc,
        POINT* pptSrc, uint crKey, BLENDFUNCTION* pblend, uint flags);

    [DllImport("user32.dll")]
    public static extern unsafe bool UpdateLayeredWindowIndirect(IntPtr hWnd, UPDATELAYEREDWINDOWINFO* info);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
