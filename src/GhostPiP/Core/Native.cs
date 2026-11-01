using System.Runtime.InteropServices;
using System.Text;

namespace GhostPiP.Core;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly override string ToString() => $"({Left},{Top} {Width}x{Height})";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;

    public POINT(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>Every Win32, DWM and shell call the app makes, in one place.</summary>
internal static class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_APPWINDOW = 0x40000, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint LWA_ALPHA = 0x2;
    public const uint GW_OWNER = 4, GA_ROOT = 2;
    public static readonly nint HWND_TOPMOST = -1, HWND_NOTOPMOST = -2, HWND_BOTTOM = 1, HWND_TOP = 0;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_FRAMECHANGED = 0x20;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_RESTORE = 9, SW_SHOWNA = 8;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

    public const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35,
        DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWCP_DEFAULT = 0, DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
    public const int DWMSBT_NONE = 1, DWMSBT_MAINWINDOW = 2, DWMSBT_TRANSIENTWINDOW = 3, DWMSBT_TABBEDWINDOW = 4;
    public const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF, DWMWA_COLOR_NONE = 0xFFFFFFFE;
    public const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4,
        DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    public const uint PW_CLIENTONLY = 0x1, PW_RENDERFULLCONTENT = 0x2;
    public const uint WM_GETICON = 0x7F;
    public const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2, GCLP_HICON = -14, GCLP_HICONSM = -34;
    public const uint SMTO_ABORTIFHUNG = 0x2;
    public const uint CURSOR_SHOWING = 0x1;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint KEYEVENTF_KEYUP = 0x2;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
        VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_ADD = 0x6B, VK_SUBTRACT = 0x6D, VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD,
        VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF,
        VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    public const uint WM_DESTROY = 0x2, WM_SETCURSOR = 0x20, WM_MOUSEACTIVATE = 0x21, WM_HOTKEY = 0x312,
        WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONUP = 0x205, WM_MOUSEWHEEL = 0x20A, WM_CAPTURECHANGED = 0x215, WM_ERASEBKGND = 0x14,
        WM_APP = 0x8000, WM_CONTEXTMENU = 0x7B, WM_DISPLAYCHANGE = 0x7E;
    public const int MA_NOACTIVATE = 3;
    public const int IDC_ARROW = 32512, IDC_SIZENWSE = 32642, IDC_SIZENESW = 32643, IDC_SIZEALL = 32646, IDC_HAND = 32649;
    public const uint CS_DBLCLKS = 0x8;
    public const int BLACK_BRUSH = 4;

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public uint flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;

    // user32 ---------------------------------------------------------------------------------------
    [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, StringBuilder text, int max);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32")] public static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32")] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32")] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32")] public static extern nint GetClassLongPtrW(nint hwnd, int index);
    [DllImport("user32")] public static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32")] public static extern nint GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] public static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32")] public static extern nint DeferWindowPos(nint info, nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] public static extern bool EndDeferWindowPos(nint info);
    [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern nint SetCapture(nint hwnd);
    [DllImport("user32")] public static extern bool ReleaseCapture();
    [DllImport("user32")] public static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32")] public static extern nint SetCursor(nint cursor);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32")] public static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] public static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32")] public static extern nint MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32")] public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32", SetLastError = true)] public static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32")] public static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32")] public static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32")] public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("user32")] public static extern nint GetDC(nint hwnd);
    [DllImport("user32")] public static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32")] public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32")] public static extern bool DestroyIcon(nint hicon);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint LoadImageW(nint instance, nint name, uint type, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern uint RegisterWindowMessageW([MarshalAs(UnmanagedType.LPWStr)] string name);

    // dwmapi ---------------------------------------------------------------------------------------
    [DllImport("dwmapi")] public static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi")] public static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi")] public static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref uint value, int size);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi")] public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    // gdi32 ----------------------------------------------------------------------------------------
    [DllImport("gdi32")] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32")] public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32")] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] public static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32")] public static extern bool GdiFlush();
    [DllImport("gdi32")] public static extern nint GetStockObject(int index);

    // kernel32 / shell32 ---------------------------------------------------------------------------
    [DllImport("kernel32")] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] public static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32", CharSet = CharSet.Unicode)] public static extern uint ExtractIconExW(string file, int index, nint[]? large, nint[]? small, uint count);
}
