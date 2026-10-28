"""Ghost PiP - click-through picture-in-picture for any window (Windows 10/11).

Mirrors a live window into an always-on-top overlay using the DWM thumbnail
API. The mouse passes straight through it, and transparency, cropping and
playback are controlled from a Pixel-style toolbar, a settings sheet, or
global hotkeys.

Hotkeys (Ctrl+Alt + ...):
  Up / Down      opacity            Space   play / pause
  Left / Right   seek -/+ 10s       L       unlock to move & resize
  T              toolbar mode       C       settings & crop
  P              precision trim     H       home
  + / -          size bigger / smaller (main or numpad keys)
  Q              quit

Run:  pythonw ghost_pip.pyw [title-keyword]
      With a keyword (e.g. "youtube") it starts mirroring the first matching
      window straight away instead of opening the home screen.
"""
import configparser
import ctypes
import ctypes.wintypes as wt
import json
import os
import queue
import sys
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
from collections import namedtuple

try:
    from PIL import Image, ImageChops, ImageDraw, ImageEnhance, ImageFont, ImageOps, ImageStat, ImageTk
except ImportError:
    Image = None

APP_NAME = "Ghost PiP"
HERE = os.path.dirname(os.path.abspath(__file__))
SETTINGS_PATH = os.path.join(HERE, "settings.json")
ICON_PATH = os.path.join(HERE, "ghost_pip.ico")
FONTS_DIR = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
MUTEX_NAME = "GhostPiP.App"
SHOW_EVENT_NAME = "GhostPiP.Show"

DEFAULTS = {
    "opacity": 0.6,
    "crop": [0.0, 0.0, 1.0, 1.0],
    "geometry": None,          # [x, y, width]; height follows the crop's shape
    "scale": None,             # 1.0 = the video's real pixel size
    "toolbar": "hover",        # hover | always | off
    "match": "youtube",        # tab lock keyword
}
TOOLBAR_MODES = {"hover": "Auto-hide", "always": "Always", "off": "Off"}

# DPI awareness has to be set before any window exists.
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

# --------------------------------------------------------------------------
# Win32
# --------------------------------------------------------------------------
user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32")
dwmapi = ctypes.WinDLL("dwmapi")

GWL_EXSTYLE = -20
WS_EX_TRANSPARENT = 0x20
WS_EX_TOOLWINDOW = 0x80
WS_EX_LAYERED = 0x80000
WS_EX_NOACTIVATE = 0x08000000
LWA_ALPHA = 0x2
GA_ROOT = 2
HWND_TOPMOST = -1
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x1, 0x2, 0x10
MONITOR_DEFAULTTONEAREST, MONITOR_DEFAULTTOPRIMARY = 2, 1

DWMWA_CLOAKED = 14
DWMWA_USE_IMMERSIVE_DARK_MODE = 20
DWMWA_WINDOW_CORNER_PREFERENCE = 33
DWMWA_BORDER_COLOR = 34
DWMWA_CAPTION_COLOR = 35
DWM_TNP_RECTDESTINATION = 0x1
DWM_TNP_RECTSOURCE = 0x2
DWM_TNP_OPACITY = 0x4
DWM_TNP_VISIBLE = 0x8
DWM_TNP_SOURCECLIENTAREAONLY = 0x10

PW_CLIENTONLY, PW_RENDERFULLCONTENT = 0x1, 0x2
WM_GETICON, ICON_SMALL, ICON_BIG, ICON_SMALL2 = 0x7F, 0, 1, 2
GCLP_HICON, GCLP_HICONSM = -14, -34
SMTO_ABORTIFHUNG = 0x2
DI_NORMAL = 0x3
CURSOR_SHOWING = 0x1

MOD_ALT, MOD_CONTROL, MOD_NOREPEAT = 0x1, 0x2, 0x4000
WM_HOTKEY = 0x0312
KEYEVENTF_KEYUP = 0x2
VK_CONTROL, VK_MENU, VK_SPACE = 0x11, 0x12, 0x20
VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN = 0x25, 0x26, 0x27, 0x28
VK_ADD, VK_SUBTRACT, VK_OEM_PLUS, VK_OEM_MINUS = 0x6B, 0x6D, 0xBB, 0xBD
VK_VOLUME_MUTE, VK_VOLUME_DOWN, VK_VOLUME_UP = 0xAD, 0xAE, 0xAF
VK_MEDIA_NEXT, VK_MEDIA_PREV, VK_MEDIA_PLAY_PAUSE = 0xB0, 0xB1, 0xB3
ERROR_ALREADY_EXISTS = 183
EVENT_MODIFY_STATE = 0x2


class DWM_THUMBNAIL_PROPERTIES(ctypes.Structure):
    _fields_ = [("dwFlags", wt.DWORD), ("rcDestination", wt.RECT), ("rcSource", wt.RECT),
                ("opacity", ctypes.c_ubyte), ("fVisible", wt.BOOL), ("fSourceClientAreaOnly", wt.BOOL)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


class CURSORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("flags", wt.DWORD), ("hCursor", wt.HANDLE), ("ptScreenPos", wt.POINT)]


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT), ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


def _proto(fn, restype, *argtypes):
    fn.restype, fn.argtypes = restype, argtypes


H = wt.HWND
WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
_proto(user32.EnumWindows, wt.BOOL, WNDENUMPROC, wt.LPARAM)
_proto(user32.GetWindowLongPtrW, ctypes.c_ssize_t, H, ctypes.c_int)
_proto(user32.SetWindowLongPtrW, ctypes.c_ssize_t, H, ctypes.c_int, ctypes.c_ssize_t)
_proto(user32.GetClassLongPtrW, ctypes.c_size_t, H, ctypes.c_int)
_proto(user32.GetAncestor, H, H, wt.UINT)
_proto(user32.SetWindowPos, wt.BOOL, H, H, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT)
_proto(user32.SetLayeredWindowAttributes, wt.BOOL, H, wt.COLORREF, wt.BYTE, wt.DWORD)
_proto(user32.GetForegroundWindow, H)
_proto(user32.SetForegroundWindow, wt.BOOL, H)
_proto(user32.IsWindow, wt.BOOL, H)
_proto(user32.IsWindowVisible, wt.BOOL, H)
_proto(user32.IsIconic, wt.BOOL, H)
_proto(user32.GetClientRect, wt.BOOL, H, ctypes.POINTER(wt.RECT))
_proto(user32.GetWindowRect, wt.BOOL, H, ctypes.POINTER(wt.RECT))
_proto(user32.GetWindowTextLengthW, ctypes.c_int, H)
_proto(user32.GetWindowTextW, ctypes.c_int, H, wt.LPWSTR, ctypes.c_int)
_proto(user32.GetWindowThreadProcessId, wt.DWORD, H, ctypes.POINTER(wt.DWORD))
_proto(user32.SendMessageTimeoutW, ctypes.c_ssize_t, H, wt.UINT, wt.WPARAM, wt.LPARAM,
       wt.UINT, wt.UINT, ctypes.POINTER(ctypes.c_size_t))
_proto(user32.DrawIconEx, wt.BOOL, wt.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_size_t,
       ctypes.c_int, ctypes.c_int, wt.UINT, ctypes.c_void_p, wt.UINT)
_proto(user32.PrintWindow, wt.BOOL, H, wt.HDC, wt.UINT)
_proto(user32.GetDC, wt.HDC, H)
_proto(user32.ReleaseDC, ctypes.c_int, H, wt.HDC)
_proto(user32.GetCursorInfo, wt.BOOL, ctypes.POINTER(CURSORINFO))
_proto(user32.MonitorFromWindow, wt.HMONITOR, H, wt.DWORD)
_proto(user32.MonitorFromPoint, wt.HMONITOR, wt.POINT, wt.DWORD)
_proto(user32.GetMonitorInfoW, wt.BOOL, wt.HMONITOR, ctypes.POINTER(MONITORINFO))
_proto(user32.RegisterHotKey, wt.BOOL, H, ctypes.c_int, wt.UINT, wt.UINT)
_proto(user32.GetMessageW, wt.BOOL, ctypes.POINTER(wt.MSG), H, wt.UINT, wt.UINT)
_proto(user32.keybd_event, None, wt.BYTE, wt.BYTE, wt.DWORD, ctypes.c_size_t)
_proto(user32.MessageBoxW, ctypes.c_int, H, wt.LPCWSTR, wt.LPCWSTR, wt.UINT)
_proto(gdi32.CreateCompatibleDC, wt.HDC, wt.HDC)
_proto(gdi32.CreateCompatibleBitmap, wt.HBITMAP, wt.HDC, ctypes.c_int, ctypes.c_int)
_proto(gdi32.CreateDIBSection, wt.HBITMAP, wt.HDC, ctypes.POINTER(BITMAPINFO), wt.UINT,
       ctypes.POINTER(ctypes.c_void_p), wt.HANDLE, wt.DWORD)
_proto(gdi32.SelectObject, wt.HGDIOBJ, wt.HDC, wt.HGDIOBJ)
_proto(gdi32.GetDIBits, ctypes.c_int, wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT,
       ctypes.c_void_p, ctypes.POINTER(BITMAPINFO), wt.UINT)
_proto(gdi32.DeleteObject, wt.BOOL, wt.HGDIOBJ)
_proto(gdi32.DeleteDC, wt.BOOL, wt.HDC)
_proto(gdi32.GdiFlush, wt.BOOL)
_proto(dwmapi.DwmRegisterThumbnail, ctypes.c_long, H, H, ctypes.POINTER(ctypes.c_void_p))
_proto(dwmapi.DwmUnregisterThumbnail, ctypes.c_long, ctypes.c_void_p)
_proto(dwmapi.DwmUpdateThumbnailProperties, ctypes.c_long, ctypes.c_void_p,
       ctypes.POINTER(DWM_THUMBNAIL_PROPERTIES))
_proto(dwmapi.DwmSetWindowAttribute, ctypes.c_long, H, wt.DWORD, ctypes.c_void_p, wt.DWORD)
_proto(dwmapi.DwmGetWindowAttribute, ctypes.c_long, H, wt.DWORD, ctypes.c_void_p, wt.DWORD)
_proto(kernel32.OpenProcess, wt.HANDLE, wt.DWORD, wt.BOOL, wt.DWORD)
_proto(kernel32.CloseHandle, wt.BOOL, wt.HANDLE)
_proto(kernel32.QueryFullProcessImageNameW, wt.BOOL, wt.HANDLE, wt.DWORD, wt.LPWSTR,
       ctypes.POINTER(wt.DWORD))
_proto(kernel32.GetProcessTimes, wt.BOOL, wt.HANDLE, *[ctypes.POINTER(wt.FILETIME)] * 4)
_proto(kernel32.CreateMutexW, wt.HANDLE, ctypes.c_void_p, wt.BOOL, wt.LPCWSTR)
_proto(kernel32.CreateEventW, wt.HANDLE, ctypes.c_void_p, wt.BOOL, wt.BOOL, wt.LPCWSTR)
_proto(kernel32.OpenEventW, wt.HANDLE, wt.DWORD, wt.BOOL, wt.LPCWSTR)
_proto(kernel32.SetEvent, wt.BOOL, wt.HANDLE)
_proto(kernel32.WaitForSingleObject, wt.DWORD, wt.HANDLE, wt.DWORD)


Win = namedtuple("Win", "hwnd title app exe")

APP_NAMES = {
    "firefox": "Firefox", "chrome": "Chrome", "msedge": "Edge", "brave": "Brave",
    "opera": "Opera", "vivaldi": "Vivaldi", "discord": "Discord", "vlc": "VLC",
    "obs64": "OBS", "spotify": "Spotify", "explorer": "File Explorer", "code": "VS Code",
    "mpv": "mpv", "steamwebhelper": "Steam", "applicationframehost": "App",
}
CHROMIUM = {"chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "vivaldi.exe"}


def window_title(hwnd):
    n = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def window_pid(hwnd):
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def process_path(pid):
    h = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return ""
    try:
        buf, n = ctypes.create_unicode_buffer(520), wt.DWORD(520)
        return buf.value if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)) else ""
    finally:
        kernel32.CloseHandle(h)


def process_started(pid):
    """Process start time as a Unix timestamp, or None."""
    h = kernel32.OpenProcess(0x1000, False, pid)
    if not h:
        return None
    try:
        times = [wt.FILETIME() for _ in range(4)]
        if not kernel32.GetProcessTimes(h, *[ctypes.byref(t) for t in times]):
            return None
        ft = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        return ft / 1e7 - 11644473600
    finally:
        kernel32.CloseHandle(h)


def app_name(exe):
    stem = os.path.splitext(os.path.basename(exe))[0]
    if not stem:
        return "App"
    return APP_NAMES.get(stem.lower(), stem[:1].upper() + stem[1:])


def _is_candidate(hwnd):
    if not user32.IsWindowVisible(hwnd):
        return False
    if user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW:
        return False
    cloaked = ctypes.c_int(0)
    dwmapi.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ctypes.byref(cloaked), 4)
    return not cloaked.value


def list_windows():
    """Top-level windows you'd see in Alt+Tab, most recently used first."""
    me, out = os.getpid(), []

    @WNDENUMPROC
    def cb(hwnd, _):
        if _is_candidate(hwnd):
            title = window_title(hwnd)
            pid = window_pid(hwnd)
            if title and title != "Program Manager" and pid != me:
                exe = process_path(pid)
                out.append(Win(hwnd, title, app_name(exe), exe))
        return True

    user32.EnumWindows(cb, 0)
    return out


def find_window(keyword, exclude=None):
    """First visible window whose title contains keyword (case-insensitive)."""
    keyword, me, found = keyword.lower(), os.getpid(), []

    @WNDENUMPROC
    def cb(hwnd, _):
        if hwnd != exclude and _is_candidate(hwnd) and keyword in window_title(hwnd).lower() \
                and window_pid(hwnd) != me:
            found.append(hwnd)
            return False
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def client_size(hwnd):
    r = wt.RECT()
    if not hwnd or not user32.GetClientRect(hwnd, ctypes.byref(r)):
        return 0, 0
    return r.right, r.bottom


