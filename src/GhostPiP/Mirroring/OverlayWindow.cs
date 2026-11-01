using GhostPiP.Core;
using static GhostPiP.Core.Native;

namespace GhostPiP.Mirroring;

/// <summary>A screen rectangle in real pixels.</summary>
public readonly record struct PxRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public RECT ToRECT() => new(X, Y, X + W, Y + H);
    public bool Contains(POINT p, int pad = 0) => p.X >= X - pad && p.X < Right + pad && p.Y >= Y - pad && p.Y < Bottom + pad;

    public static PxRect Lerp(PxRect a, PxRect b, double t) => new(
        (int)Math.Round(a.X + (b.X - a.X) * t),
        (int)Math.Round(a.Y + (b.Y - a.Y) * t),
        Math.Max(1, (int)Math.Round(a.W + (b.W - a.W) * t)),
        Math.Max(1, (int)Math.Round(a.H + (b.H - a.H) * t)));
}

/// <summary>What the overlay reports while it's unlocked and taking the mouse.</summary>
public interface IOverlayInput
{
    nint OverlayCursor(POINT screen);
    void OverlayPress(POINT screen);
    void OverlayDrag(POINT screen);
    void OverlayRelease();
    void OverlayDoubleClick();
    void OverlayWheel(int delta);
    void OverlayContextMenu(POINT screen);
}

/// <summary>
/// The mirror itself: a bare Win32 layered window holding one DWM thumbnail. It isn't a WPF window because
/// it draws nothing of its own, so there's nothing for WPF to do, and a raw window is free to composite.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    const string ClassName = "GhostPiP.Overlay";

    // Keeps the delegate alive for as long as Windows can call it.
    static readonly WndProc s_proc = Dispatch;
    static readonly Dictionary<nint, OverlayWindow> s_windows = new();
    static bool s_registered;

    readonly IOverlayInput _input;
    bool _captured;
    byte _alpha;

    public nint Handle { get; private set; }
    public bool Visible { get; private set; }
    public double DpiScale => Math.Max(1, GetDpiForWindow(Handle)) / 96.0;

    public OverlayWindow(IOverlayInput input)
    {
        _input = input;
        if (!s_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = s_proc,
                hInstance = GetModuleHandleW(null),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = ClassName,
            };
            RegisterClassExW(ref wc);
            s_registered = true;
        }
        Handle = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "Ghost PiP Mirror", WS_POPUP, -32000, -32000, 16, 16, 0, 0, GetModuleHandleW(null), 0);
        s_windows[Handle] = this;
        SetLayeredWindowAttributes(Handle, 0, 0, LWA_ALPHA);
    }

    public void SetAlpha(double alpha)
    {
        byte a = (byte)Math.Clamp(Math.Round(alpha), 0, 255);
        if (a == _alpha)
            return;
        _alpha = a;
        SetLayeredWindowAttributes(Handle, 0, a, LWA_ALPHA);
    }

    public void Show()
    {
        if (Visible)
            return;
        Visible = true;
        ShowWindow(Handle, SW_SHOWNA);
        Win.KeepOnTop(Handle);
    }

    public void Hide()
    {
        if (!Visible)
            return;
        Visible = false;
        ShowWindow(Handle, SW_HIDE);
    }

    public void SetClickThrough(bool through)
    {
        if (through)
            Win.AddExStyle(Handle, add: WS_EX_TRANSPARENT);
        else
            Win.AddExStyle(Handle, remove: WS_EX_TRANSPARENT);
    }

    static nint Dispatch(nint hwnd, uint msg, nint wParam, nint lParam) =>
        s_windows.TryGetValue(hwnd, out var self) ? self.Proc(hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);

    nint Proc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_SETCURSOR:
                SetCursor(_input.OverlayCursor(Win.Cursor()));
                return 1;
            case WM_LBUTTONDOWN:
                SetCapture(hwnd);
                _captured = true;
                _input.OverlayPress(Win.Cursor());
                return 0;
            case WM_MOUSEMOVE:
                if (_captured)
                    _input.OverlayDrag(Win.Cursor());
                return 0;
            case WM_LBUTTONUP:
                if (_captured)
                {
                    _captured = false;
                    ReleaseCapture();
                    _input.OverlayRelease();
                }
                return 0;
            case WM_CAPTURECHANGED:
                if (_captured)
                {
                    _captured = false;
                    _input.OverlayRelease();
                }
                return 0;
            case WM_LBUTTONDBLCLK:
                _input.OverlayDoubleClick();
                return 0;
            case WM_RBUTTONUP:
                _input.OverlayContextMenu(Win.Cursor());
                return 0;
            case WM_MOUSEWHEEL:
                _input.OverlayWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == 0)
            return;
        s_windows.Remove(Handle);
        DestroyWindow(Handle);
        Handle = 0;
    }
}
