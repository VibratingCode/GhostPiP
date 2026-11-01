using System.Diagnostics;
using System.IO;
using static GhostPiP.Core.Native;

namespace GhostPiP.Core;

/// <summary>A window you could pick from the gallery.</summary>
public sealed record WindowInfo(nint Hwnd, string Title, string App, string ExePath, uint Pid)
{
    public string ExeName => Path.GetFileName(ExePath).ToLowerInvariant();
    public bool IsMinimized => IsIconic(Hwnd);
}

public static class WindowScanner
{
    static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["firefox"] = "Firefox", ["chrome"] = "Chrome", ["msedge"] = "Edge", ["brave"] = "Brave",
        ["opera"] = "Opera", ["vivaldi"] = "Vivaldi", ["discord"] = "Discord", ["vlc"] = "VLC",
        ["obs64"] = "OBS", ["spotify"] = "Spotify", ["explorer"] = "File Explorer", ["code"] = "VS Code",
        ["mpv"] = "mpv", ["steamwebhelper"] = "Steam", ["applicationframehost"] = "App",
    };

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox.exe", "chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "vivaldi.exe",
    };

    public static readonly HashSet<string> Chromium = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "vivaldi.exe",
    };

    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    public static string AppName(string exePath)
    {
        string stem = Path.GetFileNameWithoutExtension(exePath);
        if (stem.Length == 0)
            return "App";
        if (KnownNames.TryGetValue(stem, out var known))
            return known;
        if (NameCache.TryGetValue(exePath, out var cached))
            return cached;
        string name = stem[..1].ToUpperInvariant() + stem[1..];
        try
        {
            var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description) && description.Length <= 32)
                name = description;
        }
        catch
        {
            // No version info: the file name will do.
        }
        return NameCache[exePath] = name;
    }

    /// <summary>Is this a window you'd see in Alt+Tab?</summary>
    static bool IsCandidate(nint hwnd)
    {
        if (!IsWindowVisible(hwnd))
            return false;
        long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow)
            return false;
        if (GetWindow(hwnd, GW_OWNER) != 0 && !appWindow)
            return false;
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4);
        return cloaked == 0;
    }

    /// <summary>Top-level windows, most recently used first.</summary>
    public static List<WindowInfo> List()
    {
        var found = new List<WindowInfo>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd))
            {
                string title = Win.Title(hwnd);
                uint pid = Win.Pid(hwnd);
                if (title.Length > 0 && title != "Program Manager" && pid != me)
                {
                    string exe = Win.ProcessPath(pid);
                    found.Add(new WindowInfo(hwnd, title, FriendlyApp(exe, title), exe, pid));
                }
            }
            return true;
        }, 0);
        return found;
    }

    /// <summary>First visible window whose title contains the keyword (case-insensitive).</summary>
    public static nint FindByTitle(string keyword, nint exclude = 0)
    {
        nint match = 0;
        uint me = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd != exclude && IsCandidate(hwnd) && Win.Pid(hwnd) != me &&
                Win.Title(hwnd).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                match = hwnd;
                return false;
            }
            return true;
        }, 0);
        return match;
    }

    public static WindowInfo? Describe(nint hwnd)
    {
        if (!IsWindow(hwnd))
            return null;
        uint pid = Win.Pid(hwnd);
        string exe = Win.ProcessPath(pid);
        string title = Win.Title(hwnd);
        return new WindowInfo(hwnd, title, FriendlyApp(exe, title), exe, pid);
    }

    static string FriendlyApp(string exe, string title)
    {
        // UWP apps all live inside one host process; the title is the better name.
        if (Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
            return dash >= 0 ? title[(dash + 3)..] : title;
        }
        return AppName(exe);
    }

    /// <summary>
    /// The video site a browser window is showing, from a title like "Video - YouTube — Mozilla Firefox".
    /// Only well-known sites count: guessing from arbitrary titles would tie a crop to one page's name.
    /// </summary>
    public static string SiteOf(string exeName, string title)
    {
        if (!Browsers.Contains(exeName))
            return "";
        string[] parts = title.Split(new[] { " - ", " — ", " – ", " | ", " · " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            string part = parts[i];
            if (part.StartsWith('(') && part.IndexOf(')') is var close and > 0)
                part = part[(close + 1)..].Trim(); // "(3) YouTube": unread count
            if (KnownSites.Contains(part))
                return part;
        }
        return "";
    }

    static readonly HashSet<string> KnownSites = new(StringComparer.OrdinalIgnoreCase)
    {
        "YouTube", "YouTube Music", "Twitch", "Netflix", "Vimeo", "Disney+", "Prime Video", "Hulu", "Max", "Crunchyroll",
        "Spotify", "SoundCloud", "Kick", "Dailymotion", "Plex", "Apple TV", "Apple TV+", "Paramount+", "Peacock",
        "Bilibili", "Rumble", "TikTok", "Google Meet", "Meet", "Zoom", "Microsoft Teams", "Discord",
    };

    /// <summary>What a mirror's crop, size and position are remembered under: the site if there is one, else the app.</summary>
    public static string ProfileKey(string exeName, string title)
    {
        string site = SiteOf(exeName, title);
        return site.Length > 0 ? "site:" + site.ToLowerInvariant() : "app:" + exeName;
    }
}
