using GhostPiP.Core;
using GhostPiP.Views;
using static GhostPiP.Core.Native;

namespace GhostPiP.Mirroring;

/// <summary>The things a controller button, a menu item and a hotkey all have in common.</summary>
public static class MirrorCommands
{
    const int SeekSeconds = 10;

    static MediaHub Media => App.Media;
    static HudWindow Hud => App.Mirrors.Hud;

    public static MediaState? MediaOf(Mirror mirror) => Media.Find(mirror.Title, mirror.Info.ExeName);

    public static async void PlayPause(Mirror? mirror)
    {
        if (mirror is not null && MediaOf(mirror) is { } state && await Media.TogglePlayAsync(state.Id))
        {
            Hud.Show(state.IsPlaying ? "Paused" : "Playing", state.IsPlaying ? Ic.Pause : Ic.Play, mirror, seconds: 0.9);
            _ = Media.RefreshAsync();
            return;
        }
        // No session to talk to: the media key goes to whatever Windows considers current.
        Win.ReleaseModifiers();
        Win.Tap(VK_MEDIA_PLAY_PAUSE);
        Hud.Show("Play / pause", Ic.Play, mirror, seconds: 0.9);
    }

    public static async void Next(Mirror? mirror)
    {
        if (mirror is not null && MediaOf(mirror) is { } state && await Media.NextAsync(state.Id))
        {
            Hud.Show("Next", Ic.Next, mirror, seconds: 0.9);
            return;
        }
        Win.ReleaseModifiers();
        Win.Tap(VK_MEDIA_NEXT);
        Hud.Show("Next", Ic.Next, mirror, seconds: 0.9);
    }

    public static async void Previous(Mirror? mirror)
    {
        if (mirror is not null && MediaOf(mirror) is { } state && await Media.PreviousAsync(state.Id))
        {
            Hud.Show("Previous", Ic.Previous, mirror, seconds: 0.9);
            return;
        }
        Win.ReleaseModifiers();
        Win.Tap(VK_MEDIA_PREV);
        Hud.Show("Previous", Ic.Previous, mirror, seconds: 0.9);
    }

    public static async void Seek(Mirror? mirror, int direction)
    {
        if (mirror is null)
            return;
        string label = direction < 0 ? $"−{SeekSeconds} s" : $"+{SeekSeconds} s";
        string icon = direction < 0 ? Ic.Rewind : Ic.Forward;
        if (MediaOf(mirror) is { CanSeek: true } state &&
            await Media.SeekAsync(state, state.PositionNow + TimeSpan.FromSeconds(direction * SeekSeconds)))
        {
            Hud.Show(label, icon, mirror, seconds: 0.8);
            _ = Media.RefreshAsync();
            return;
        }
        // Fall back on keyboard shortcuts: J and L on YouTube, the arrows in most other players.
        bool youtube = mirror.Title.Contains("youtube", StringComparison.OrdinalIgnoreCase);
        int key = youtube ? (direction < 0 ? 'J' : 'L') : (direction < 0 ? VK_LEFT : VK_RIGHT);
        Win.SendKeyToWindow(mirror.Source, key);
        Hud.Show(youtube ? label : direction < 0 ? "Back" : "Forward", icon, mirror, seconds: 0.8);
    }

    public static void SeekTo(Mirror? mirror, double fraction)
    {
        if (mirror is not null && MediaOf(mirror) is { CanSeek: true } state)
            _ = Media.SeekAsync(state, TimeSpan.FromSeconds(state.Duration.TotalSeconds * fraction));
    }

    public static void ToggleMute(Mirror? mirror)
    {
        if (mirror is null)
            return;
        if (AppAudio.Get(mirror.Info.ExeName) is not { } audio)
        {
            Hud.Show($"{mirror.AppName} isn't making sound", Ic.Mute, mirror, seconds: 1.2);
            return;
        }
        bool muted = !audio.Muted;
        AppAudio.SetMute(mirror.Info.ExeName, muted);
        Hud.Show(muted ? $"{mirror.AppName} muted" : $"{mirror.AppName} unmuted", muted ? Ic.Mute : Ic.Volume, mirror, seconds: 1.0);
        App.Mirrors.Controller.RefreshAudio();
    }

    public static void ToggleLock(Mirror? mirror)
    {
        if (mirror is null)
            return;
        mirror.Locked = !mirror.Locked;
        if (mirror.Locked)
            Hud.Show("Locked. Clicks pass through", Ic.Lock, mirror);
        else
        {
            Hud.Show("Unlocked. Drag to move, scroll to resize", Ic.Move, mirror, seconds: 2.2);
            App.Mirrors.Controller.Peek(2.5);
        }
    }

    public static void CycleController()
    {
        var settings = App.Settings;
        settings.Controller = settings.Controller switch
        {
            "hover" => "always",
            "always" => "off",
            _ => "hover",
        };
        string label = settings.Controller switch
        {
            "hover" => "auto-hide",
            "always" => "always on",
            _ => "off",
        };
        App.Mirrors.Controller.Peek(settings.Controller == "off" ? 0 : 1.5);
        App.Mirrors.Dirty();
        Hud.Show($"Controller: {label}", Ic.Settings);
    }

    public static void MirrorForegroundWindow()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || Win.Pid(foreground) == Environment.ProcessId)
        {
            Hud.Show("Click into a window first, then try again", Ic.Warning);
            return;
        }
        App.Mirrors.Start(foreground);
    }

    public static void ShowMenu(Mirror mirror, POINT at) => MirrorMenu.Show(mirror, at);

    /// <summary>Everything the global hotkeys can do.</summary>
    public static void Run(HotAction action)
    {
        var mirrors = App.Mirrors;
        var active = mirrors.Active;
        switch (action)
        {
            case HotAction.Home:
                App.ShowMain();
                return;
            case HotAction.Quit:
                App.Quit();
                return;
            case HotAction.MirrorActive:
                MirrorForegroundWindow();
                return;
            case HotAction.GhostAll:
                mirrors.ToggleGhost();
                return;
            case HotAction.Controller:
                CycleController();
                return;
        }
        if (active is null)
        {
            Hud.Show("Nothing floating yet", Ic.Pip, seconds: 1.0);
            return;
        }
        switch (action)
        {
            case HotAction.OpacityUp: active.SetOpacity(active.P.Opacity + 0.1); break;
            case HotAction.OpacityDown: active.SetOpacity(active.P.Opacity - 0.1); break;
            case HotAction.Bigger: active.StepScale(1); break;
            case HotAction.Smaller: active.StepScale(-1); break;
            case HotAction.Lock: ToggleLock(active); break;
            case HotAction.NextCorner: active.NextCorner(); break;
            case HotAction.PlayPause: PlayPause(active); break;
            case HotAction.SeekBack: Seek(active, -1); break;
            case HotAction.SeekForward: Seek(active, 1); break;
            case HotAction.Mute: ToggleMute(active); break;
            case HotAction.NextMirror: mirrors.CycleActive(); break;
            case HotAction.Jump: Win.Focus(active.Source); break;
            case HotAction.Studio: App.ShowStudio(active); break;
            case HotAction.Precision: App.ShowStudio(active, precision: true); break;
        }
    }
}
