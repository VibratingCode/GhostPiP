using System.IO;

namespace GhostPiP.Core;

public enum FirefoxState { Unknown, NeedsFix, NeedsRestart, Ok }

/// <summary>
/// Firefox stops drawing when it's fully covered, which freezes any mirror of it.
/// Two prefs in the profile's user.js turn that off.
/// </summary>
public static class Firefox
{
    static readonly (string Key, string Value)[] Prefs =
    {
        ("widget.windows.window_occlusion_tracking.enabled", "false"),
        ("dom.min_background_timeout_value", "0"),
    };

    static string? Profile()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string baseDir = Path.Combine(appData, "Mozilla", "Firefox");
        string ini = Path.Combine(baseDir, "profiles.ini");
        if (!File.Exists(ini))
            return null;

        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        try
        {
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                    sections.Add((line[1..^1], new(StringComparer.OrdinalIgnoreCase)));
                else if (sections.Count > 0 && line.IndexOf('=') is var eq and > 0)
                    sections[^1].Values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (IOException)
        {
            return null;
        }

        string? path = sections.FirstOrDefault(s => s.Name.StartsWith("Install") && s.Values.ContainsKey("Default")).Values?["Default"];
        path ??= sections.FirstOrDefault(s => s.Name.StartsWith("Profile") && s.Values.GetValueOrDefault("Default") == "1").Values?.GetValueOrDefault("Path");
        if (string.IsNullOrEmpty(path))
            return null;

        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
        return Directory.Exists(full) ? full : null;
    }

    public static FirefoxState Check(uint pid)
    {
        string? profile = Profile();
        if (profile is null)
            return FirefoxState.Unknown;
        string userJs = Path.Combine(profile, "user.js");
        string text;
        try
        {
            text = File.Exists(userJs) ? File.ReadAllText(userJs) : "";
        }
        catch (IOException)
        {
            text = "";
        }
        if (!Prefs.All(p => text.Contains($"\"{p.Key}\", {p.Value})")))
            return FirefoxState.NeedsFix;
        try
        {
            var started = System.Diagnostics.Process.GetProcessById((int)pid).StartTime.ToUniversalTime();
            if (started < File.GetLastWriteTimeUtc(userJs).AddSeconds(-1))
                return FirefoxState.NeedsRestart;
        }
        catch
        {
            // The process is gone or off limits; nothing useful to add.
        }
        return FirefoxState.Ok;
    }

    public static bool Fix()
    {
        string? profile = Profile();
        if (profile is null)
            return false;
        string userJs = Path.Combine(profile, "user.js");
        var lines = new List<string>();
        try
        {
            if (File.Exists(userJs))
                lines = File.ReadAllLines(userJs).Where(l => !Prefs.Any(p => l.Contains(p.Key)) && !l.Contains("Ghost PiP")).ToList();
            lines.Add("// Added by Ghost PiP: keep drawing while covered so mirrors stay live.");
            lines.AddRange(Prefs.Select(p => $"user_pref(\"{p.Key}\", {p.Value});"));
            File.WriteAllText(userJs, string.Join("\n", lines) + "\n");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
