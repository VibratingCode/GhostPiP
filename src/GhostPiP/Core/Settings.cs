using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GhostPiP.Core;

/// <summary>How one mirror looks and behaves. Remembered per site or app.</summary>
public sealed class MirrorProfile
{
    public double Opacity { get; set; } = 0.92;
    public double[] Crop { get; set; } = { 0, 0, 1, 1 };   // left, top, width, height as fractions of the window
    public double? Scale { get; set; }                      // 1.0 = the source's real pixel size
    public int[]? Position { get; set; }                    // x, y in screen pixels
    public string Corners { get; set; } = "round";          // round | small | square
    public bool Shadow { get; set; } = true;
    public bool FadeOnHover { get; set; }
    public double HoverOpacity { get; set; } = 0.2;
    public bool HideWhenSourceActive { get; set; }
    public bool TabLock { get; set; } = true;
    public string TabKeyword { get; set; } = "";
    public bool HideFromCapture { get; set; }
    public bool KeepSourceAlive { get; set; }

    public MirrorProfile Clone()
    {
        var copy = (MirrorProfile)MemberwiseClone();
        copy.Crop = (double[])Crop.Clone();
        copy.Position = (int[]?)Position?.Clone();
        return copy;
    }

    public void Sanitize()
    {
        Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 0.92, 0.1, 1.0);
        HoverOpacity = Math.Clamp(double.IsFinite(HoverOpacity) ? HoverOpacity : 0.2, 0.0, 1.0);
        if (Scale is { } s && (!double.IsFinite(s) || s <= 0))
            Scale = null;
        if (Crop is not { Length: 4 } || Crop.Any(v => !double.IsFinite(v)) ||
            Crop[0] < 0 || Crop[1] < 0 || Crop[2] <= 0 || Crop[3] <= 0 || Crop[0] + Crop[2] > 1.001 || Crop[1] + Crop[3] > 1.001)
            Crop = new double[] { 0, 0, 1, 1 };
        if (Position is not { Length: 2 })
            Position = null;
        if (Corners is not ("round" or "small" or "square"))
            Corners = "round";
        TabKeyword ??= "";
    }
}

public sealed class SessionEntry
{
    public string Exe { get; set; } = "";
    public string ProfileKey { get; set; } = "";
    public string Keyword { get; set; } = "";
}

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public string Accent { get; set; } = "spectral";
    public string Controller { get; set; } = "hover";       // hover | always | off
    public bool StartWithWindows { get; set; }
    public bool StartHidden { get; set; }
    public bool KeepInTray { get; set; }
    public bool RestoreSession { get; set; }
    public bool ReduceMotion { get; set; }
    public MirrorProfile Defaults { get; set; } = new();
    public Dictionary<string, MirrorProfile> Profiles { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    public List<SessionEntry> Session { get; set; } = new();

    [JsonIgnore] public string Path { get; set; } = "";
}

/// <summary>Loads and saves settings.json next to the exe, upgrading a v1 file in place.</summary>
public static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DataFolder()
    {
        string? overridden = Environment.GetEnvironmentVariable("GHOSTPIP_DATA");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            Directory.CreateDirectory(overridden);
            return overridden;
        }
        string exeDir = AppContext.BaseDirectory;
        try
        {
            string probe = System.IO.Path.Combine(exeDir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return exeDir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            string fallback = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GhostPiP");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static AppSettings Load(string folder)
    {
        string path = System.IO.Path.Combine(folder, "settings.json");
        AppSettings settings;
        try
        {
            settings = File.Exists(path) ? Parse(File.ReadAllText(path), path) : new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }
        settings.Path = path;
        return settings;
    }

    /// <summary>A damaged file must never stop the app from starting; it falls back to defaults.</summary>
    public static AppSettings Parse(string text, string? path = null)
    {
        AppSettings settings;
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is JsonObject obj && !obj.ContainsKey("version"))
            {
                if (path is not null)
                    TryCopy(path, path[..^".json".Length] + ".v1.json");
                settings = FromV1(obj);
            }
            else
            {
                settings = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            settings = new AppSettings();
        }
        settings.Defaults ??= new MirrorProfile();
        settings.Profiles ??= new();
        settings.Hotkeys ??= new();
        settings.Session ??= new();
        settings.Defaults.Sanitize();
        foreach (var profile in settings.Profiles.Values)
            profile?.Sanitize();
        if (settings.Controller is not ("hover" or "always" or "off"))
            settings.Controller = "hover";
        return settings;
    }

    /// <summary>
    /// v1 kept one global crop plus a tab keyword. That crop was tuned for whatever the keyword names,
    /// so it moves to that site's profile and everything else starts uncropped.
    /// </summary>
    public static AppSettings FromV1(JsonObject v1)
    {
        var settings = new AppSettings();
        double opacity = Num(v1["opacity"]) ?? 0.92;
        settings.Defaults.Opacity = opacity;
        if (v1["toolbar"]?.GetValue<string>() is { } toolbar)
            settings.Controller = toolbar;

        var profile = settings.Defaults.Clone();
        if (v1["crop"] is JsonArray { Count: 4 } crop && crop.Select(Num).ToArray() is var values && values.All(v => v is not null))
            profile.Crop = values.Select(v => v!.Value).ToArray();
        profile.Scale = Num(v1["scale"]);
        if (v1["geometry"] is JsonArray { Count: >= 2 } geometry && Num(geometry[0]) is { } x && Num(geometry[1]) is { } y)
            profile.Position = new[] { (int)x, (int)y };

        string keyword = (v1["match"]?.GetValue<string>() ?? "").Trim();
        if (keyword.Length > 0)
        {
            profile.TabKeyword = keyword;
            settings.Profiles["site:" + keyword.ToLowerInvariant()] = profile;
        }
        else
        {
            settings.Defaults = profile;
        }
        return settings;
    }

    static double? Num(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        return value.TryGetValue(out double d) ? d : null;
    }

    public static void Save(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.Path))
            return;
        try
        {
            string temp = settings.Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, settings.Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save must never take the mirror down.
        }
    }

    static void TryCopy(string from, string to)
    {
        try
        {
            if (!File.Exists(to))
                File.Copy(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>"Start with Windows" lives in the per-user Run key.</summary>
public static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "GhostPiP";

    public static void Set(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null)
            return;
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\" --background");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
