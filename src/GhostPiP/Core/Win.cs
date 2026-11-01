using System.Text;
using static GhostPiP.Core.Native;

namespace GhostPiP.Core;

/// <summary>Everything we ask Windows about windows that aren't ours.</summary>
public static class Win
{
    public static string Title(nint hwnd)
    {
        int n = GetWindowTextLengthW(hwnd);
        if (n <= 0)
            return "";
        var sb = new StringBuilder(n + 1);
        GetWindowTextW(hwnd, sb, n + 1);
        return sb.ToString();
    }

    public static uint Pid(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static string ProcessPath(uint pid)
    {
        nint h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0)
            return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : "";
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static (int W, int H) ClientSize(nint hwnd) =>
        hwnd != 0 && GetClientRect(hwnd, out var r) ? (r.Right, r.Bottom) : (0, 0);

    public static RECT WorkArea(nint hwnd) => MonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)).rcWork;

    public static RECT WorkAreaAt(POINT p) => MonitorInfo(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST)).rcWork;

    public static RECT WorkAreaOf(RECT r) => MonitorInfo(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST)).rcWork;

    public static RECT PrimaryWorkArea() => MonitorInfo(MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY)).rcWork;

    static MONITORINFO MonitorInfo(nint monitor)
    {
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref mi);
        return mi;
    }

    /// <summary>Cursor position, or null while it's hidden (e.g. captured by a game).</summary>
    public static POINT? VisibleCursor()
    {
        var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0)
            return null;
        return ci.ptScreenPos;
    }

    public static POINT Cursor()
    {
        GetCursorPos(out var p);
        return p;
    }

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static void AddExStyle(nint hwnd, uint add = 0, uint remove = 0)
    {
        long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (nint)((ex | add) & ~(long)remove));
    }

    public static void KeepOnTop(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static void SetCorners(nint hwnd, int preference) =>
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, 4);

    public static void SetBorderColor(nint hwnd, uint colorref) =>
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, 4);

    public static void SetExcludedFromCapture(nint hwnd, bool excluded) =>
        SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    public static void Tap(int vk)
    {
        keybd_event((byte)vk, 0, 0, 0);
        keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, 0);
    }

    /// <summary>Hotkeys arrive with Ctrl+Alt still held; let go so they don't combine with keys we send.</summary>
    public static void ReleaseModifiers()
    {
        foreach (int vk in new[] { VK_CONTROL, VK_MENU, VK_SHIFT })
            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, 0);
    }

    /// <summary>Briefly focus hwnd, tap a key, then hand focus back.</summary>
    public static void SendKeyToWindow(nint hwnd, int vk)
    {
        nint prev = GetForegroundWindow();
        ReleaseModifiers();
        SetForegroundWindow(hwnd);
        Tap(vk);
        if (prev != 0 && prev != hwnd)
            SetForegroundWindow(prev);
    }

    public static void Focus(nint hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
        ReleaseModifiers();
        SetForegroundWindow(hwnd);
    }

    /// <summary>Un-minimize without taking focus, parked behind everything. Windows stops drawing minimized windows.</summary>
    public static void RestoreBehind(nint hwnd)
    {
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
