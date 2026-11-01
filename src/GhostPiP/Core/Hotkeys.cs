using System.Windows.Input;
using static GhostPiP.Core.Native;

namespace GhostPiP.Core;

public enum HotAction
{
    OpacityUp, OpacityDown, Bigger, Smaller, Lock, Controller, NextCorner,
    PlayPause, SeekBack, SeekForward, Mute,
    MirrorActive, GhostAll, NextMirror, Jump, Studio, Precision, Home, Quit,
}

public sealed record HotkeyDef(HotAction Action, string Label, string Group, string Default, string? Extra = null, bool Repeat = false);

/// <summary>A modifier set plus one key, e.g. Ctrl+Alt+Up.</summary>
public readonly record struct KeyChord(uint Mods, int Vk)
{
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        uint mods = 0;
        int vk = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (raw.Length == 0)
                        vk = VK_OEM_PLUS; // "Ctrl+Alt++": the plus key itself
                    else
                        vk = VkFromName(raw);
                    if (vk == 0)
                        return false;
                    break;
            }
        }
        if (vk == 0)
            return false;
        chord = new KeyChord(mods, vk);
        return true;
    }

    public override string ToString() => string.Join("+", Parts());

    public IEnumerable<string> Parts()
    {
        if ((Mods & MOD_CONTROL) != 0) yield return "Ctrl";
        if ((Mods & MOD_ALT) != 0) yield return "Alt";
        if ((Mods & MOD_SHIFT) != 0) yield return "Shift";
        if ((Mods & MOD_WIN) != 0) yield return "Win";
        yield return NameFromVk(Vk);
    }

    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Up"] = VK_UP, ["Down"] = VK_DOWN, ["Left"] = VK_LEFT, ["Right"] = VK_RIGHT, ["Space"] = VK_SPACE,
        ["="] = VK_OEM_PLUS, ["-"] = VK_OEM_MINUS, ["Num+"] = VK_ADD, ["Num-"] = VK_SUBTRACT,
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Backspace"] = 0x08, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PgUp"] = 0x21, ["PgDn"] = 0x22, ["Pause"] = 0x13,
        ["["] = 0xDB, ["]"] = 0xDD, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, ["\\"] = 0xDC, ["`"] = 0xC0,
    };

    static int VkFromName(string name)
    {
        if (Named.TryGetValue(name, out int vk))
            return vk;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
            return char.ToUpperInvariant(name[0]);
        if (name.Length >= 2 && name[0] is 'F' or 'f' && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[3..], out int n) && n is >= 0 and <= 9)
            return 0x60 + n;
        return 0;
    }

    public static string NameFromVk(int vk)
    {
        foreach (var (name, value) in Named)
            if (value == vk)
                return name;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87)
            return "F" + (vk - 0x70 + 1);
        if (vk is >= 0x60 and <= 0x69)
            return "Num" + (vk - 0x60);
        return $"0x{vk:X2}";
    }

    /// <summary>Reads a chord from a WPF key press; null while only modifiers are down or none are held.</summary>
    public static KeyChord? FromKeyEvent(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
            return null;
        uint mods = 0;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (held.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (held.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (held.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        bool bareFunctionKey = vk is >= 0x70 and <= 0x87;
        return mods == 0 && !bareFunctionKey ? null : new KeyChord(mods, vk);
    }
}

/// <summary>Registers global hotkeys and tells you which ones another app already owns.</summary>
public sealed class HotkeyManager
{
    public static readonly HotkeyDef[] All =
    {
        new(HotAction.OpacityUp, "Opacity up", "Mirror", "Ctrl+Alt+Up", Repeat: true),
        new(HotAction.OpacityDown, "Opacity down", "Mirror", "Ctrl+Alt+Down", Repeat: true),
        new(HotAction.Bigger, "Bigger", "Mirror", "Ctrl+Alt+=", "Ctrl+Alt+Num+", Repeat: true),
        new(HotAction.Smaller, "Smaller", "Mirror", "Ctrl+Alt+-", "Ctrl+Alt+Num-", Repeat: true),
        new(HotAction.Lock, "Unlock to move and resize", "Mirror", "Ctrl+Alt+L"),
        new(HotAction.NextCorner, "Move to the next corner", "Mirror", "Ctrl+Alt+K"),
        new(HotAction.Controller, "Controller: auto, always, off", "Mirror", "Ctrl+Alt+T"),
        new(HotAction.PlayPause, "Play / pause", "Playback", "Ctrl+Alt+Space"),
        new(HotAction.SeekBack, "Back 10 seconds", "Playback", "Ctrl+Alt+Left", Repeat: true),
        new(HotAction.SeekForward, "Forward 10 seconds", "Playback", "Ctrl+Alt+Right", Repeat: true),
        new(HotAction.Mute, "Mute the mirrored app", "Playback", "Ctrl+Alt+M"),
        new(HotAction.MirrorActive, "Float the window you're in", "App", "Ctrl+Alt+N"),
        new(HotAction.GhostAll, "Hide / show all mirrors", "App", "Ctrl+Alt+G"),
        new(HotAction.NextMirror, "Switch between mirrors", "App", "Ctrl+Alt+O"),
        new(HotAction.Jump, "Go to the source window", "App", "Ctrl+Alt+J"),
        new(HotAction.Studio, "Open Studio", "App", "Ctrl+Alt+C"),
        new(HotAction.Precision, "Open pixel trim", "App", "Ctrl+Alt+P"),
        new(HotAction.Home, "Open Ghost PiP", "App", "Ctrl+Alt+H", "Ctrl+Alt+W"),
        new(HotAction.Quit, "Quit", "App", "Ctrl+Alt+Q"),
    };

    readonly nint _window;
    readonly List<int> _registered = new();
    readonly Dictionary<int, HotAction> _ids = new();
    readonly HashSet<HotAction> _failed = new();

    public IReadOnlySet<HotAction> Failed => _failed;

    public HotkeyManager(nint window) => _window = window;

    public static KeyChord ChordFor(HotkeyDef def, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(def.Action.ToString(), out var text) && KeyChord.TryParse(text, out var chord))
            return chord;
        KeyChord.TryParse(def.Default, out var fallback);
        return fallback;
    }

    /// <summary>Replaces all registrations with the given bindings. Unbound actions stay unregistered.</summary>
    public void Apply(IReadOnlyDictionary<string, string> overrides)
    {
        Clear();
        int id = 1;
        foreach (var def in All)
        {
            var chord = ChordFor(def, overrides);
            bool ok = Register(id++, chord, def);
            if (def.Extra is not null && KeyChord.TryParse(def.Extra, out var extra))
                Register(id++, extra, def); // alternates (numpad keys) are a bonus, so they never count as failures
            if (!ok)
                _failed.Add(def.Action);
        }
    }

    bool Register(int id, KeyChord chord, HotkeyDef def)
    {
        uint mods = chord.Mods | (def.Repeat ? 0 : MOD_NOREPEAT);
        if (!RegisterHotKey(_window, id, mods, (uint)chord.Vk))
            return false;
        _registered.Add(id);
        _ids[id] = def.Action;
        return true;
    }

    public void Clear()
    {
        foreach (int id in _registered)
            UnregisterHotKey(_window, id);
        _registered.Clear();
        _ids.Clear();
        _failed.Clear();
    }

    public HotAction? ActionFor(int id) => _ids.TryGetValue(id, out var action) ? action : null;
}