def window_rect(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def work_area(hwnd=None):
    if hwnd:
        mon = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
    else:
        mon = user32.MonitorFromPoint(wt.POINT(0, 0), MONITOR_DEFAULTTOPRIMARY)
    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    user32.GetMonitorInfoW(mon, ctypes.byref(mi))
    return mi.rcWork


def visible_cursor_pos():
    """Cursor position, or None while it's hidden (e.g. captured by a game)."""
    ci = CURSORINFO()
    ci.cbSize = ctypes.sizeof(CURSORINFO)
    if not user32.GetCursorInfo(ctypes.byref(ci)) or not ci.flags & CURSOR_SHOWING:
        return None
    return ci.ptScreenPos.x, ci.ptScreenPos.y


def hwnd_of(widget):
    widget.update_idletasks()
    return user32.GetAncestor(widget.winfo_id(), GA_ROOT)


def set_exstyle(hwnd, add=0, remove=0):
    ex = user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE)
    user32.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (ex | add) & ~remove)


def set_alpha(hwnd, alpha):
    user32.SetLayeredWindowAttributes(hwnd, 0, max(0, min(255, int(alpha))), LWA_ALPHA)


def keep_on_top(hwnd):
    user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)


def _dwm_int(hwnd, attr, value):
    v = ctypes.c_int(value)
    dwmapi.DwmSetWindowAttribute(hwnd, attr, ctypes.byref(v), 4)


def colorref(hex_color):
    r, g, b = int(hex_color[1:3], 16), int(hex_color[3:5], 16), int(hex_color[5:7], 16)
    return r | (g << 8) | (b << 16)


def round_corners(hwnd):
    _dwm_int(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, 2)


def style_titlebar(hwnd):
    _dwm_int(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1)
    _dwm_int(hwnd, DWMWA_CAPTION_COLOR, colorref(BG))
    _dwm_int(hwnd, DWMWA_BORDER_COLOR, colorref(OUTLINE))


def tap(vk):
    user32.keybd_event(vk, 0, 0, 0)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


def release_modifiers():
    # Hotkeys arrive while Ctrl+Alt are still held; let go so they don't
    # combine with the keys we send.
    for vk in (VK_CONTROL, VK_MENU):
        user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


def send_to_window(hwnd, vk):
    """Briefly focus hwnd, tap a key, then hand focus back."""
    prev = user32.GetForegroundWindow()
    release_modifiers()
    user32.SetForegroundWindow(hwnd)
    tap(vk)
    if prev and prev != hwnd:
        user32.SetForegroundWindow(prev)


