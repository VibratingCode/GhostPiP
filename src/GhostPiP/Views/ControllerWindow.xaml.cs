using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GhostPiP.Core;
using GhostPiP.Mirroring;
using static GhostPiP.Core.Native;

namespace GhostPiP.Views;

/// <summary>The floating mini-player. One instance, bound to whichever mirror the cursor is on.</summary>
public partial class ControllerWindow : FloatingWindow
{
    const double ShowDelay = 0.35, HideDelay = 0.9;

    Mirror? _mirror;
    DateTime? _hotSince, _coldSince;
    DateTime _peekUntil;
    bool _wanted, _syncing;
    string _artFor = "";
    int _n;

    public ControllerWindow() : base(clickThrough: false)
    {
        InitializeComponent();
        ArtButton.Click += (_, _) => { if (_mirror is { } m) Win.Focus(m.Source); };
        PrevButton.Click += (_, _) => MirrorCommands.Previous(_mirror);
        BackButton.Click += (_, _) => MirrorCommands.Seek(_mirror, -1);
        PlayButton.Click += (_, _) => MirrorCommands.PlayPause(_mirror);
        FwdButton.Click += (_, _) => MirrorCommands.Seek(_mirror, 1);
        NextButton.Click += (_, _) => MirrorCommands.Next(_mirror);
        MuteButton.Click += (_, _) => MirrorCommands.ToggleMute(_mirror);
        LockButton.Click += (_, _) => { MirrorCommands.ToggleLock(_mirror); SyncButtons(); };
        StudioButton.Click += (_, _) => App.ShowStudio(_mirror);
        CloseButton.Click += (_, _) => { if (_mirror is { } m) App.Mirrors.Stop(m); };
        MoreButton.Checked += (_, _) => { Sliders.Visibility = Visibility.Visible; RefreshAudio(); SyncSliders(); };
        MoreButton.Unchecked += (_, _) => Sliders.Visibility = Visibility.Collapsed;
        OpacitySlider.ValueChanged += (_, e) => { if (!_syncing && _mirror is { } m) { m.OpacityPercent = e.NewValue; SyncSliders(); } };
        SizeSlider.ValueChanged += (_, e) => { if (!_syncing && _mirror is { } m) { m.ScalePercent = e.NewValue; SyncSliders(); } };
        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (!_syncing && _mirror is { } m)
            {
                AppAudio.SetVolume(m.Info.ExeName, (float)(e.NewValue / 100));
                VolumeText.Text = $"{e.NewValue:0}%";
            }
        };
        ProgressBox.MouseLeftButtonDown += (_, e) =>
            MirrorCommands.SeekTo(_mirror, e.GetPosition(ProgressBox).X / Math.Max(1, ProgressBox.ActualWidth));
        // Scrolling anywhere on the bar is the quick way to change opacity, as in v1.
        MouseWheel += (_, e) => _mirror?.SetOpacity(_mirror.P.Opacity + (e.Delta > 0 ? 0.05 : -0.05));
        SizeChanged += (_, _) => Place();
    }

    public void Peek(double seconds) => _peekUntil = DateTime.UtcNow.AddSeconds(seconds);

    public void Forget(Mirror mirror)
    {
        if (_mirror != mirror)
            return;
        _mirror = null;
        _wanted = false;
        FadeOut();
    }

    public void FollowMirror(Mirror mirror)
    {
        if (mirror == _mirror && IsVisible)
            Place();
    }

    public void KeepOnTop()
    {
        if (IsVisible)
            Win.KeepOnTop(Handle);
    }

    public void RefreshAudio()
    {
        if (_mirror is not { } m)
            return;
        var audio = AppAudio.Get(m.Info.ExeName);
        _syncing = true;
        VolumeSlider.IsEnabled = audio is not null;
        VolumeSlider.Value = (audio?.Volume ?? 1) * 100;
        VolumeText.Text = audio is null ? "–" : $"{audio.Value.Volume * 100:0}%";
        _syncing = false;
        MuteButton.Content = audio?.Muted == true ? Ic.Mute : Ic.Volume;
    }

    void SyncSliders()
    {
        if (_mirror is not { } m)
            return;
        _syncing = true;
        OpacitySlider.Value = m.OpacityPercent;
        SizeSlider.Value = m.ScalePercent;
        OpacityText.Text = $"{m.OpacityPercent:0}%";
        SizeText.Text = $"{m.ScalePercent:0}%";
        _syncing = false;
    }

    void SyncButtons()
    {
        if (_mirror is not { } m)
            return;
        LockButton.Content = m.Locked ? Ic.Lock : Ic.Unlock;
    }

    void Bind(Mirror mirror)
    {
        if (_mirror == mirror)
            return;
        _mirror = mirror;
        _artFor = "";
        Art.Source = mirror.AppIcon;
        SetExcludedFromCapture(mirror.P.HideFromCapture);
        SyncButtons();
        SyncSliders();
        RefreshAudio();
        SyncMedia();
    }

    async void SyncMedia()
    {
        if (_mirror is not { } m)
            return;
        var state = MirrorCommands.MediaOf(m);
        bool hasTitle = state is { Title.Length: > 0 };
        TitleText.Text = hasTitle ? state!.Title : m.Title;
        SubText.Text = hasTitle && state!.Artist.Length > 0 ? $"{state.Artist}  ·  {m.AppName}" : m.AppName;
        PlayButton.Content = state?.IsPlaying == false ? Ic.Play : Ic.Pause;
        NextButton.IsEnabled = state is null || state.CanNext;
        PrevButton.IsEnabled = state is null || state.CanPrevious;

        bool hasTimeline = state is { Duration.Ticks: > 0 };
        ProgressBox.Visibility = hasTimeline ? Visibility.Visible : Visibility.Collapsed;
        if (hasTimeline)
            ProgressFill.Width = ProgressBox.ActualWidth * state!.Progress;

        string artKey = state is null ? "" : state.Id + state.Title;
        if (artKey != _artFor)
        {
            _artFor = artKey;
            var art = state is null ? null : await App.Media.ArtAsync(state.Id);
            if (_mirror == m && _artFor == artKey)
                Art.Source = art ?? (System.Windows.Media.ImageSource?)m.AppIcon;
        }
    }

    /// <summary>Under the mirror if it fits, above it if not, always on screen.</summary>
    void Place()
    {
        if (_mirror is not { } m || !IsVisible)
            return;
        var r = m.Rect;
        double scale = Scale;
        int w = (int)(ActualWidth * scale), h = (int)(ActualHeight * scale);
        var wa = Win.WorkAreaOf(r.ToRECT());
        int x = Math.Clamp(r.X + (r.W - w) / 2, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = r.Bottom - (int)(2 * scale);
        if (y + h > wa.Bottom)
            y = r.Y - h + (int)(10 * scale);
        SetWindowPos(Handle, 0, x, Math.Max(wa.Top, y), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    bool CursorOnMe()
    {
        if (!IsVisible || Win.VisibleCursor() is not { } c)
            return false;
        GetWindowRect(Handle, out var r);
        return c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;
    }

    public void Tick(Mirror? hot)
    {
        var now = DateTime.UtcNow;
        bool over = hot is not null || CursorOnMe();
        var subject = hot ?? _mirror ?? App.Mirrors.Active;

        bool want = false;
        if (subject is { IsVisibleNow: true })
        {
            string mode = App.Settings.Controller;
            if (mode == "always" || subject.Unlocked || now < _peekUntil)
                want = true;
            else if (mode == "hover")
            {
                if (over)
                {
                    _coldSince = null;
                    _hotSince ??= now;
                    want = _wanted || (now - _hotSince.Value).TotalSeconds > ShowDelay;
                }
                else
                {
                    _hotSince = null;
                    _coldSince ??= now;
                    want = _wanted && (now - _coldSince.Value).TotalSeconds < HideDelay;
                }
            }
        }

        if (want && !_wanted)
        {
            Bind(subject!);
            FadeIn();
        }
        else if (!want && _wanted)
        {
            FadeOut();
        }
        else if (want && hot is not null && hot != _mirror)
        {
            Bind(hot);
            Place();
        }
        _wanted = want;

        if (want && ++_n % 15 == 0)
        {
            SyncMedia();
            SyncSliders();
            SyncButtons();
        }
    }

    Animator.Tween? _fade;

    void FadeIn()
    {
        _fade?.Cancel();
        if (!IsVisible)
            Show();
        UpdateLayout();
        Place();
        Win.KeepOnTop(Handle);
        double from = Opacity;
        _fade = Animator.Run(150, t => Opacity = from + (1 - from) * t);
    }

    void FadeOut()
    {
        _fade?.Cancel();
        double from = Opacity;
        _fade = Animator.Run(180, t => Opacity = from * (1 - t), Ease.OutCubic, () =>
        {
            Hide();
            MoreButton.IsChecked = false;
        });
    }
}
