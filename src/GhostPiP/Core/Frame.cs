using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static GhostPiP.Core.Native;

namespace GhostPiP.Core;

/// <summary>A captured picture: 32-bit BGRA, top-down, no padding.</summary>
public sealed class Frame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Frame(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public BitmapSource ToBitmap()
    {
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Pixels, Width * 4);
        bmp.Freeze();
        return bmp;
    }
}

public static class WindowCapture
{
    /// <summary>Snapshot of a window's client area, even when it's covered. Null if it's minimized or refuses.</summary>
    public static Frame? Snapshot(nint hwnd)
    {
        var (w, h) = Win.ClientSize(hwnd);
        if (w < 2 || h < 2 || IsIconic(hwnd))
            return null;

        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        nint bmp = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
        if (bmp == 0)
        {
            DeleteDC(dc);
            ReleaseDC(0, screen);
            return null;
        }
        nint old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT))
                return null;
            GdiFlush();
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new Frame(w, h, pixels);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }
}
