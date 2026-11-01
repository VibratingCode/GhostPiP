using System.Collections.ObjectModel;
using System.Windows.Threading;
using GhostPiP.Core;
using GhostPiP.Views;

namespace GhostPiP.Mirroring;

/// <summary>Owns every live mirror and the 30 Hz loop that keeps them honest.</summary>
public sealed class MirrorManager
{
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _saveTimer;
    readonly AppSettings _settings;
    int _n;
    Mirror? _active;

    public ObservableCollection<Mirror> Items { get; } = new();
    public HudWindow Hud { get; }
    public ControllerWindow Controller { get; }
    public bool GhostHidden { get; private set; }

    public event Action? ActiveChanged;

    public Mirror? Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;
            _active = value;
            ActiveChanged?.Invoke();
        }
    }

    public MirrorManager(AppSettings settings, HudWindow hud, ControllerWindow controller)
    {
        _settings = settings;
        Hud = hud;
        Controller = controller;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public Mirror? Find(nint source) => Items.FirstOrDefault(m => m.Source == source);

    public Mirror? Start(nint hwnd, PxRect? from = null)
    {
        if (Find(hwnd) is { } existing)
        {
            Active = existing;
            Controller.Peek(2.5);
            return existing;
        }
        if (WindowScanner.Describe(hwnd) is not { } info)
        {
            Hud.Show("That window is gone", Ic.Warning);
            return null;
        }
        if (info.IsMinimized)
            Win.RestoreBehind(hwnd);

        string key = WindowScanner.ProfileKey(info.ExeName, info.Title);
        var profile = ProfileFor(key, info);
        var mirror = new Mirror(this, info, profile, key);
        if (!mirror.Attach(hwnd))
        {
            mirror.Dispose();
            Hud.Show("Couldn't mirror that window", Ic.Warning);
            return null;
        }
        if (GhostHidden)
            GhostHidden = false;
        Items.Add(mirror);
        Active = mirror;
        mirror.Show(from);
        Controller.Peek(2.5);
        Hud.Show($"Floating {info.App}", Ic.Pip, mirror);
        Dirty();
        return mirror;
    }

    MirrorProfile ProfileFor(string key, WindowInfo info)
    {
        if (!_settings.Profiles.TryGetValue(key, out var profile) || profile is null)
        {
            profile = _settings.Defaults.Clone();
            profile.Crop = new double[] { 0, 0, 1, 1 };
            profile.Scale = null;
            profile.Position = null;
            string site = WindowScanner.SiteOf(info.ExeName, info.Title);
            if (site.Length > 0)
                profile.TabKeyword = site;
            _settings.Profiles[key] = profile;
        }
        // Two mirrors of the same site can't share one position: the second gets its own copy for this run.
        if (Items.Any(m => m.ProfileKey == key))
        {
            profile = profile.Clone();
            profile.Position = null;
        }
        return profile;
    }

    public void Stop(Mirror mirror)
    {
        if (!Items.Remove(mirror))
            return;
        if (Active == mirror)
            Active = Items.LastOrDefault();
        Controller.Forget(mirror);
        mirror.Close();
        Dirty();
    }

    public void StopAll()
    {
        foreach (var mirror in Items.ToArray())
            Stop(mirror);
    }

    public void ToggleGhost()
    {
        if (Items.Count == 0)
            return;
        GhostHidden = !GhostHidden;
        Hud.Show(GhostHidden ? "All mirrors hidden" : "Mirrors back", GhostHidden ? Ic.Hide : Ic.View);
    }

    public void CycleActive()
    {
        if (Items.Count == 0)
            return;
        int i = Active is null ? -1 : Items.IndexOf(Active);
        Active = Items[(i + 1) % Items.Count];
        Controller.Peek(2);
        Hud.Show(Active.AppName, Ic.Pip, Active);
    }

    /// <summary>A free corner for a new mirror: bottom-right first, stacking upward if it's taken.</summary>
    public (int X, int Y) FreeSpot(int w, int h, int margin)
    {
        var wa = Active is { } a ? Win.WorkAreaOf(a.Rect.ToRECT()) : Win.PrimaryWorkArea();
        int x = wa.Right - w - margin;
        int y = wa.Bottom - h - margin;
        foreach (var other in Items.OrderByDescending(m => m.Rect.Bottom))
        {
            var r = other.Rect;
            if (r.W > 0 && x < r.Right && x + w > r.X && y < r.Bottom && y + h > r.Y)
                y = r.Y - h - margin;
        }
        return (x, Math.Max(wa.Top + margin, y));
    }

    public void MirrorMoved(Mirror mirror) => Controller.FollowMirror(mirror);

    public void ShowMenuFor(Mirror mirror, Core.POINT p)
    {
        Active = mirror;
        MirrorCommands.ShowMenu(mirror, p);
    }

    public void Dirty()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        _settings.Session = Items.Select(m => new SessionEntry
        {
            Exe = m.Info.ExeName,
            ProfileKey = m.ProfileKey,
            Keyword = WindowScanner.SiteOf(m.Info.ExeName, m.Title),
        }).ToList();
        SettingsStore.Save(_settings);
    }

    void Tick()
    {
        _n++;
        foreach (var mirror in Items.ToArray())
        {
            if (!mirror.Tick(_n))
            {
                string name = mirror.AppName;
                Stop(mirror);
                Hud.Show($"{name} closed, so its mirror did too", Ic.Warning, null, seconds: 2.2);
            }
        }

        // Whichever mirror the cursor is on becomes the one hotkeys and the controller act on.
        Mirror? hot = null;
        if (Win.VisibleCursor() is { } cursor)
        {
            hot = Items.LastOrDefault(m => m.IsVisibleNow && m.Rect.Contains(cursor, 6));
            if (hot is not null)
                Active = hot;
        }
        Controller.Tick(hot);
        Hud.Tick();

        if (_n % 16 == 0)
            Controller.KeepOnTop();
    }

    public void Shutdown()
    {
        _timer.Stop();
        SaveNow();
        foreach (var mirror in Items)
            mirror.Dispose();
        Items.Clear();
    }
}