def capture(hwnd):
    """Snapshot of a window's client area, even when it's covered."""
    w, h = client_size(hwnd)
    if w < 2 or h < 2 or user32.IsIconic(hwnd):
        return None
    screen = user32.GetDC(None)
    dc = gdi32.CreateCompatibleDC(screen)
    bmp = gdi32.CreateCompatibleBitmap(screen, w, h)
    old = gdi32.SelectObject(dc, bmp)
    try:
        if not user32.PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT):
            return None
        bmi = BITMAPINFO()
        hdr = bmi.bmiHeader
        hdr.biSize, hdr.biWidth, hdr.biHeight = ctypes.sizeof(BITMAPINFOHEADER), w, -h
        hdr.biPlanes, hdr.biBitCount = 1, 32
        buf = ctypes.create_string_buffer(w * h * 4)
        if not gdi32.GetDIBits(dc, bmp, 0, h, buf, ctypes.byref(bmi), 0):
            return None
        return Image.frombuffer("RGB", (w, h), buf.raw, "raw", "BGRX", 0, 1)
    finally:
        gdi32.SelectObject(dc, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(dc)
        user32.ReleaseDC(None, screen)


def window_icon(hwnd, size):
    """The window's own icon as a premultiplied RGBA image, or None."""
    res, hicon = ctypes.c_size_t(0), 0
    for which in (ICON_BIG, ICON_SMALL2, ICON_SMALL):
        if user32.SendMessageTimeoutW(hwnd, WM_GETICON, which, 0, SMTO_ABORTIFHUNG, 60,
                                      ctypes.byref(res)) and res.value:
            hicon = res.value
            break
    hicon = hicon or user32.GetClassLongPtrW(hwnd, GCLP_HICON) or user32.GetClassLongPtrW(hwnd, GCLP_HICONSM)
    if not hicon:
        return None
    bmi = BITMAPINFO()
    hdr = bmi.bmiHeader
    hdr.biSize, hdr.biWidth, hdr.biHeight = ctypes.sizeof(BITMAPINFOHEADER), size, -size
    hdr.biPlanes, hdr.biBitCount = 1, 32
    bits = ctypes.c_void_p()
    dc = gdi32.CreateCompatibleDC(None)
    bmp = gdi32.CreateDIBSection(dc, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    if not bmp:
        gdi32.DeleteDC(dc)
        return None
    old = gdi32.SelectObject(dc, bmp)
    try:
        if not user32.DrawIconEx(dc, 0, 0, hicon, size, size, 0, None, DI_NORMAL):
            return None
        gdi32.GdiFlush()
        raw = ctypes.string_at(bits, size * size * 4)
    finally:
        gdi32.SelectObject(dc, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(dc)
    img = Image.frombuffer("RGBA", (size, size), raw, "raw", "BGRA", 0, 1)
    if img.getchannel("A").getextrema()[1] == 0:  # old-style icon without alpha
        img.putalpha(255)
    return img


# --------------------------------------------------------------------------
# Firefox health check
# --------------------------------------------------------------------------
FIREFOX_PREFS = {
    "widget.windows.window_occlusion_tracking.enabled": "false",
    "dom.min_background_timeout_value": "0",
}


def firefox_profile():
    base = os.path.join(os.environ.get("APPDATA", ""), "Mozilla", "Firefox")
    cp = configparser.ConfigParser(interpolation=None, strict=False)
    try:
        cp.read(os.path.join(base, "profiles.ini"), encoding="utf-8")
    except Exception:
        return None
    path = None
    for sec in cp.sections():
        if sec.startswith("Install") and cp.has_option(sec, "Default"):
            path = cp.get(sec, "Default")
            break
    if not path:
        for sec in cp.sections():
            if sec.startswith("Profile") and cp.get(sec, "Default", fallback="0") == "1":
                path = cp.get(sec, "Path", fallback=None)
                break
    if not path:
        return None
    full = os.path.normpath(path if os.path.isabs(path) else os.path.join(base, path))
    return full if os.path.isdir(full) else None


def firefox_status(pid):
    """'fix' (prefs missing), 'restart' (set but Firefox predates them), 'ok', or None."""
    prof = firefox_profile()
    if not prof:
        return None
    user_js = os.path.join(prof, "user.js")
    try:
        with open(user_js, encoding="utf-8") as f:
            text = f.read()
    except OSError:
        text = ""
    if not all(f'"{k}", {v})' in text for k, v in FIREFOX_PREFS.items()):
        return "fix"
    started = process_started(pid)
    if started and started < os.path.getmtime(user_js) - 1:
        return "restart"
    return "ok"


def firefox_fix():
    prof = firefox_profile()
    if not prof:
        return False
    user_js = os.path.join(prof, "user.js")
    try:
        with open(user_js, encoding="utf-8") as f:
            lines = [l for l in f.read().splitlines()
                     if not any(k in l for k in FIREFOX_PREFS) and "Ghost PiP" not in l]
    except OSError:
        lines = []
    lines.append("// Added by Ghost PiP: keep drawing while covered so mirrors stay live.")
    lines += [f'user_pref("{k}", {v});' for k, v in FIREFOX_PREFS.items()]
    with open(user_js, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    return True


# --------------------------------------------------------------------------
# Settings
# --------------------------------------------------------------------------
def load_settings():
    s = dict(DEFAULTS)
    try:
        with open(SETTINGS_PATH, encoding="utf-8") as f:
            s.update(json.load(f))
    except Exception:
        pass
    if s.get("toolbar") not in TOOLBAR_MODES:
        s["toolbar"] = "hover"
    try:
        s["crop"] = [float(v) for v in s["crop"]][:4]
        assert len(s["crop"]) == 4
    except Exception:
        s["crop"] = list(DEFAULTS["crop"])
    g = s.get("geometry")
    if not (isinstance(g, list) and len(g) == 3):
        s["geometry"] = None
    s["opacity"] = max(0.1, min(1.0, float(s.get("opacity", 0.6))))
    try:
        s["scale"] = max(0.02, float(s["scale"])) if s.get("scale") is not None else None
    except (TypeError, ValueError):
        s["scale"] = None
    s["match"] = str(s.get("match") or "")
    s["locked"] = True  # always start click-through
    return s


def save_settings(s):
    data = {k: s[k] for k in DEFAULTS}
    try:
        with open(SETTINGS_PATH, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
    except OSError:
        pass


# --------------------------------------------------------------------------
# Theme
# --------------------------------------------------------------------------
BG = "#121316"
SURFACE = "#1b1c20"
SURFACE_HI = "#25272c"
SURFACE_TOP = "#30333a"
SEL = "#243149"
OUTLINE = "#33363d"
TEXT = "#e6e7eb"
MUTED = "#9ba0aa"
PRIMARY = "#a8c7fa"
PRIMARY_HI = "#c4d9fc"
ON_PRIMARY = "#0b2a5b"
OK = "#7fd1a0"
WARN = "#f6c36b"
MONO_COLORS = ["#a8c7fa", "#7fd1a0", "#f6c36b", "#f2a39b", "#d7aefb", "#78d9ec"]

IC = {
    "prev": "\ue892", "next": "\ue893", "play": "\ue768", "pause": "\ue769",
    "rew": "\ueb9e", "ff": "\ueb9d", "lock": "\ue72e", "unlock": "\ue785",
    "crop": "\ue7a8", "settings": "\ue713", "home": "\ue80f", "close": "\ue8bb",
    "search": "\ue721", "refresh": "\ue72c", "vol_down": "\ue992", "vol_up": "\ue995",
    "mute": "\ue74f", "warn": "\ue7ba", "pip": "\uee49", "opacity": "\ue706",
    "move": "\ue7c2", "undo": "\ue7a7", "redo": "\ue7a6", "check": "\ue73e",
    "zoom": "\ue8a3", "zoom_out": "\ue71f", "ruler": "\ued5e", "target": "\ue81d",
}

S = 1.0
UI_FONT = SEMIBOLD = DISPLAY_FONT = ICON_FONT = "Segoe UI"
LANCZOS = Image.Resampling.LANCZOS if Image else None
_pil_fonts = {}
_PIL_FILES = {
    "regular": ["segoeui.ttf"],
    "semibold": ["seguisb.ttf", "segoeuib.ttf", "segoeui.ttf"],
    "icons": ["SegoeIcons.ttf", "segmdl2.ttf"],
}


def px(n):
    return int(round(n * S))


def init_theme(root):
    global S, UI_FONT, SEMIBOLD, DISPLAY_FONT, ICON_FONT
    S = root.winfo_fpixels("1i") / 96.0
    fams = set(tkfont.families(root))

    def pick(*names):
        return next((n for n in names if n in fams), "Segoe UI")

    UI_FONT = pick("Segoe UI Variable Text", "Segoe UI")
    SEMIBOLD = pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold")
    DISPLAY_FONT = pick("Segoe UI Variable Display Semib", "Segoe UI Semibold")
    ICON_FONT = pick("Segoe Fluent Icons", "Segoe MDL2 Assets")


def pil_font(kind, pt):
    key = (kind, pt)
    if key not in _pil_fonts:
        size = max(6, round(pt * S * 96 / 72))
        for name in _PIL_FILES[kind]:
            try:
                _pil_fonts[key] = ImageFont.truetype(os.path.join(FONTS_DIR, name), size)
                break
            except OSError:
                continue
        else:
            _pil_fonts[key] = ImageFont.load_default()
    return _pil_fonts[key]


def rounded_bg(w, h, r, fill, bg, outline=None):
    """Antialiased rounded rectangle (drawn 4x and scaled down)."""
    k = 4
    im = Image.new("RGB", (w * k, h * k), bg)
    ImageDraw.Draw(im).rounded_rectangle((0, 0, w * k - 1, h * k - 1), radius=r * k, fill=fill,
                                         outline=outline, width=k if outline else 0)
    return im.resize((w, h), LANCZOS)


def ellipsize(text, font, maxw):
    if font.getlength(text) <= maxw:
        return text
    while text and font.getlength(text + "…") > maxw:
        text = text[:-1]
    return text.rstrip() + "…"


def make_icon(size):
    """App icon: a dim screen with a bright, slightly translucent PiP window."""
    k = 4
    s = size * k
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * 0.24), fill="#1d2230")
    m = s * 0.17
    d.rounded_rectangle((m, m * 1.3, s - m, s - m * 1.3), radius=int(s * 0.07),
                        outline="#6f7889", width=max(1, int(s * 0.045)))
    pw, ph = s * 0.42, s * 0.29
    x1, y1 = s - m * 0.62, s - m * 0.95
    d.rounded_rectangle((x1 - pw, y1 - ph, x1, y1), radius=int(s * 0.055), fill=(168, 199, 250, 235))
    cx, cy, t = x1 - pw / 2, y1 - ph / 2, ph * 0.24
    d.polygon([(cx - t * 0.75, cy - t), (cx - t * 0.75, cy + t), (cx + t, cy)], fill=ON_PRIMARY)
    return im.resize((size, size), LANCZOS)


def find_motion(frames):
    """Fractional (l, t, w, h) box around the part of the window that moved
    between frames (i.e. the playing video), or None if nothing moved."""
    if len(frames) < 2 or len({f.size for f in frames}) != 1:
        return None  # window resized mid-scan
    w0 = 320
    small = [f.convert("L").resize((w0, max(1, round(w0 * f.height / f.width))),
                                   Image.Resampling.BILINEAR) for f in frames]
    w, h = small[0].size
    mask = Image.new("L", (w, h), 0)
    for a, b in zip(small, small[1:]):
        mask = ImageChops.lighter(mask, ImageChops.difference(a, b).point(lambda v: 255 if v > 16 else 0))

    def longest_run(values, thresh):
        best, start = None, None
        for i, v in enumerate(list(values) + [0]):
            if v > thresh and start is None:
                start = i
            elif v <= thresh and start is not None:
                if not best or i - start > best[1] - best[0]:
                    best = (start, i)
                start = None
        return best

    def profile(img, axis):  # mean change per column (axis 0) or row (axis 1)
        size = (img.width, 1) if axis == 0 else (1, img.height)
        return img.resize(size, Image.Resampling.BOX).getdata()

    # Columns first, then rows inside those columns, then columns again inside
    # those rows. Small changing bits (spinners, clocks) fall below the threshold.
    cols = longest_run(profile(mask, 0), 255 * 0.03)
    if not cols:
        return None
    rows = longest_run(profile(mask.crop((cols[0], 0, cols[1], h)), 1), 255 * 0.03)
    if not rows:
        return None
    cols2 = longest_run(profile(mask.crop((0, rows[0], w, rows[1])), 0), 255 * 0.03)
    if cols2:
        cols = cols2
    l, t, cw, ch = cols[0] / w, rows[0] / h, (cols[1] - cols[0]) / w, (rows[1] - rows[0]) / h
    if cw < 0.05 or ch < 0.05:
        return None
    return [round(l, 4), round(t, 4), round(cw, 4), round(ch, 4)]


def strongest_edge(img, side, line, span, radius=40):
    """Pixel boundary near `line` with the sharpest colour change across `span`
    (columns for top/bottom, rows for left/right), or None if it's all smooth."""
    a, b = span
    horizontal = side in ("top", "bottom")
    size = img.height if horizontal else img.width
    lo, hi = max(1, line - radius), min(size - 1, line + radius)
    if hi < lo or b - a < 2:
        return None
    if horizontal:
        vals = list(img.crop((a, lo - 1, b, hi + 1)).resize((1, hi - lo + 2), Image.Resampling.BOX).getdata())
    else:
        vals = list(img.crop((lo - 1, a, hi + 1, b)).resize((hi - lo + 2, 1), Image.Resampling.BOX).getdata())
    best, best_p = 0.0, None
    for i in range(1, len(vals)):
        p = lo - 1 + i  # boundary between pixel p-1 and pixel p
        score = sum(abs(x - y) for x, y in zip(vals[i - 1], vals[i])) - abs(p - line) * 0.05
        if score > best:
            best, best_p = score, p
    return best_p if best > 24 else None


def shave_flat(img, side, edges, min_px):
    """Move one cut inward past rows/columns of flat colour (letterbox bars,
    page background). Returns the new line, or None if the edge isn't flat."""
    L, T, R, B = edges

    def stats(p):
        box = (L, p, R, p + 1) if side in ("top", "bottom") else (p, T, p + 1, B)
        st = ImageStat.Stat(img.crop(box))
        return st.mean, max(st.stddev)

    start, step, limit = {
        "top": (T, 1, B - min_px), "bottom": (B - 1, -1, T + min_px - 1),
        "left": (L, 1, R - min_px), "right": (R - 1, -1, L + min_px - 1),
    }[side]
    ref, sd = stats(start)
    if sd > 6:
        return None
    p = start
    while (p < limit) if step > 0 else (p > limit):
        mean, sd = stats(p)
        if sd > 6 or sum(abs(x - y) for x, y in zip(mean, ref)) > 24:
            break
        p += step
    line = p if step > 0 else p + 1
    return None if line == (start if step > 0 else start + 1) else line


def ratio_label(w, h):
    if h <= 0:
        return ""
    r = w / h
    for a, b in ((16, 9), (4, 3), (21, 9), (32, 9), (3, 2), (5, 4), (1, 1), (9, 16)):
        if abs(r / (a / b) - 1) < 0.005:
            return f"{a}:{b}"
    return f"{r:.3f}:1"


def flatten_icon(icon, fill):
    """Composite a premultiplied RGBA icon onto a solid colour."""
    inv = ImageOps.invert(icon.getchannel("A")).convert("RGB")
    return ImageChops.add(icon.convert("RGB"), ImageChops.multiply(Image.new("RGB", icon.size, fill), inv))


# --------------------------------------------------------------------------
# Widgets
# --------------------------------------------------------------------------
PILL_KINDS = {  # kind: (fill, hover fill, text colour)
    "primary": (PRIMARY, PRIMARY_HI, ON_PRIMARY),
    "tonal": (SURFACE_HI, SURFACE_TOP, TEXT),
    "ghost": (None, SURFACE_HI, PRIMARY),
    "selected": (SEL, SEL, PRIMARY),
}


def pill_image(text, icon, kind, bg, hover, minw, h, padx=18):
    fill, hfill, fg = PILL_KINDS[kind]
    fill = (hfill if hover else fill) or bg
    font, ifont = pil_font("semibold", 9.5), pil_font("icons", 10.5)
    tw = font.getlength(text) if text else 0
    iw = ifont.getlength(icon) if icon else 0
    gap = px(8) if text and icon else 0
    H = px(h)
    W = max(px(minw), int(tw + iw + gap + px(padx) * 2)) if text else H
    im = rounded_bg(W, H, H // 2, fill, bg)
    d = ImageDraw.Draw(im)
    x, cy = (W - (tw + iw + gap)) / 2, H / 2
    if icon:
        d.text((x, cy + px(1)), icon, font=ifont, fill=fg, anchor="lm")
        x += iw + gap
    if text:
        d.text((x, cy), text, font=font, fill=fg, anchor="lm")
    return ImageTk.PhotoImage(im)


class Pill(tk.Label):
    """Rounded button rendered with PIL so the edges are smooth."""

    def __init__(self, parent, text="", command=None, kind="tonal", icon=None, minw=0, h=36, padx=18):
        self.bgc = parent["bg"]
        super().__init__(parent, bd=0, bg=self.bgc, cursor="hand2", highlightthickness=0)
        self.command, self._spec = command, [text, icon, minw, h, padx]
        self.set_kind(kind)
        self.bind("<Enter>", lambda e: self.configure(image=self.img_h))
        self.bind("<Leave>", lambda e: self.configure(image=self.img_n))
        self.bind("<ButtonRelease-1>", self._click)

    def set_kind(self, kind):
        self.kind = kind
        text, icon, minw, h, padx = self._spec
        self.img_n = pill_image(text, icon, kind, self.bgc, False, minw, h, padx)
        self.img_h = pill_image(text, icon, kind, self.bgc, True, minw, h, padx)
        self.configure(image=self.img_n)

    def set_text(self, text, icon=None):
        self._spec[0] = text
        if icon:
            self._spec[1] = icon
        self.set_kind(self.kind)

    def _click(self, e):
        if 0 <= e.x < self.winfo_width() and 0 <= e.y < self.winfo_height() and self.command:
            self.command()


class Slider(tk.Canvas):
    def __init__(self, parent, lo, hi, value, command=None, width=240, step=1):
        bg = parent["bg"]
        super().__init__(parent, width=px(width), height=px(28), bg=bg, highlightthickness=0,
                         bd=0, cursor="hand2")
        self.lo, self.hi, self.step, self.command, self.value = lo, hi, step, command, value
        self.r = r = px(8)
        k = 4
        dot = Image.new("RGBA", (r * 2 * k, r * 2 * k), (0, 0, 0, 0))
        ImageDraw.Draw(dot).ellipse((0, 0, r * 2 * k - 1, r * 2 * k - 1), fill=PRIMARY)
        self.thumb = ImageTk.PhotoImage(dot.resize((r * 2, r * 2), LANCZOS))
        self.bind("<Button-1>", lambda e: self._from_x(e.x))
        self.bind("<B1-Motion>", lambda e: self._from_x(e.x))
        self.bind("<MouseWheel>", self._wheel)
        self.bind("<Configure>", lambda e: self.draw())
        self.draw()

    def _from_x(self, x):
        w = max(self.winfo_width(), 2 * self.r + 1)
        frac = min(1.0, max(0.0, (x - self.r) / (w - 2 * self.r)))
        self.set(self.lo + frac * (self.hi - self.lo), notify=True)

    def _wheel(self, e):
        self.set(self.value + (self.step * 5 if e.delta > 0 else -self.step * 5), notify=True)
        return "break"

    def set(self, v, notify=False):
        v = min(self.hi, max(self.lo, round(v / self.step) * self.step))
        changed = v != self.value
        self.value = v
        self.draw()
        if notify and changed and self.command:
            self.command(v)

    def draw(self):
        self.delete("all")
        w = self.winfo_width() if self.winfo_width() > 1 else int(self["width"])
        cy, r = int(self["height"]) // 2, self.r
        x = r + (self.value - self.lo) / (self.hi - self.lo) * (w - 2 * r)
        self.create_line(r, cy, w - r, cy, fill=OUTLINE, width=px(4), capstyle="round")
        self.create_line(r, cy, x, cy, fill=PRIMARY, width=px(4), capstyle="round")
        self.create_image(x, cy, image=self.thumb)


class Segmented(tk.Frame):
    def __init__(self, parent, options, value, command):
        super().__init__(parent, bg=parent["bg"])
        self.command, self.pills = command, {}
        for v, label in options:
            p = Pill(self, label, lambda v=v: self.select(v, True),
                     kind="selected" if v == value else "tonal", h=32)
            p.pack(side="left", padx=(0, px(6)))
            self.pills[v] = p

    def select(self, value, notify=False):
        for v, p in self.pills.items():
            want = "selected" if v == value else "tonal"
            if p.kind != want:
                p.set_kind(want)
        if notify:
            self.command(value)


class Field(tk.Frame):
    """Rounded text field with an optional leading icon and placeholder."""

    def __init__(self, parent, var, width, icon=None, placeholder="", h=40):
        bg = parent["bg"]
        W, Hh = px(width), px(h)
        super().__init__(parent, width=W, height=Hh, bg=bg)
        self.img = ImageTk.PhotoImage(rounded_bg(W, Hh, Hh // 2, SURFACE_HI, bg))
        tk.Label(self, image=self.img, bd=0, bg=bg).place(x=0, y=0)
        self.x0 = px(16)
        if icon:
            tk.Label(self, text=icon, font=(ICON_FONT, 11), fg=MUTED, bg=SURFACE_HI).place(
                x=self.x0, rely=0.5, anchor="w")
            self.x0 = px(44)
        self.entry = tk.Entry(self, textvariable=var, bg=SURFACE_HI, fg=TEXT, insertbackground=TEXT,
                              relief="flat", bd=0, highlightthickness=0, font=(UI_FONT, 11),
                              selectbackground=SEL, selectforeground=TEXT)
        self.entry.place(x=self.x0, rely=0.5, anchor="w", width=W - self.x0 - px(18))
        self.var = var
        self.ph = None
        if placeholder:
            self.ph = tk.Label(self, text=placeholder, font=(UI_FONT, 11), fg=MUTED, bg=SURFACE_HI)
            self.ph.bind("<Button-1>", lambda e: self.entry.focus_set())
            var.trace_add("write", lambda *a: self._placeholder())
            self._placeholder()

    def _placeholder(self):
        if self.var.get():
            self.ph.place_forget()
        else:
            self.ph.place(x=self.x0, rely=0.5, anchor="w")


def heading(parent, title, desc=None, wrap=440):
    tk.Label(parent, text=title, font=(SEMIBOLD, 12), fg=TEXT, bg=parent["bg"]).pack(anchor="w")
    if desc:
        tk.Label(parent, text=desc, font=(UI_FONT, 9), fg=MUTED, bg=parent["bg"], justify="left",
                 wraplength=px(wrap)).pack(anchor="w", pady=(px(2), 0))


# --------------------------------------------------------------------------
# Window list (home screen)
# --------------------------------------------------------------------------
class WindowList(tk.Frame):
    ROW_FILL = {"normal": BG, "hover": SURFACE_HI, "selected": SEL}

    def __init__(self, parent, width, height, on_select, on_activate):
        bg = parent["bg"]
        self.W, self.Hh = px(width), px(height)
        super().__init__(parent, bg=bg, width=self.W, height=self.Hh)
        self.pack_propagate(False)
        self.canvas = tk.Canvas(self, bg=bg, highlightthickness=0, bd=0, width=self.W, height=self.Hh)
        self.canvas.pack(fill="both", expand=True)
        self.inner = tk.Frame(self.canvas, bg=bg)
        self.canvas.create_window(0, 0, window=self.inner, anchor="nw")
        self.inner.bind("<Configure>", lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.bind("<MouseWheel>", self._wheel)
        self.on_select, self.on_activate = on_select, on_activate
        self.items, self.rows, self.imgs, self.sel = [], [], [], -1
        self.icons = {}
        self.row_h = px(58)

    def _wheel(self, e):
        self.canvas.yview_scroll(-1 if e.delta > 0 else 1, "units")
        return "break"

    def _render(self, win, state):
        W, Hh = self.W - px(6), self.row_h
        fill = self.ROW_FILL[state]
        im = rounded_bg(W, Hh, px(14), fill, BG)
        d = ImageDraw.Draw(im)
        isz, ix = px(30), px(14)
        iy = (Hh - isz) // 2
        icon = self.icons.get(win.hwnd)
        if icon is not None:
            im.paste(flatten_icon(icon, fill), (ix, iy))
        else:
            k = 4
            mono = Image.new("RGB", (isz * k, isz * k), fill)
            color = MONO_COLORS[sum(map(ord, win.app)) % len(MONO_COLORS)]
            ImageDraw.Draw(mono).ellipse((0, 0, isz * k - 1, isz * k - 1), fill=color)
            im.paste(mono.resize((isz, isz), LANCZOS), (ix, iy))
            d.text((ix + isz / 2, iy + isz / 2), win.app[:1].upper(), font=pil_font("semibold", 11),
                   fill=BG, anchor="mm")
        tx = ix + isz + px(14)
        d.text((tx, Hh / 2 - px(2)), win.app, font=pil_font("semibold", 8.5),
               fill=PRIMARY if state == "selected" else MUTED, anchor="ls")
        d.text((tx, Hh / 2 + px(2)), ellipsize(win.title, pil_font("regular", 10), W - tx - px(16)),
               font=pil_font("regular", 10), fill=TEXT, anchor="lt")
        return ImageTk.PhotoImage(im)

    def set_items(self, wins, select=0):
        for r in self.rows:
            r.destroy()
        self.items, self.rows, self.imgs, self.sel = wins, [], [], -1
        for w in wins:
            if w.hwnd not in self.icons:
                try:
                    self.icons[w.hwnd] = window_icon(w.hwnd, px(30))
                except Exception:
                    self.icons[w.hwnd] = None
        for i, w in enumerate(wins):
            imgs = {s: self._render(w, s) for s in self.ROW_FILL}
            row = tk.Label(self.inner, image=imgs["normal"], bd=0, bg=BG, cursor="hand2")
            row.pack(pady=(0, px(2)))
            row.bind("<Enter>", lambda e, i=i: self._hover(i, True))
            row.bind("<Leave>", lambda e, i=i: self._hover(i, False))
            row.bind("<Button-1>", lambda e, i=i: self.select(i))
            row.bind("<Double-Button-1>", lambda e, i=i: (self.select(i), self.on_activate()))
            row.bind("<MouseWheel>", self._wheel)
            self.rows.append(row)
            self.imgs.append(imgs)
        if not wins:
            lbl = tk.Label(self.inner, text="No windows match", font=(UI_FONT, 10), fg=MUTED, bg=BG)
            lbl.pack(pady=px(40), padx=px(180))
            self.rows.append(lbl)
        self.canvas.yview_moveto(0)
        if wins:
            self.select(min(select, len(wins) - 1))
        else:
            self.on_select(None)

    def _hover(self, i, on):
        if i != self.sel and i < len(self.imgs):
            self.rows[i].configure(image=self.imgs[i]["hover" if on else "normal"])

    def select(self, i):
        if not self.items:
            return
        i = max(0, min(len(self.items) - 1, i))
        if 0 <= self.sel < len(self.rows):
            self.rows[self.sel].configure(image=self.imgs[self.sel]["normal"])
        self.sel = i
        self.rows[i].configure(image=self.imgs[i]["selected"])
        # Keep the selection in view.
        self.update_idletasks()
        total = max(1, self.inner.winfo_height())
        top, bottom = self.rows[i].winfo_y(), self.rows[i].winfo_y() + self.row_h
        view0, view1 = (f * total for f in self.canvas.yview())
        if top < view0:
            self.canvas.yview_moveto(top / total)
        elif bottom > view1:
            self.canvas.yview_moveto((bottom - self.Hh) / total)
        self.on_select(self.items[i])

    def move(self, d):
        self.select(self.sel + d)

    def selected(self):
        return self.items[self.sel] if 0 <= self.sel < len(self.items) else None


# --------------------------------------------------------------------------
# Overlay: the click-through mirror itself
# --------------------------------------------------------------------------
def popup(root, bg):
    """Borderless top-level, mapped off-screen once so its HWND exists."""
    t = tk.Toplevel(root)
    t.overrideredirect(True)
    t.attributes("-topmost", True)
    t.configure(bg=bg)
    t.geometry("+-32000+-32000")
    hwnd = hwnd_of(t)
    t.withdraw()
    return t, hwnd


class Overlay:
    def __init__(self, app):
        self.app = app
        self.win, self.hwnd = popup(app.root, "#000000")
        self.edge = tk.Frame(self.win, bg="#000000", highlightthickness=0,
                             highlightbackground=PRIMARY, highlightcolor=PRIMARY)
        self.edge.pack(fill="both", expand=True)
        set_exstyle(self.hwnd, add=WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
        round_corners(self.hwnd)
        self.thumb = ctypes.c_void_p()
        self.visible = False
        self.rect = (0, 0, 0, 0)       # x, y, w, h
        self.src_size = (0, 0)
        self._drag = None
        self.win.bind("<ButtonPress-1>", self._press)
        self.win.bind("<B1-Motion>", self._motion)
        self.win.bind("<ButtonRelease-1>", self._release)
        self.win.bind("<MouseWheel>", self._wheel)

    # thumbnail ------------------------------------------------------------
    def attach(self, source):
        self.detach()
        if dwmapi.DwmRegisterThumbnail(self.hwnd, source, ctypes.byref(self.thumb)) != 0:
            self.thumb = ctypes.c_void_p()
            return False
        return True

    def detach(self):
        if self.thumb:
            dwmapi.DwmUnregisterThumbnail(self.thumb)
            self.thumb = ctypes.c_void_p()

    def update_thumb(self):
        if not self.thumb or not self.app.source:
            return
        sw, sh = client_size(self.app.source)
        l, t, cw, ch = self.app.settings["crop"]
        _, _, w, h = self.rect
        inset = 0 if self.app.settings["locked"] else px(2)
        p = DWM_THUMBNAIL_PROPERTIES()
        p.dwFlags = (DWM_TNP_RECTSOURCE | DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE |
                     DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY)
        # round(), not int(): a pixel-exact crop must survive the fraction round trip.
        p.rcSource = wt.RECT(round(l * sw), round(t * sh), round((l + cw) * sw), round((t + ch) * sh))
        p.rcDestination = wt.RECT(inset, inset, w - inset, h - inset)
        p.opacity, p.fVisible, p.fSourceClientAreaOnly = 255, True, True
        dwmapi.DwmUpdateThumbnailProperties(self.thumb, ctypes.byref(p))

    # placement ---------------------------------------------------------------
    def crop_px(self):
        """Size of the cropped region in source pixels."""
        sw, sh = client_size(self.app.source)
        _, _, cw, ch = self.app.settings["crop"]
        return max(1.0, cw * sw), max(1.0, ch * sh)

    def scale_limits(self):
        cw, ch = self.crop_px()
        wa = work_area(self.hwnd if self.visible else None)
        hi = min((wa.right - wa.left) / cw, (wa.bottom - wa.top) / ch)  # fits on screen
        lo = min(hi, px(96) / max(cw, ch))                               # still findable
        return lo, hi

    def layout(self):
        """Size the overlay to crop × scale and place it. When the size changes,
        the corner nearest the screen's corner stays put, so a PiP parked
        bottom-right grows up and to the left, like on a phone."""
        s = self.app.settings
        self.src_size = client_size(self.app.source)
        cw, ch = self.crop_px()
        if s.get("scale") is None:  # first run, or settings from before scaling existed
            g = s.get("geometry")
            s["scale"] = (g[2] if g else px(420)) / cw
        lo, hi = self.scale_limits()
        s["scale"] = scale = min(max(s["scale"], lo), hi)
        w, h = max(1, round(cw * scale)), max(1, round(ch * scale))
        if not s["geometry"]:
            wa = work_area()
            s["geometry"] = [wa.right - w - px(24), wa.bottom - h - px(90), w]
        x, y = s["geometry"][:2]
        ox, oy, ow, oh = self.rect
        if self.visible and ow and (w, h) != (ow, oh):
            wa = work_area(self.hwnd)
            x = ox + ow - w if ox + ow / 2 > (wa.left + wa.right) / 2 else ox
            y = oy + oh - h if oy + oh / 2 > (wa.top + wa.bottom) / 2 else oy
        s["geometry"] = [x, y, w]
        self.rect = (x, y, w, h)
        self.win.geometry(f"{w}x{h}+{x}+{y}")
        self.update_thumb()

    def show(self):
        self.layout()
        if not self.visible:
            self.win.deiconify()
            self.visible = True
        self.style()
        keep_on_top(self.hwnd)

    def hide(self):
        if self.visible:
            self.win.withdraw()
            self.visible = False

    def style(self):
        locked = self.app.settings["locked"]
        if locked:
            set_exstyle(self.hwnd, add=WS_EX_LAYERED | WS_EX_TRANSPARENT)
        else:
            set_exstyle(self.hwnd, add=WS_EX_LAYERED, remove=WS_EX_TRANSPARENT)
        set_alpha(self.hwnd, self.app.settings["opacity"] * 255)
        self.edge.configure(highlightthickness=0 if locked else px(2))
        self.update_thumb()

    # mouse (only reaches us while unlocked) ---------------------------------
    def _press(self, e):
        x, y, _, _ = self.rect
        self._drag = (e.x_root, e.y_root, x, y)

    def _motion(self, e):
        if not self._drag:
            return
        sx, sy, x0, y0 = self._drag
        x, y = x0 + e.x_root - sx, y0 + e.y_root - sy
        self.app.settings["geometry"][:2] = [x, y]
        self.rect = (x, y) + self.rect[2:]
        self.win.geometry(f"+{x}+{y}")

    def _release(self, e):
        self._drag = None
        self.snap()
        self.app.mark_dirty()

    def snap(self):
        """Pixel-style: settle against nearby screen edges."""
        x, y, w, h = self.rect
        wa, m, reach = work_area(self.hwnd), px(16), px(40)
        if abs(x - wa.left) < reach:
            x = wa.left + m
        elif abs(wa.right - (x + w)) < reach:
            x = wa.right - m - w
        if abs(y - wa.top) < reach:
            y = wa.top + m
        elif abs(wa.bottom - (y + h)) < reach:
            y = wa.bottom - m - h
        self.app.settings["geometry"][:2] = [x, y]
        self.layout()

    def _wheel(self, e):
        self.app.step_scale(1 if e.delta > 0 else -1)


# --------------------------------------------------------------------------
# Toolbar: Pixel-style floating controls
# --------------------------------------------------------------------------
class Toolbar:
    def __init__(self, app):
        self.app = app
        self.win, self.hwnd = popup(app.root, SURFACE)
        bar = tk.Frame(self.win, bg=SURFACE, padx=px(10), pady=px(3))
        bar.pack()
        self.btns = {}

        def button(key, glyph, cmd, size=13):
            b = tk.Label(bar, text=glyph, font=(ICON_FONT, size), fg=TEXT, bg=SURFACE,
                         padx=px(8), pady=px(7), cursor="hand2")
            b.bind("<Enter>", lambda e: b.configure(fg=PRIMARY))
            b.bind("<Leave>", lambda e: b.configure(fg=self._fg(key)))
            b.bind("<ButtonRelease-1>", lambda e: cmd())
            b.pack(side="left")
            self.btns[key] = b

        def divider():
            tk.Frame(bar, bg=OUTLINE, width=max(1, px(1)), height=px(20)).pack(side="left", padx=px(6))

        button("prev", IC["prev"], lambda: app.media("prev"))
        button("rew", IC["rew"], lambda: app.media("back"))
        button("play", IC["pause"], lambda: app.media("play"), size=16)
        button("ff", IC["ff"], lambda: app.media("fwd"))
        button("next", IC["next"], lambda: app.media("next"))
        divider()
        button("smaller", IC["zoom_out"], lambda: app.step_scale(-1))
        button("bigger", IC["zoom"], lambda: app.step_scale(1))
        divider()
        button("lock", IC["lock"], app.toggle_lock)
        button("settings", IC["settings"], app.open_settings)
        button("home", IC["home"], app.show_home)
        button("close", IC["close"], app.quit)
        self.win.bind("<MouseWheel>", lambda e: app.set_opacity(
            app.settings["opacity"] + (0.05 if e.delta > 0 else -0.05)))

        set_exstyle(self.hwnd, add=WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
        round_corners(self.hwnd)
        set_alpha(self.hwnd, 0)
        self.alpha, self.target, self.shown = 0, 0, False
        self.hot_since = self.cold_since = None
        self.peek_until = 0.0

    def _fg(self, key):
        if key == "lock" and not self.app.settings["locked"]:
            return PRIMARY
        return TEXT

    def sync(self):
        self.btns["play"].configure(text=IC["pause"] if self.app.playing else IC["play"])
        locked = self.app.settings["locked"]
        self.btns["lock"].configure(text=IC["lock"] if locked else IC["unlock"], fg=self._fg("lock"))

    def peek(self, seconds=2.5):
        self.peek_until = time.time() + seconds

    def _hot(self):
        p = visible_cursor_pos()
        if not p:
            return False
        r = window_rect(self.app.overlay.hwnd)
        L, T, R, B = r.left, r.top, r.right, r.bottom
        if self.shown:
            t = window_rect(self.hwnd)
            L, T, R, B = min(L, t.left), min(T, t.top), max(R, t.right), max(B, t.bottom)
        pad = px(6)
        return L - pad <= p[0] <= R + pad and T - pad <= p[1] <= B + pad

    def place(self):
        x, y, w, h = self.app.overlay.rect
        tw, th = self.win.winfo_reqwidth(), self.win.winfo_reqheight()
        wa = work_area(self.app.overlay.hwnd)
        tx = min(max(x + (w - tw) // 2, wa.left + px(8)), wa.right - tw - px(8))
        ty = y + h + px(10)
        if ty + th > wa.bottom:
            ty = y - th - px(10)
        if (self.win.winfo_x(), self.win.winfo_y()) != (tx, ty):
            self.win.geometry(f"+{tx}+{ty}")

    def tick(self):
        app, now, want = self.app, time.time(), False
        if app.overlay.visible:
            mode = app.settings["toolbar"]
            if mode == "always" or not app.settings["locked"] or now < self.peek_until:
                want = True
            elif mode == "hover":
                if self._hot():
                    self.cold_since = None
                    self.hot_since = self.hot_since or now
                    want = self.target > 0 or now - self.hot_since > 0.35
                else:
                    self.hot_since = None
                    self.cold_since = self.cold_since or now
                    want = self.target > 0 and now - self.cold_since < 0.9
        self.target = 240 if want else 0
        if want and not self.shown:
            self.place()
            self.win.deiconify()
            self.shown = True
            keep_on_top(self.hwnd)
        if self.shown:
            self.place()
        if self.alpha != self.target:
            step = 45 if self.target > self.alpha else -30
            self.alpha = max(0, min(240, self.alpha + step))
            if (step > 0 and self.alpha > self.target) or (step < 0 and self.alpha < self.target):
                self.alpha = self.target
            set_alpha(self.hwnd, self.alpha)
        if self.shown and self.alpha == 0 and self.target == 0:
            self.win.withdraw()
            self.shown = False


# --------------------------------------------------------------------------
# Toast: brief click-through feedback ("Opacity 70%")
# --------------------------------------------------------------------------
class Toast:
    def __init__(self, app):
        self.app = app
        self.win, self.hwnd = popup(app.root, SURFACE_TOP)
        row = tk.Frame(self.win, bg=SURFACE_TOP, padx=px(16), pady=px(8))
        row.pack()
        self.icon = tk.Label(row, font=(ICON_FONT, 11), fg=PRIMARY, bg=SURFACE_TOP)
        self.text = tk.Label(row, font=(SEMIBOLD, 10), fg=TEXT, bg=SURFACE_TOP)
        self.icon.pack(side="left", padx=(0, px(10)))
        self.text.pack(side="left")
        set_exstyle(self.hwnd, add=WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
        round_corners(self.hwnd)
        set_alpha(self.hwnd, 0)
        self.alpha, self.until, self.shown = 0, 0.0, False

    def show(self, text, icon=None, seconds=1.4):
        self.icon.configure(text=icon or "")
        self.icon.pack_configure(padx=(0, px(10)) if icon else 0)
        self.text.configure(text=text)
        self.win.update_idletasks()
        tw = self.win.winfo_reqwidth()
        ov = self.app.overlay
        if ov.visible:
            x, y, w, h = ov.rect
            tx, ty = x + (w - tw) // 2, y + px(14)
        else:
            wa = work_area()
            tx, ty = wa.left + (wa.right - wa.left - tw) // 2, wa.top + px(48)
        self.win.geometry(f"+{tx}+{ty}")
        if not self.shown:
            self.win.deiconify()
            self.shown = True
        keep_on_top(self.hwnd)
        self.alpha = 240
        set_alpha(self.hwnd, self.alpha)
        self.until = time.time() + seconds

    def tick(self):
        if self.shown and time.time() > self.until:
            self.alpha -= 24
            if self.alpha <= 0:
                self.alpha, self.shown = 0, False
                self.win.withdraw()
            set_alpha(self.hwnd, self.alpha)


# --------------------------------------------------------------------------
# Crop editor: drag a box on a live snapshot of the source window
# --------------------------------------------------------------------------
class CropEditor(tk.Canvas):
    def __init__(self, parent, app, width, height, on_change):
        self.bgc = parent["bg"]
        self.W, self.Hh = px(width), px(height)
        super().__init__(parent, width=self.W, height=self.Hh, bg=self.bgc, highlightthickness=0,
                         bd=0, cursor="crosshair")
        self.app, self.on_change, self.mode = app, on_change, None
        self.bind("<ButtonPress-1>", self._press)
        self.bind("<B1-Motion>", self._motion)
        self.bind("<ButtonRelease-1>", self._release)
        self.bind("<Motion>", self._hover)
        self.load()

    def load(self):
        src = self.app.source
        img = capture(src) if src else None
        self.live = img is not None
        if img is None:
            sw, sh = client_size(src) if src else (16, 9)
            img = Image.new("RGB", (max(sw, 16), max(sh, 9)), SURFACE_HI)
        scale = min(self.W / img.width, self.Hh / img.height)
        self.iw, self.ih = max(1, int(img.width * scale)), max(1, int(img.height * scale))
        self.ox, self.oy = (self.W - self.iw) // 2, (self.Hh - self.ih) // 2
        self.full = img.resize((self.iw, self.ih), LANCZOS)
        self.dim = ImageTk.PhotoImage(ImageEnhance.Brightness(self.full).enhance(0.28))
        self.draw()

    def rect_px(self):
        l, t, w, h = self.app.settings["crop"]
        return (self.ox + l * self.iw, self.oy + t * self.ih,
                self.ox + (l + w) * self.iw, self.oy + (t + h) * self.ih)

    def draw(self):
        self.delete("all")
        self.create_image(self.ox, self.oy, image=self.dim, anchor="nw")
        x0, y0, x1, y1 = (int(v) for v in self.rect_px())
        if x1 - x0 > 1 and y1 - y0 > 1:
            crop = self.full.crop((x0 - self.ox, y0 - self.oy, x1 - self.ox, y1 - self.oy))
            self.bright = ImageTk.PhotoImage(crop)
            self.create_image(x0, y0, image=self.bright, anchor="nw")
        self.create_rectangle(x0, y0, x1, y1, outline=PRIMARY, width=px(2))
        hs, bar = px(4), px(9)
        for cx, cy in ((x0, y0), (x1, y0), (x0, y1), (x1, y1)):
            self.create_rectangle(cx - hs, cy - hs, cx + hs, cy + hs, fill=PRIMARY, outline="")
        # Mid-edge grips: drag one to trim just that side.
        mx, my = (x0 + x1) // 2, (y0 + y1) // 2
        for cx, cy, horiz in ((mx, y0, True), (mx, y1, True), (x0, my, False), (x1, my, False)):
            dx, dy = (bar, px(3)) if horiz else (px(3), bar)
            self.create_rectangle(cx - dx, cy - dy, cx + dx, cy + dy, fill=PRIMARY, outline="")
        if not self.live:
            self.create_text(self.W // 2, self.Hh // 2, fill=MUTED, font=(UI_FONT, 10),
                             text="No preview - is the window minimized?")

    def _corner(self, x, y):
        x0, y0, x1, y1 = self.rect_px()
        tol = px(12)
        for cx, cy, ox, oy in ((x0, y0, x1, y1), (x1, y0, x0, y1), (x0, y1, x1, y0), (x1, y1, x0, y0)):
            if abs(x - cx) <= tol and abs(y - cy) <= tol:
                return (cx, cy), (ox, oy)
        return None

    def _inside(self, x, y):
        x0, y0, x1, y1 = self.rect_px()
        return x0 < x < x1 and y0 < y < y1

    def _edge(self, x, y):
        """Which side ('l', 't', 'r', 'b') the point is grabbing, if any."""
        x0, y0, x1, y1 = self.rect_px()
        tol = px(8)
        if y0 - tol <= y <= y1 + tol:
            if abs(x - x0) <= tol:
                return "l"
            if abs(x - x1) <= tol:
                return "r"
        if x0 - tol <= x <= x1 + tol:
            if abs(y - y0) <= tol:
                return "t"
            if abs(y - y1) <= tol:
                return "b"
        return None

    def _hover(self, e):
        c = self._corner(e.x, e.y)
        edge = self._edge(e.x, e.y)
        if c:
            (cx, cy), (ox, oy) = c
            self.configure(cursor="size_nw_se" if (cx - ox) * (cy - oy) > 0 else "size_ne_sw")
        elif edge:
            self.configure(cursor="size_we" if edge in "lr" else "size_ns")
        else:
            self.configure(cursor="fleur" if self._inside(e.x, e.y) else "crosshair")

    def _clamp(self, x, y):
        return (min(max(x, self.ox), self.ox + self.iw), min(max(y, self.oy), self.oy + self.ih))

    def _press(self, e):
        c = self._corner(e.x, e.y)
        edge = self._edge(e.x, e.y)
        if c:
            self.mode = ("resize", c[1])
        elif edge:
            self.mode = ("edge", edge)
        elif self._inside(e.x, e.y):
            self.mode = ("move", (e.x, e.y, list(self.app.settings["crop"])))
        else:
            self.mode = ("resize", self._clamp(e.x, e.y))

    def _motion(self, e):
        if not self.mode:
            return
        kind, data = self.mode
        if kind == "move":
            sx, sy, (l, t, w, h) = data
            l = min(max(l + (e.x - sx) / self.iw, 0.0), 1.0 - w)
            t = min(max(t + (e.y - sy) / self.ih, 0.0), 1.0 - h)
        elif kind == "edge":
            # Trim one side; the opposite side stays put.
            l, t, w, h = self.app.settings["crop"]
            r, b = l + w, t + h
            nx = min(max((e.x - self.ox) / self.iw, 0.0), 1.0)
            ny = min(max((e.y - self.oy) / self.ih, 0.0), 1.0)
            if data == "l":
                l = min(nx, r - 0.05)
            elif data == "r":
                r = max(nx, l + 0.05)
            elif data == "t":
                t = min(ny, b - 0.05)
            else:
                b = max(ny, t + 0.05)
            w, h = r - l, b - t
        else:
            ax, ay = data
            bx, by = self._clamp(e.x, e.y)
            w = max(abs(bx - ax) / self.iw, 0.05)
            h = max(abs(by - ay) / self.ih, 0.05)
            l = min((min(ax, bx) - self.ox) / self.iw, 1.0 - w)
            t = min((min(ay, by) - self.oy) / self.ih, 1.0 - h)
        self.app.set_crop([round(l, 4), round(t, 4), round(w, 4), round(h, 4)])
        self.draw()
        self.on_change()

    def _release(self, e):
        self.mode = None
        self.app.mark_dirty()


# --------------------------------------------------------------------------
# Precision trim: pixel-exact cuts with magnified loupes on every edge
# --------------------------------------------------------------------------
class PrecisionTrim:
    SIDES = ("top", "bottom", "left", "right")
    ZOOMS = ((2, "2×"), (4, "4×"), (8, "8×"), (16, "16×"))
    LW, LH = 320, 140   # loupe size (logical px)
    MIN_PX = 16         # never trim the picture below this

    def __init__(self, app):
        self.app = app
        t = self.win = tk.Toplevel(app.root)
        t.title(f"{APP_NAME} precision trim")
        t.configure(bg=BG)
        t.resizable(False, False)
        t.attributes("-topmost", True)
        t.geometry("+-32000+-32000")
        t.protocol("WM_DELETE_WINDOW", self.close)
        self.zoom, self.active = 8, "top"
        self.undo_stack, self.redo_stack, self._last_push = [], [], 0.0
        self.views, self.canvases, self.titles, self.coords = {}, {}, {}, {}
        self.vars, self.entries, self.photos = {}, {}, {}
        self.snap, self.live, self.closing = None, False, False

        body = tk.Frame(t, bg=BG, padx=px(24), pady=px(20))
        body.pack()
        head = tk.Frame(body, bg=BG)
        head.pack(fill="x")
        tk.Label(head, text="Precision trim", font=(SEMIBOLD, 12), fg=TEXT, bg=BG).pack(side="left")
        self.result = tk.Label(head, font=(SEMIBOLD, 10), fg=PRIMARY, bg=BG)
        self.result.pack(side="right")
        tk.Label(body, text="Every pixel counts. Pick a side, then nudge it with the arrow keys (Shift = 10 px), "
                            "scroll on its loupe, click in the loupe to cut exactly there, or type the number.",
                 font=(UI_FONT, 9), fg=MUTED, bg=BG, justify="left",
                 wraplength=px(2 * self.LW + 24)).pack(anchor="w", pady=(px(2), 0))

        bar = tk.Frame(body, bg=BG)
        bar.pack(fill="x", pady=(px(14), px(12)))
        tk.Label(bar, text="Zoom", font=(UI_FONT, 10), fg=TEXT, bg=BG).pack(side="left", padx=(0, px(10)))
        self.zoom_sel = Segmented(bar, list(self.ZOOMS), self.zoom, self.set_zoom)
        self.zoom_sel.pack(side="left")
        Pill(bar, "", self.redo, kind="tonal", icon=IC["redo"], h=32).pack(side="right")
        Pill(bar, "", self.undo, kind="tonal", icon=IC["undo"], h=32).pack(side="right", padx=(0, px(6)))
        Pill(bar, "", self.refresh, kind="tonal", icon=IC["refresh"], h=32).pack(side="right", padx=(0, px(6)))

        grid = tk.Frame(body, bg=BG)
        grid.pack()
        for i, side in enumerate(self.SIDES):
            r, c = divmod(i, 2)
            card = tk.Frame(grid, bg=BG)
            card.grid(row=r, column=c, sticky="n", padx=(0, px(24)) if c == 0 else 0, pady=(0, px(16)))
            self._card(card, side)

        foot = tk.Frame(body, bg=BG)
        foot.pack(fill="x")
        Pill(foot, "Snap all to edges", self.snap_all, kind="tonal", icon=IC["target"], h=34).pack(side="left")
        Pill(foot, "Shave flat bars", self.shave_all, kind="ghost", icon=IC["crop"], h=34).pack(
            side="left", padx=(px(6), 0))
        Pill(foot, "Done", self.close, kind="primary", minw=100, h=34).pack(side="right")

        for d in ("Up", "Down", "Left", "Right"):
            t.bind(f"<{d}>", lambda e, d=d: self._arrow(e, d))
            t.bind(f"<Shift-{d}>", lambda e, d=d: self._arrow(e, d))
        t.bind("<Tab>", lambda e: self._cycle(1))
        t.bind("<Shift-Tab>", lambda e: self._cycle(-1))
        t.bind("<Control-z>", lambda e: self.undo())
        t.bind("<Control-y>", lambda e: self.redo())
        t.bind("<Control-Shift-Z>", lambda e: self.redo())
        t.bind("<Escape>", lambda e: self.close())

        style_titlebar(hwnd_of(t))
        t.withdraw()  # re-shown by position(), which forces a full fresh paint on-screen
        self.refresh()
        self.position()

    def _card(self, card, side):
        head = tk.Frame(card, bg=BG)
        head.pack(fill="x", pady=(0, px(6)))
        self.titles[side] = tk.Label(head, text=side.title(), font=(SEMIBOLD, 10), fg=TEXT, bg=BG)
        self.titles[side].pack(side="left")
        self.coords[side] = tk.Label(head, font=(UI_FONT, 9), fg=MUTED, bg=BG)
        self.coords[side].pack(side="left", padx=(px(10), 0))
        Pill(head, "Snap", lambda: self.snap_side(side), kind="ghost", icon=IC["target"], h=26, padx=10).pack(
            side="right")

        cv = tk.Canvas(card, width=px(self.LW), height=px(self.LH), bg="#000000", bd=0,
                       highlightthickness=px(2), highlightbackground=OUTLINE, cursor="crosshair")
        cv.pack()
        cv.bind("<ButtonPress-1>", lambda e: self._click(side, e))
        cv.bind("<B1-Motion>", lambda e: self._click(side, e))
        cv.bind("<MouseWheel>", lambda e: self._wheel(side, e))
        self.canvases[side] = cv

        ctl = tk.Frame(card, bg=BG)
        ctl.pack(fill="x", pady=(px(8), 0))
        for label, d in (("−10", -10), ("−1", -1)):
            Pill(ctl, label, lambda d=d: self.nudge(side, d), kind="tonal", h=30, padx=11).pack(
                side="left", padx=(0, px(4)))
        var = tk.StringVar()
        field = Field(ctl, var, 84, h=30)
        field.pack(side="left", padx=(px(2), 0))
        e = field.entry
        e.configure(justify="center")
        e.bind("<Return>", lambda ev: self._typed(side))
        e.bind("<KP_Enter>", lambda ev: self._typed(side))
        e.bind("<FocusOut>", lambda ev: self._typed(side))
        e.bind("<FocusIn>", lambda ev: self.set_active(side))
        e.bind("<Up>", lambda ev: (self.nudge(side, 1), "break")[1])
        e.bind("<Down>", lambda ev: (self.nudge(side, -1), "break")[1])
        self.vars[side], self.entries[side] = var, e
        tk.Label(ctl, text="px", font=(UI_FONT, 9), fg=MUTED, bg=BG).pack(side="left", padx=(px(6), px(8)))
        for label, d in (("+1", 1), ("+10", 10)):
            Pill(ctl, label, lambda d=d: self.nudge(side, d), kind="tonal", h=30, padx=11).pack(
                side="left", padx=(0, px(4)))

    # geometry -------------------------------------------------------------------
    def size(self):
        return self.snap.size

    def edges(self):
        sw, sh = self.size()
        l, t, w, h = self.app.settings["crop"]
        return [round(l * sw), round(t * sh), round((l + w) * sw), round((t + h) * sh)]

    def line_of(self, side, e=None):
        L, T, R, B = e or self.edges()
        return {"top": T, "bottom": B, "left": L, "right": R}[side]

    def amounts(self, e):
        sw, sh = self.size()
        L, T, R, B = e
        return {"top": T, "bottom": sh - B, "left": L, "right": sw - R}

    def set_side(self, side, line):
        """Put one cut at an exact pixel boundary; the other three stay put."""
        L, T, R, B = self.edges()
        sw, sh, m = *self.size(), self.MIN_PX
        if side == "top":
            T = max(0, min(line, B - m))
        elif side == "bottom":
            B = min(sh, max(line, T + m))
        elif side == "left":
            L = max(0, min(line, R - m))
        else:
            R = min(sw, max(line, L + m))
        self.commit([L, T, R, B])

    def commit(self, e):
        if e != self.edges():
            now = time.time()
            if now - self._last_push > 0.6:  # bursts of nudges undo as one step
                self.undo_stack.append(list(self.app.settings["crop"]))
                self.redo_stack.clear()
            self._last_push = now
            sw, sh = self.size()
            L, T, R, B = e
            self.app.set_crop([L / sw, T / sh, (R - L) / sw, (B - T) / sh], origin="precision")
            self.app.mark_dirty()
        self.redraw()

    def nudge(self, side, cut):
        """cut > 0 trims deeper into the picture, cut < 0 gives pixels back."""
        self.set_active(side)
        self.set_side(side, self.line_of(side) + (cut if side in ("top", "left") else -cut))

    def _typed(self, side):
        if self.closing:
            return
        try:
            amount = int(float(self.vars[side].get().strip() or 0))
        except ValueError:
            self.redraw()
            return
        sw, sh = self.size()
        line = {"top": amount, "bottom": sh - amount, "left": amount, "right": sw - amount}[side]
        self.set_side(side, line)

    # history --------------------------------------------------------------------
    def _swap(self, src, dst):
        if src:
            dst.append(list(self.app.settings["crop"]))
            self.app.set_crop(src.pop(), origin="precision")
            self.app.mark_dirty()
            self._last_push = 0.0
            self.redraw()

    def undo(self):
        self._swap(self.undo_stack, self.redo_stack)

    def redo(self):
        self._swap(self.redo_stack, self.undo_stack)

    # smart helpers ----------------------------------------------------------------
    def snap_side(self, side, quiet=False):
        if not self.live:
            return False
        e = self.edges()
        L, T, R, B = e
        span = (L, R) if side in ("top", "bottom") else (T, B)
        p = strongest_edge(self.snap, side, self.line_of(side, e), span)
        if p is None:
            if not quiet:
                self.app.toast.show(f"No clear edge near the {side} cut", IC["warn"])
            return False
        self.set_active(side)
        self.set_side(side, p)
        return True

    def snap_all(self):
        hits = sum(self.snap_side(s, quiet=True) for s in self.SIDES)
        self.app.toast.show(f"Snapped {hits} of 4 edges" if hits else "No clear edges nearby",
                            IC["target"] if hits else IC["warn"])

    def shave_all(self):
        if not self.live:
            return
        hits = 0
        for side in self.SIDES:
            line = shave_flat(self.snap, side, self.edges(), self.MIN_PX)
            if line is not None:
                self.set_side(side, line)
                hits += 1
        self.app.toast.show(f"Shaved {hits} flat edge{'s' if hits != 1 else ''}" if hits
                            else "No flat bars to shave", IC["crop"] if hits else IC["warn"])

    # view -----------------------------------------------------------------------------
    def refresh(self):
        src = self.app.source
        img = capture(src) if src else None
        self.live = img is not None
        if img is None:
            sw, sh = client_size(src) if src else (1600, 900)
            img = Image.new("RGB", (max(sw, 32), max(sh, 32)), SURFACE_HI)
            self.app.toast.show("No snapshot - is the window minimized?", IC["warn"], 2.0)
        self.snap = img
        self.redraw()

    def set_zoom(self, z):
        self.zoom = z
        self.zoom_sel.select(z)
        self.redraw()

    def set_active(self, side):
        self.active = side
        for s in self.SIDES:
            on = s == side
            self.titles[s].configure(fg=PRIMARY if on else TEXT, text=("●  " if on else "") + s.title())
            self.canvases[s].configure(highlightbackground=PRIMARY if on else OUTLINE)

    def _cycle(self, d):
        self.win.focus_set()
        self.set_active(self.SIDES[(self.SIDES.index(self.active) + d) % 4])
        return "break"

    def _arrow(self, e, direction):
        if isinstance(self.win.focus_get(), tk.Entry) and direction in ("Left", "Right"):
            return None  # let the caret move inside a number field
        step = 10 if e.state & 0x1 else 1
        side = self.active
        if side in ("top", "bottom") and direction in ("Up", "Down"):
            self.set_side(side, self.line_of(side) + (step if direction == "Down" else -step))
        elif side in ("left", "right") and direction in ("Left", "Right"):
            self.set_side(side, self.line_of(side) + (step if direction == "Right" else -step))
        return "break"

    def _click(self, side, e):
        self.win.focus_set()
        self.set_active(side)
        x0, y0 = self.views[side]
        hl, z = px(2), self.zoom
        if side in ("top", "bottom"):
            self.set_side(side, y0 + round((e.y - hl) / z))
        else:
            self.set_side(side, x0 + round((e.x - hl) / z))

    def _wheel(self, side, e):
        self.set_active(side)
        self.set_side(side, self.line_of(side) + (-1 if e.delta > 0 else 1))
        return "break"

    def _render(self, side, e):
        z, W, Hh, hl = self.zoom, px(self.LW), px(self.LH), px(2)
        L, T, R, B = e
        nx, ny = -(-W // z), -(-Hh // z)
        line = self.line_of(side, e)
        if side in ("top", "bottom"):
            x0, y0 = (L + R) // 2 - nx // 2, line - ny // 2
        else:
            x0, y0 = line - nx // 2, (T + B) // 2 - ny // 2
        self.views[side] = (x0, y0)
        region = self.snap.crop((x0, y0, x0 + nx, y0 + ny))  # black beyond the window
        # Dim whatever is trimmed away so the kept picture pops.
        out = ImageEnhance.Brightness(region).enhance(0.28)
        kx0, ky0 = max(0, L - x0), max(0, T - y0)
        kx1, ky1 = min(nx, R - x0), min(ny, B - y0)
        if kx1 > kx0 and ky1 > ky0:
            out.paste(region.crop((kx0, ky0, kx1, ky1)), (kx0, ky0))
        big = out.resize((nx * z, ny * z), Image.Resampling.NEAREST)
        if z >= 8:  # pixel grid
            grid = Image.new("RGBA", big.size, (0, 0, 0, 0))
            d = ImageDraw.Draw(grid)
            for gx in range(0, big.width, z):
                d.line([(gx, 0), (gx, big.height)], fill=(255, 255, 255, 26))
            for gy in range(0, big.height, z):
                d.line([(0, gy), (big.width, gy)], fill=(255, 255, 255, 26))
            big = Image.alpha_composite(big.convert("RGBA"), grid).convert("RGB")
        self.photos[side] = ImageTk.PhotoImage(big)
        cv = self.canvases[side]
        cv.delete("all")
        cv.create_image(hl, hl, image=self.photos[side], anchor="nw")
        lw = max(1, px(2))
        if side in ("top", "bottom"):
            ly = hl + (line - y0) * z
            cv.create_line(hl, ly, hl + W, ly, fill=PRIMARY, width=lw)
        else:
            lx = hl + (line - x0) * z
            cv.create_line(lx, hl, lx, hl + Hh, fill=PRIMARY, width=lw)

    def redraw(self):
        if not self.snap:
            return
        e = self.edges()
        L, T, R, B = e
        am = self.amounts(e)
        for side in self.SIDES:
            self._render(side, e)
            coord = {"top": f"y = {T}", "bottom": f"y = {B}", "left": f"x = {L}", "right": f"x = {R}"}[side]
            self.coords[side].configure(text=coord)
            if self.win.focus_get() is not self.entries[side]:
                self.vars[side].set(str(am[side]))
        self.set_active(self.active)
        w, h = R - L, B - T
        sw, sh = self.size()
        self.result.configure(text=f"{w} × {h} px  ·  {ratio_label(w, h)}   (window {sw} × {sh})")

    def sync(self):
        self.redraw()

    def position(self):
        t = self.win
        t.update_idletasks()
        w, h = t.winfo_reqwidth(), t.winfo_reqheight()
        ov = self.app.overlay
        wa = work_area(ov.hwnd if ov.visible else None)
        t.geometry(f"+{max(wa.left, (wa.left + wa.right - w) // 2)}+{max(wa.top, (wa.top + wa.bottom - h) // 2)}")
        t.deiconify()
        t.lift()
        t.focus_force()

    def close(self):
        self.closing = True
        self.win.destroy()
        self.app.precision = None


# --------------------------------------------------------------------------
# Settings sheet
# --------------------------------------------------------------------------
class Settings:
    def __init__(self, app):
        self.app = app
        t = self.win = tk.Toplevel(app.root)
        t.title(f"{APP_NAME} settings")
        t.configure(bg=BG)
        t.resizable(False, False)
        t.attributes("-topmost", True)
        t.geometry("+-32000+-32000")
        t.protocol("WM_DELETE_WINDOW", self.close)
        t.bind("<Escape>", lambda e: self.close())
        s = app.settings

        body = tk.Frame(t, bg=BG, padx=px(26), pady=px(22))
        body.pack()
        left = tk.Frame(body, bg=BG)
        left.grid(row=0, column=0, sticky="n")
        tk.Frame(body, bg=OUTLINE, width=max(1, px(1))).grid(row=0, column=1, sticky="ns", padx=px(26))
        right = tk.Frame(body, bg=BG)
        right.grid(row=0, column=2, sticky="n")

        # Crop ---------------------------------------------------------------
        top = tk.Frame(left, bg=BG)
        top.pack(fill="x")
        tk.Label(top, text="Crop", font=(SEMIBOLD, 12), fg=TEXT, bg=BG).pack(side="left")
        self.crop_info = tk.Label(top, font=(UI_FONT, 9), fg=MUTED, bg=BG)
        self.crop_info.pack(side="right")
        tk.Label(left, text="Trim the fat: drag an edge to cut that side, a corner to resize, or inside "
                            "to move. Find video does it for you while the video is playing.",
                 font=(UI_FONT, 9), fg=MUTED, bg=BG, justify="left", wraplength=px(448)).pack(
            anchor="w", pady=(px(2), 0))
        self.editor = CropEditor(left, app, 448, 252, on_change=self.crop_changed)
        self.editor.pack(pady=(px(14), px(10)))

        # Trim each side independently: top down, bottom up, left in, right in.
        grid = tk.Frame(left, bg=BG)
        grid.pack(fill="x")
        self.trims, self.trim_vals = {}, {}
        for i, side in enumerate(("top", "bottom", "left", "right")):
            r, c = divmod(i, 2)
            cell = tk.Frame(grid, bg=BG)
            cell.grid(row=r, column=c, sticky="w", padx=(0, px(18) if c == 0 else 0), pady=(0, px(2)))
            tk.Label(cell, text=side.title(), font=(UI_FONT, 10), fg=TEXT, bg=BG, width=6,
                     anchor="w").pack(side="left")
            self.trims[side] = Slider(cell, 0, 95, 0, lambda v, s=side: self.set_trim(s, v),
                                      width=118, step=0.5)
            self.trims[side].pack(side="left")
            self.trim_vals[side] = tk.Label(cell, font=(SEMIBOLD, 9), fg=PRIMARY, bg=BG, width=5, anchor="e")
            self.trim_vals[side].pack(side="left")

        row = tk.Frame(left, bg=BG)
        row.pack(fill="x", pady=(px(10), 0))
        Pill(row, "Find video", self.find_video, kind="tonal", icon=IC["crop"], h=34).pack(side="left")
        Pill(row, "Precision", app.open_precision, kind="tonal", icon=IC["zoom"], h=34).pack(
            side="left", padx=(px(6), 0))
        Pill(row, "Reset", self.reset_crop, kind="ghost", icon=IC["undo"], h=34).pack(
            side="left", padx=(px(6), 0))
        Pill(row, "", self.editor.load, kind="ghost", icon=IC["refresh"], h=34).pack(side="left")
        self.crop_changed()

        # Look & sound ---------------------------------------------------------
        heading(right, "Look & sound")

        def label(parent, text, value=None):
            r = tk.Frame(parent, bg=BG)
            r.pack(fill="x", pady=(px(16), px(4)))
            tk.Label(r, text=text, font=(UI_FONT, 10), fg=TEXT, bg=BG).pack(side="left")
            if value is not None:
                v = tk.Label(r, text=value, font=(SEMIBOLD, 10), fg=PRIMARY, bg=BG)
                v.pack(side="right")
                return v

        self.opacity_val = label(right, "Opacity", f"{round(s['opacity'] * 100)}%")
        self.opacity = Slider(right, 10, 100, round(s["opacity"] * 100),
                              lambda v: app.set_opacity(v / 100, toast=False), width=300)
        self.opacity.pack(anchor="w")

        self.scale_val = label(right, "Size", "")
        self.scale = Slider(right, 10, 300, round((s["scale"] or 1) * 100),
                            lambda v: app.set_scale(v / 100, toast=False), width=300)
        self.scale.pack(anchor="w")
        quick = tk.Frame(right, bg=BG)
        quick.pack(anchor="w", pady=(px(4), 0))
        for pct in (50, 75, 100, 150):
            Pill(quick, f"{pct}%", lambda p=pct: app.set_scale(p / 100, toast=False), kind="tonal",
                 h=28, padx=12).pack(side="left", padx=(0, px(6)))
        tk.Label(right, text="100% = the video's real pixel size. Grows from the corner it's parked in.",
                 font=(UI_FONT, 9), fg=MUTED, bg=BG, justify="left", wraplength=px(300)).pack(
            anchor="w", pady=(px(6), 0))
        self.sync_scale()

        label(right, "Toolbar")
        self.mode = Segmented(right, list(TOOLBAR_MODES.items()), s["toolbar"], app.set_toolbar_mode)
        self.mode.pack(anchor="w")

        label(right, "System volume")
        vol = tk.Frame(right, bg=BG)
        vol.pack(anchor="w")
        for icon, cmd in ((IC["vol_down"], "vol-"), (IC["mute"], "mute"), (IC["vol_up"], "vol+")):
            Pill(vol, "", lambda c=cmd: app.media(c), kind="tonal", icon=icon, h=36).pack(
                side="left", padx=(0, px(6)))

        label(right, "Tab lock")
        self.keyword = tk.StringVar(value=s["match"])
        self.keyword.trace_add("write", lambda *a: app.set_tab_match(self.keyword.get()))
        Field(right, self.keyword, 300, placeholder="Off", h=38).pack(anchor="w")
        tk.Label(right, text="Hide the overlay while the window's title doesn't contain this "
                             "(e.g. when you switch away from the YouTube tab).",
                 font=(UI_FONT, 9), fg=MUTED, bg=BG, justify="left", wraplength=px(300)).pack(
            anchor="w", pady=(px(6), 0))

        # Footer -----------------------------------------------------------------
        foot = tk.Frame(body, bg=BG)
        foot.grid(row=1, column=0, columnspan=3, sticky="ew", pady=(px(22), 0))
        Pill(foot, "Change window", app.show_home, kind="ghost", icon=IC["home"]).pack(side="left")
        Pill(foot, "Done", self.close, kind="primary", minw=110).pack(side="right")

        style_titlebar(hwnd_of(t))
        t.withdraw()  # re-shown by position(), which forces a full fresh paint on-screen
        self.position()

    def position(self):
        t = self.win
        t.update_idletasks()
        w, h = t.winfo_reqwidth(), t.winfo_reqheight()
        ov = self.app.overlay
        wa = work_area(ov.hwnd if ov.visible else None)
        if ov.visible:
            x, y, ow, oh = ov.rect
            nx = x + ow + px(16) if x + ow + px(16) + w < wa.right else x - w - px(16)
            ny = y
        else:
            nx, ny = (wa.left + wa.right - w) // 2, (wa.top + wa.bottom - h) // 2
        nx = min(max(nx, wa.left + px(8)), wa.right - w - px(8))
        ny = min(max(ny, wa.top + px(8)), wa.bottom - h - px(8))
        t.geometry(f"+{nx}+{ny}")
        t.deiconify()
        t.lift()
        t.focus_force()

    def crop_changed(self):
        l, t, w, h = self.app.settings["crop"]
        if (w, h) == (1.0, 1.0):
            self.crop_info.configure(text="Showing the whole window")
        else:
            self.crop_info.configure(text=f"Showing {round(w * 100)} × {round(h * 100)}%")
        sides = {"top": t, "bottom": 1 - t - h, "left": l, "right": 1 - l - w}
        for side, v in sides.items():
            pct = round(max(0.0, v) * 200) / 2
            self.trims[side].set(pct)
            self.trim_vals[side].configure(text=f"{pct:g}%")

    def set_trim(self, side, value):
        l, t, w, h = self.app.settings["crop"]
        r, b, v = 1 - l - w, 1 - t - h, value / 100
        # Always leave at least 5% of the window visible.
        if side == "top":
            t = min(v, 0.95 - b)
        elif side == "bottom":
            b = min(v, 0.95 - t)
        elif side == "left":
            l = min(v, 0.95 - r)
        else:
            r = min(v, 0.95 - l)
        self.app.set_crop([round(l, 4), round(t, 4), round(1 - l - r, 4), round(1 - t - b, 4)])
        self.app.mark_dirty()
        self.editor.draw()
        self.crop_changed()

    def find_video(self):
        """Grab a few frames, find what's moving, and crop to it."""
        src, frames = self.app.source, []
        if not src:
            return
        self.app.toast.show("Watching for the playing video...", IC["search"], 3.6)

        def grab():
            if not self.win.winfo_exists() or self.app.source != src:
                return
            f = capture(src)
            if f is None:
                self.app.toast.show("Can't see the window - is it minimized?", IC["warn"], 2.2)
                return
            frames.append(f)
            # ~3.5 s of frames, so a camera cut lights up the whole video frame
            # rather than just whoever happens to be moving.
            if len(frames) < 12:
                self.win.after(300, grab)
                return
            box = find_motion(frames)
            if not box:
                self.app.toast.show("No moving video found - is it playing?", IC["warn"], 2.2)
                return
            self.app.set_crop(box)
            self.app.mark_dirty()
            self.editor.load()
            self.crop_changed()
            self.app.toast.show("Trimmed to the video - run again if it's too tight", IC["crop"], 2.2)

        grab()

    def reset_crop(self):
        self.app.set_crop([0.0, 0.0, 1.0, 1.0])
        self.app.mark_dirty()
        self.editor.draw()
        self.crop_changed()

    def sync(self):
        s = self.app.settings
        self.opacity.set(round(s["opacity"] * 100))
        self.opacity_val.configure(text=f"{round(s['opacity'] * 100)}%")
        self.mode.select(s["toolbar"])
        self.sync_scale()

    def sync_scale(self):
        scale = self.app.settings["scale"] or 1.0
        _, _, w, h = self.app.overlay.rect
        self.scale.set(round(scale * 100))
        self.scale_val.configure(text=f"{round(scale * 100)}%   {w} × {h}" if w else f"{round(scale * 100)}%")

    def close(self):
        self.win.destroy()
        self.app.sheet = None


# --------------------------------------------------------------------------
# Home screen
# --------------------------------------------------------------------------
class Home:
    WIDTH = 540

    def __init__(self, app):
        self.app = app
        r = self.win = app.root
        r.title(APP_NAME)
        r.configure(bg=BG)
        r.resizable(False, False)
        r.geometry("+-32000+-32000")
        r.protocol("WM_DELETE_WINDOW", self.close)
        self.icon_img = ImageTk.PhotoImage(make_icon(64))
        r.iconphoto(True, self.icon_img)
        if os.path.exists(ICON_PATH):
            try:
                r.iconbitmap(default=ICON_PATH)
            except tk.TclError:
                pass
        s, W = app.settings, self.WIDTH

        body = tk.Frame(r, bg=BG, padx=px(26), pady=px(22))
        body.pack()

        head = tk.Frame(body, bg=BG)
        head.pack(fill="x")
        self.logo = ImageTk.PhotoImage(make_icon(px(46)))
        tk.Label(head, image=self.logo, bg=BG).pack(side="left")
        titles = tk.Frame(head, bg=BG)
        titles.pack(side="left", padx=(px(14), 0))
        tk.Label(titles, text=APP_NAME, font=(DISPLAY_FONT, 17), fg=TEXT, bg=BG).pack(anchor="w")
        tk.Label(titles, text="Click-through picture-in-picture for any window", font=(UI_FONT, 9),
                 fg=MUTED, bg=BG).pack(anchor="w")
        self.status = tk.Frame(head, bg=BG)
        self.status.pack(side="right")

        self.query = tk.StringVar()
        self.query.trace_add("write", lambda *a: self.refresh(keep=False))
        self.search = Field(body, self.query, W, icon=IC["search"], placeholder="Search open windows")
        self.search.pack(pady=(px(20), px(12)))

        self.list = WindowList(body, W, 296, self.on_select, self.start)
        self.list.pack()

        self.banner = tk.Frame(body, bg=BG)
        self.banner.pack(fill="x")

        opts = tk.Frame(body, bg=BG)
        opts.pack(fill="x", pady=(px(16), 0))
        opts.columnconfigure(1, weight=1)
        tk.Label(opts, text="Opacity", font=(UI_FONT, 10), fg=TEXT, bg=BG).grid(row=0, column=0, sticky="w")
        self.opacity = Slider(opts, 10, 100, round(s["opacity"] * 100), self._opacity, width=300)
        self.opacity.grid(row=0, column=1, sticky="w", padx=px(16))
        self.opacity_val = tk.Label(opts, text="", font=(SEMIBOLD, 10), fg=PRIMARY, bg=BG, width=5, anchor="e")
        self.opacity_val.grid(row=0, column=2, sticky="e")
        tk.Label(opts, text="Tab lock", font=(UI_FONT, 10), fg=TEXT, bg=BG).grid(
            row=1, column=0, sticky="w", pady=(px(12), 0))
        self.keyword = tk.StringVar(value=s["match"])
        self.keyword.trace_add("write", lambda *a: app.set_tab_match(self.keyword.get()))
        Field(opts, self.keyword, 300, placeholder="Off", h=36).grid(
            row=1, column=1, sticky="w", padx=px(16), pady=(px(12), 0))
        self._opacity(self.opacity.value)

        act = tk.Frame(body, bg=BG)
        act.pack(fill="x", pady=(px(20), 0))
        Pill(act, "Refresh", lambda: self.refresh(), kind="ghost", icon=IC["refresh"]).pack(side="left")
        self.go = Pill(act, "Start mirroring", self.start, kind="primary", icon=IC["play"], minw=190, h=40)
        self.go.pack(side="right")

        tk.Frame(body, bg=OUTLINE, height=max(1, px(1))).pack(fill="x", pady=(px(20), px(12)))
        rows = [
            [("↑↓", "opacity"), ("+−", "size"), ("←→", "seek"), ("Space", "play"), ("L", "move")],
            [("C", "settings"), ("P", "precision trim"), ("H", "home"), ("Q", "quit")],
        ]
        for i, keys in enumerate(rows):
            hint = tk.Frame(body, bg=BG)
            hint.pack(fill="x", pady=(0, px(2)))
            tk.Label(hint, text="Ctrl+Alt +", font=(UI_FONT, 9), fg=MUTED if i == 0 else BG,
                     bg=BG).pack(side="left")
            for k, what in keys:
                tk.Label(hint, text=k, font=(SEMIBOLD, 9), fg=TEXT, bg=BG).pack(side="left", padx=(px(8), px(3)))
                tk.Label(hint, text=what, font=(UI_FONT, 9), fg=MUTED, bg=BG).pack(side="left")
        tk.Label(body, text="Gaming? Use borderless / windowed fullscreen so the overlay can stay on top.",
                 font=(UI_FONT, 9), fg=MUTED, bg=BG).pack(anchor="w", pady=(px(6), 0))

        for seq, fn in (("<Down>", lambda e: self.list.move(1)), ("<Up>", lambda e: self.list.move(-1)),
                        ("<Return>", lambda e: self.start()), ("<Escape>", lambda e: self.close())):
            r.bind(seq, fn)
        r.bind("<MouseWheel>", lambda e: self.list._wheel(e))

        r.update_idletasks()
        self.hwnd = hwnd_of(r)
        style_titlebar(self.hwnd)
        r.withdraw()
        self.update_status()

    def _opacity(self, v):
        self.opacity_val.configure(text=f"{int(v)}%")
        if self.app.settings["opacity"] != v / 100:
            self.app.set_opacity(v / 100, toast=False)

    def show(self):
        r = self.win
        s = self.app.settings
        self.opacity.set(round(s["opacity"] * 100))
        self.opacity_val.configure(text=f"{round(s['opacity'] * 100)}%")
        if self.keyword.get() != s["match"]:
            self.keyword.set(s["match"])
        self.refresh(keep=False)
        self.update_status()
        r.update_idletasks()
        wa = work_area()
        w, h = r.winfo_reqwidth(), r.winfo_reqheight()
        r.geometry(f"+{(wa.left + wa.right - w) // 2}+{max(wa.top, (wa.top + wa.bottom - h) // 2)}")
        r.deiconify()
        r.lift()
        r.attributes("-topmost", True)
        r.after(200, lambda: r.attributes("-topmost", False))
        r.focus_force()
        self.search.entry.focus_set()

    def close(self):
        if self.app.source:
            self.win.withdraw()
            self.app.toast.show("Still mirroring - Ctrl+Alt+H brings this back", IC["pip"], 2.5)
        else:
            self.app.quit()

    def refresh(self, keep=True):
        prev = self.list.selected()
        q = self.query.get().strip().lower()
        wins = [w for w in list_windows() if q in w.title.lower() or q in w.app.lower()]
        pick = 0
        kw = self.app.settings["match"].strip().lower()
        hw = [w.hwnd for w in wins]
        if keep and prev and prev.hwnd in hw:
            pick = hw.index(prev.hwnd)
        elif self.app.source in hw:
            pick = hw.index(self.app.source)
        elif kw:
            pick = next((i for i, w in enumerate(wins) if kw in w.title.lower()), 0)
        self.list.set_items(wins, pick)

    def on_select(self, win):
        if win and self.app.source == win.hwnd:
            self.go.set_text("Restart mirror", IC["refresh"])
        elif self.app.source:
            self.go.set_text("Switch to this", IC["pip"])
        else:
            self.go.set_text("Start mirroring", IC["play"])
        self.update_banner(win)

    def update_banner(self, win):
        for c in self.banner.winfo_children():
            c.destroy()
        msg = action = None
        exe = os.path.basename(win.exe).lower() if win else ""
        if exe == "firefox.exe":
            st = firefox_status(window_pid(win.hwnd))
            if st == "fix":
                msg = "Firefox stops drawing when it's fully covered, so the mirror freezes behind games."
                action = ("Fix it", self._fix_firefox)
            elif st == "restart":
                msg = "Restart Firefox once to finish the fix. Until then the mirror freezes while it's covered."
        elif exe in CHROMIUM:
            msg = (f"{win.app} may pause when it's fully covered. Keep a sliver of it visible "
                   "if the mirror freezes.")
        if not msg:
            return
        box = tk.Frame(self.banner, bg=BG)
        box.pack(fill="x", pady=(px(12), 0))
        tk.Frame(box, bg=WARN, width=px(3)).pack(side="left", fill="y")
        tk.Label(box, text=IC["warn"], font=(ICON_FONT, 12), fg=WARN, bg=BG).pack(side="left", padx=(px(12), px(10)))
        if action:
            Pill(box, action[0], action[1], kind="tonal", h=32).pack(side="right")
        tk.Label(box, text=msg, font=(UI_FONT, 9), fg=TEXT, bg=BG, justify="left",
                 wraplength=px(self.WIDTH - (190 if action else 60))).pack(side="left", fill="x")

    def _fix_firefox(self):
        if firefox_fix():
            self.app.toast.show("Firefox fixed - restart it to apply", IC["check"], 2.5)
        self.update_banner(self.list.selected())

    def update_status(self):
        for c in self.status.winfo_children():
            c.destroy()
        if self.app.source:
            Pill(self.status, "Stop", self.app.stop, kind="tonal", h=30).pack(side="right")
            tk.Label(self.status, text="●  Live", font=(SEMIBOLD, 10), fg=OK, bg=BG).pack(
                side="right", padx=(0, px(10)))
        else:
            tk.Label(self.status, text="○  Idle", font=(SEMIBOLD, 10), fg=MUTED, bg=BG).pack(side="right")

    def start(self):
        win = self.list.selected()
        if win:
            self.app.start(win.hwnd)


# --------------------------------------------------------------------------
# Hotkeys and single-instance signalling
# --------------------------------------------------------------------------
HOTKEYS = [  # (vk, action, auto-repeat, label)
    (VK_UP, "opacity+", True, "Up"), (VK_DOWN, "opacity-", True, "Down"),
    (VK_LEFT, "back", True, "Left"), (VK_RIGHT, "fwd", True, "Right"),
    (VK_SPACE, "play", False, "Space"), (ord("L"), "lock", False, "L"),
    (ord("T"), "toolbar", False, "T"), (ord("C"), "settings", False, "C"),
    (ord("P"), "precision", False, "P"),
    (VK_OEM_PLUS, "bigger", True, "="), (VK_OEM_MINUS, "smaller", True, "-"),
    (VK_ADD, "bigger", True, "Num +"), (VK_SUBTRACT, "smaller", True, "Num -"),
    (ord("H"), "home", False, "H"), (ord("W"), "home", False, "W"), (ord("Q"), "quit", False, "Q"),
]


class HotkeyThread(threading.Thread):
    def __init__(self, q):
        super().__init__(daemon=True)
        self.q = q

    def run(self):
        taken = []
        for i, (vk, _, repeat, label) in enumerate(HOTKEYS, 1):
            mods = MOD_CONTROL | MOD_ALT | (0 if repeat else MOD_NOREPEAT)
            if not user32.RegisterHotKey(None, i, mods, vk):
                taken.append(f"Ctrl+Alt+{label}")
        if taken:
            self.q.put(("warn", "In use by another app: " + ", ".join(taken)))
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == WM_HOTKEY and 1 <= msg.wParam <= len(HOTKEYS):
                self.q.put(("key", HOTKEYS[msg.wParam - 1][1]))


def watch_show_event(q, event):
    while kernel32.WaitForSingleObject(event, 0xFFFFFFFF) == 0:
        q.put(("home", None))


# --------------------------------------------------------------------------
# App
# --------------------------------------------------------------------------
class App:
    def __init__(self, argv, show_event=None):
        self.root = tk.Tk()
        init_theme(self.root)
        self.settings = load_settings()
        self.q = queue.Queue()
        self.source = None
        self.tab_match = ""
        self.hidden_for_tab = False
        self.playing = True
        self.sheet = None
        self.precision = None
        self.alive = True
        self.dirty = False
        self.n = 0
        if not os.path.exists(ICON_PATH):
            try:
                make_icon(256).save(ICON_PATH, sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
            except OSError:
                pass

        self.overlay = Overlay(self)
        self.toolbar = Toolbar(self)
        self.toast = Toast(self)
        self.home = Home(self)

        HotkeyThread(self.q).start()
        if show_event:
            threading.Thread(target=watch_show_event, args=(self.q, show_event), daemon=True).start()
        self.root.after(30, self.tick)

        target = find_window(argv[0]) if argv else None
        if target:
            self.start(target)
        else:
            self.root.after(10, self.show_home)

    # mirroring ------------------------------------------------------------------
    def start(self, hwnd):
        if not user32.IsWindow(hwnd):
            self.toast.show("That window is gone", IC["warn"])
            return
        if not self.overlay.attach(hwnd):
            self.toast.show("Couldn't mirror that window", IC["warn"])
            return
        self.source = hwnd
        self.settings["locked"] = True
        self._update_tab_match()
        self.hidden_for_tab = False
        self.home.win.withdraw()
        self.overlay.show()
        self.toolbar.sync()
        self.toolbar.peek(2.5)
        self.toast.show(f"Mirroring {app_name(process_path(window_pid(hwnd)))}", IC["pip"])
        self.home.update_status()
        self.mark_dirty()

    def stop(self):
        self.overlay.detach()
        self.overlay.hide()
        self.source = None
        if self.precision:
            self.precision.close()
        if self.sheet:
            self.sheet.close()
        self.home.update_status()
        self.home.on_select(self.home.list.selected())

    def _update_tab_match(self):
        kw = self.settings["match"].strip().lower()
        title = window_title(self.source).lower() if self.source else ""
        # Only lock if the keyword fits the chosen window; otherwise it would hide immediately.
        self.tab_match = kw if kw and kw in title else ""

    def _check_source(self):
        if not user32.IsWindow(self.source):
            self.stop()
            self.show_home()
            self.toast.show("The mirrored window closed", IC["warn"], 2.0)
            return
        if self.n % 8 or not self.tab_match:
            return
        if self.tab_match in window_title(self.source).lower():
            if self.hidden_for_tab:
                self.hidden_for_tab = False
                self.overlay.show()
            return
        other = find_window(self.tab_match, exclude=self.source)
        if other and self.overlay.attach(other):
            # The tab moved (e.g. dragged into its own window): follow it.
            self.source = other
            self.hidden_for_tab = False
            self.overlay.show()
            self.toast.show(f"Following {self.tab_match} to another window", IC["pip"])
        elif not self.hidden_for_tab:
            self.hidden_for_tab = True
            self.overlay.hide()

    # controls ---------------------------------------------------------------------
    def set_opacity(self, value, toast=True):
        value = round(max(0.1, min(1.0, value)), 2)
        self.settings["opacity"] = value
        if self.overlay.visible:
            set_alpha(self.overlay.hwnd, value * 255)
        if toast and self.overlay.visible:
            self.toast.show(f"Opacity {round(value * 100)}%", IC["opacity"], 0.9)
        if self.sheet:
            self.sheet.sync()
        self.mark_dirty()

    def set_scale(self, scale, toast=True):
        """Resize the PiP: 1.0 shows the cropped video at its real pixel size."""
        self.settings["scale"] = scale
        if self.source:
            self.overlay.layout()  # clamps to what fits on screen
        scale = self.settings["scale"]
        _, _, w, h = self.overlay.rect
        if toast and self.overlay.visible:
            self.toast.show(f"Size {round(scale * 100)}%  ·  {w} × {h}", IC["zoom"], 0.9)
        if self.sheet:
            self.sheet.sync_scale()
        self.mark_dirty()

    def step_scale(self, direction):
        cur = self.settings["scale"] or 1.0
        new = round(cur * (1.1 if direction > 0 else 1 / 1.1), 2)
        if new == cur:
            new = cur + (0.01 if direction > 0 else -0.01)
        self.set_scale(new)

    def set_crop(self, crop, origin="sheet"):
        self.settings["crop"] = crop
        if self.overlay.visible:
            self.overlay.layout()
            if self.sheet:
                self.sheet.sync_scale()  # trimming changes the window size too
        # Keep the settings sheet and the precision window showing the same cut.
        if origin == "precision" and self.sheet:
            self.sheet.editor.draw()
            self.sheet.crop_changed()
        elif origin != "precision" and self.precision:
            self.precision.sync()

    def open_precision(self):
        if not self.source:
            self.show_home()
            return
        if self.precision:
            self.precision.refresh()
            self.precision.position()
        else:
            self.precision = PrecisionTrim(self)

    def set_toolbar_mode(self, mode):
        self.settings["toolbar"] = mode
        if mode != "off":
            self.toolbar.peek(1.5)
        if self.sheet:
            self.sheet.sync()
        self.mark_dirty()

    def cycle_toolbar(self):
        order = list(TOOLBAR_MODES)
        mode = order[(order.index(self.settings["toolbar"]) + 1) % len(order)]
        self.set_toolbar_mode(mode)
        self.toast.show(f"Toolbar: {TOOLBAR_MODES[mode].lower()}", IC["settings"])

    def set_tab_match(self, keyword):
        self.settings["match"] = keyword.strip()
        if self.source:
            self._update_tab_match()
            if not self.tab_match and self.hidden_for_tab:
                self.hidden_for_tab = False
                self.overlay.show()
        self.mark_dirty()

    def toggle_lock(self):
        s = self.settings
        s["locked"] = not s["locked"]
        self.overlay.style()
        self.toolbar.sync()
        if s["locked"]:
            self.toast.show("Locked - clicks pass through", IC["lock"])
        else:
            self.toast.show("Unlocked - drag to move, scroll to resize", IC["move"], 2.2)
            self.toolbar.peek(2.5)

    def media(self, cmd):
        if cmd == "play":
            release_modifiers()
            tap(VK_MEDIA_PLAY_PAUSE)
            self.playing = not self.playing
            self.toolbar.sync()
            self.toast.show("Playing" if self.playing else "Paused",
                            IC["play"] if self.playing else IC["pause"], 0.9)
        elif cmd in ("back", "fwd") and self.source:
            youtube = "youtube" in window_title(self.source).lower()
            if youtube:  # YouTube: J / L skip 10 seconds
                key = ord("J") if cmd == "back" else ord("L")
            else:        # most other players seek with the arrow keys
                key = VK_LEFT if cmd == "back" else VK_RIGHT
            send_to_window(self.source, key)
            label = ("-10 s" if cmd == "back" else "+10 s") if youtube else ("Back" if cmd == "back" else "Forward")
            self.toast.show(label, IC["rew"] if cmd == "back" else IC["ff"], 0.8)
        elif cmd == "prev":
            tap(VK_MEDIA_PREV)
            self.toast.show("Previous", IC["prev"], 0.9)
        elif cmd == "next":
            tap(VK_MEDIA_NEXT)
            self.toast.show("Next", IC["next"], 0.9)
        elif cmd == "vol-":
            tap(VK_VOLUME_DOWN)
        elif cmd == "vol+":
            tap(VK_VOLUME_UP)
        elif cmd == "mute":
            tap(VK_VOLUME_MUTE)

    def open_settings(self):
        if not self.source:
            self.show_home()
            return
        if self.sheet:
            self.sheet.editor.load()
            self.sheet.position()
        else:
            self.sheet = Settings(self)

    def show_home(self):
        self.home.show()

    def mark_dirty(self):
        self.dirty = True

    # loop -------------------------------------------------------------------------
    def handle(self, kind, arg):
        if kind == "home":
            self.show_home()
        elif kind == "warn":
            self.toast.show(arg, IC["warn"], 3.5)
        elif kind == "key":
            if arg == "quit":
                self.quit()
            elif arg == "home":
                self.show_home()
            elif self.source:
                {
                    "opacity+": lambda: self.set_opacity(self.settings["opacity"] + 0.1),
                    "opacity-": lambda: self.set_opacity(self.settings["opacity"] - 0.1),
                    "back": lambda: self.media("back"),
                    "fwd": lambda: self.media("fwd"),
                    "play": lambda: self.media("play"),
                    "lock": self.toggle_lock,
                    "toolbar": self.cycle_toolbar,
                    "settings": self.open_settings,
                    "precision": self.open_precision,
                    "bigger": lambda: self.step_scale(1),
                    "smaller": lambda: self.step_scale(-1),
                }[arg]()

    def tick(self):
        if not self.alive:
            return
        self.n += 1
        while not self.q.empty():
            self.handle(*self.q.get())
            if not self.alive:
                return
        if self.source:
            self._check_source()
        if self.source and self.overlay.visible and self.n % 16 == 0:
            # Re-assert always-on-top so games and other topmost apps can't bury us,
            # and follow the source if it was resized.
            keep_on_top(self.overlay.hwnd)
            if self.toolbar.shown:
                keep_on_top(self.toolbar.hwnd)
            if client_size(self.source) != self.overlay.src_size:
                self.overlay.layout()
            else:
                self.overlay.update_thumb()
        self.toolbar.tick()
        self.toast.tick()
        if self.dirty and self.n % 50 == 0:
            self.dirty = False
            save_settings(self.settings)
        self.root.after(30, self.tick)

    def quit(self):
        if not self.alive:
            return
        self.alive = False
        save_settings(self.settings)
        self.overlay.detach()
        self.root.destroy()

    def run(self):
        self.root.mainloop()


def main():
    if Image is None:
        user32.MessageBoxW(None, "Ghost PiP needs Pillow.\n\nInstall it with:  pip install pillow",
                           APP_NAME, 0x10)
        return
    mutex = kernel32.CreateMutexW(None, False, MUTEX_NAME)  # held for the app's lifetime
    if ctypes.get_last_error() == ERROR_ALREADY_EXISTS:
        # Already running: ask that copy to show its home screen instead.
        ev = kernel32.OpenEventW(EVENT_MODIFY_STATE, False, SHOW_EVENT_NAME)
        if ev:
            kernel32.SetEvent(ev)
            kernel32.CloseHandle(ev)
        return
    show_event = kernel32.CreateEventW(None, False, False, SHOW_EVENT_NAME)
    App(sys.argv[1:], show_event).run()
    kernel32.CloseHandle(mutex)


if __name__ == "__main__":
    main()
