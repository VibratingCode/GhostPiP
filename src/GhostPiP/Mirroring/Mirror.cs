using System.Windows.Media;
using GhostPiP.Core;
using GhostPiP.Views;
using static GhostPiP.Core.Native;

namespace GhostPiP.Mirroring;

/// <summary>One floating, live copy of a window, and everything about how it looks and behaves.</summary>
public sealed class Mirror : ObservableObject, IOverlayInput, IDisposable
{
    const int DefaultWidth = 420;
    const int ScreenMargin = 24;
    const int HoverPad = 6;

    readonly MirrorManager _hub;
    readonly OverlayWindow _overlay;
    readonly HaloWindow _halo;
    DwmThumbnail? _thumb;

    PxRect _rect;                 // where it is this frame
    PxRect _target;               // where it's heading
    (int W, int H) _sourceSize;
    Animator.Tween? _move;
    double _alpha;
    bool _visibleWanted;
    bool _disposed;

    string _tabMatch = "";
    bool _hiddenForTab, _sourceMinimized, _hiddenForFocus, _hoverFading, _tempGrab;
    bool _locked = true;

    enum DragKind { None, Move, Resize }
    DragKind _drag;
    POINT _dragStart;
    PxRect _dragRect;
    (int X, int Y) _dragAnchor;
    bool _dragMoved;

    readonly List<double[]> _undo = new(), _redo = new();
    DateTime _lastUndoPush;

    public nint Source { get; private set; }
    public WindowInfo Info { get; private set; }
    public string ProfileKey { get; }
    public MirrorProfile P { get; }
    public ImageSource? AppIcon { get; private set; }
    public PxRect Rect => _rect;
    public bool IsVisibleNow => _overlay.Visible;
    public nint OverlayHandle => _overlay.Handle;

    public Mirror(MirrorManager hub, WindowInfo info, MirrorProfile profile, string profileKey)
    {
        _hub = hub;
        Info = info;
        Source = info.Hwnd;
        P = profile;
        ProfileKey = profileKey;
        _overlay = new OverlayWindow(this);
        _halo = new HaloWindow();
        AppIcon = AppIcons.ForWindow(info.Hwnd, info.ExePath);
        ApplyCorners();
        ApplyCaptureRule();
        Win.SetBorderColor(_overlay.Handle, DWMWA_COLOR_NONE);
        RefreshTabMatch();
    }

    // ------------------------------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------------------------------
    public bool Attach(nint source)
    {
        var thumb = DwmThumbnail.Create(_overlay.Handle, source);
        if (thumb is null)
            return false;
        _thumb?.Dispose();
        _thumb = thumb;
        Source = source;
        if (WindowScanner.Describe(source) is { } info)
        {
            Info = info;
            AppIcon = AppIcons.ForWindow(source, info.ExePath);
            Notify(nameof(AppIcon));
            Notify(nameof(Info));
            Notify(nameof(Title));
            Notify(nameof(AppName));
        }
        return true;
    }

    /// <summary>Appears at its home position, flying in from a gallery card if one is given.</summary>
    public void Show(PxRect? from)
    {
        _visibleWanted = true;
        Layout(animate: false);
        var home = _target;
        if (from is { } start && !Animator.ReduceMotion)
        {
            Apply(start);
            _alpha = 255;
            _overlay.SetAlpha(_alpha);
            _overlay.Show();
            MoveTo(home, animate: true, durationMs: 420);
            Animator.Run(420, t =>
            {
                _alpha = 255 + (TargetAlpha() - 255) * t;
                _overlay.SetAlpha(_alpha);
            }, Ease.OutCubic);
            ShowHaloWhenSettled(420);
        }
        else
        {
            var seed = new PxRect(home.X + home.W / 40, home.Y + home.H / 40, home.W - home.W / 20, home.H - home.H / 20);
            Apply(seed);
            _alpha = 0;
            _overlay.SetAlpha(0);
            _overlay.Show();
            MoveTo(home, animate: true, durationMs: 260);
            ShowHaloWhenSettled(120);
        }
    }

    void ShowHaloWhenSettled(int delayMs)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_disposed)
                SyncHalo();
        };
        timer.Start();
    }

    public void Close(Action? done = null)
    {
        _visibleWanted = false;
        _move?.Cancel();
        _halo.Hide();
        double from = _alpha;
        var rect = _rect;
        var end = new PxRect(rect.X + rect.W / 30, rect.Y + rect.H / 30, Math.Max(1, rect.W - rect.W / 15), Math.Max(1, rect.H - rect.H / 15));
        Animator.Run(150, t =>
        {
            if (_disposed)
                return;
            _alpha = from * (1 - t);
            _overlay.SetAlpha(_alpha);
            Apply(PxRect.Lerp(rect, end, t));
        }, Ease.OutCubic, () =>
        {
            Dispose();
            done?.Invoke();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _move?.Cancel();
        _thumb?.Dispose();
        _thumb = null;
        _overlay.Dispose();
        _halo.Close();
    }

    // ------------------------------------------------------------------------------------------
    // Placement
    // ------------------------------------------------------------------------------------------
    int Px(double dip) => (int)Math.Round(dip * _overlay.DpiScale);

    (double W, double H) CropPixels()
    {
        var (sw, sh) = _sourceSize;
        return (Math.Max(1.0, P.Crop[2] * sw), Math.Max(1.0, P.Crop[3] * sh));
    }

    RECT WorkArea() => _target.W > 0 ? Win.WorkAreaOf(_target.ToRECT()) : Win.PrimaryWorkArea();

    public (double Lo, double Hi) ScaleLimits()
    {
        var (cw, ch) = CropPixels();
        var wa = WorkArea();
        double hi = Math.Min(wa.Width / cw, wa.Height / ch);   // fits on screen
        double lo = Math.Min(hi, Px(96) / Math.Max(cw, ch));   // still findable
        return (lo, hi);
    }

    /// <summary>
    /// Sizes the mirror to crop × scale and places it. When the size changes, the corner nearest the
    /// screen's corner stays put, so a mirror parked bottom-right grows up and to the left.
    /// </summary>
    public void Layout(bool animate = false)
    {
        var size = Win.ClientSize(Source);
        if (size.W < 2 || size.H < 2)
            return; // minimized: keep the last shape until it's back
        _sourceSize = size;

        var (cw, ch) = CropPixels();
        P.Scale ??= Px(DefaultWidth) / cw;
        var (lo, hi) = ScaleLimits();
        double scale = Math.Clamp(P.Scale.Value, lo, hi);
        P.Scale = scale;
        int w = Math.Max(1, (int)Math.Round(cw * scale)), h = Math.Max(1, (int)Math.Round(ch * scale));

        int x, y;
        if (_target.W > 0 && (w, h) != (_target.W, _target.H))
        {
            var wa = Win.WorkAreaOf(_target.ToRECT());
            x = _target.X + _target.W / 2 > (wa.Left + wa.Right) / 2 ? _target.Right - w : _target.X;
            y = _target.Y + _target.H / 2 > (wa.Top + wa.Bottom) / 2 ? _target.Bottom - h : _target.Y;
        }
        else if (_target.W > 0)
        {
            (x, y) = (_target.X, _target.Y);
        }
        else if (P.Position is { } saved)
        {
            var wa = Win.WorkAreaAt(new POINT(saved[0] + w / 2, saved[1] + h / 2));
            x = Math.Clamp(saved[0], wa.Left, Math.Max(wa.Left, wa.Right - w));
            y = Math.Clamp(saved[1], wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        }
        else
        {
            (x, y) = _hub.FreeSpot(w, h, Px(ScreenMargin));
        }

        P.Position = new[] { x, y };
        MoveTo(new PxRect(x, y, w, h), animate);
        Notify(nameof(ScalePercent));
        Notify(nameof(SizeText));
        Notify(nameof(CropText));
    }

    void MoveTo(PxRect target, bool animate, int durationMs = 240)
    {
        _move?.Cancel();
        _target = target;
        if (!animate || !_overlay.Visible)
        {
            Apply(target);
            return;
        }
        var from = _rect;
        _move = Animator.Run(durationMs, t =>
        {
            if (!_disposed)
                Apply(PxRect.Lerp(from, target, t));
        }, Ease.Spring);
    }

    /// <summary>Moves the window, its shadow and the thumbnail together, in one atomic step.</summary>
    void Apply(PxRect r)
    {
        _rect = r;
        var flags = SWP_NOACTIVATE | SWP_NOZORDER;
        if (_halo.IsReady && _halo.IsVisible)
        {
            nint batch = BeginDeferWindowPos(2);
            batch = DeferWindowPos(batch, _overlay.Handle, 0, r.X, r.Y, r.W, r.H, flags);
            batch = _halo.Defer(batch, r, _overlay.DpiScale);
            EndDeferWindowPos(batch);
        }
        else
        {
            SetWindowPos(_overlay.Handle, 0, r.X, r.Y, r.W, r.H, flags);
        }
        UpdateThumb();
        _hub.MirrorMoved(this);
    }

    void UpdateThumb()
    {
        if (_thumb is null)
            return;
        var (sw, sh) = _sourceSize;
        double[] c = P.Crop;
        // Round, don't truncate: a pixel-exact crop must survive the trip through fractions.
        var source = new RECT((int)Math.Round(c[0] * sw), (int)Math.Round(c[1] * sh),
            (int)Math.Round((c[0] + c[2]) * sw), (int)Math.Round((c[1] + c[3]) * sh));
        _thumb.Update(new RECT(0, 0, _rect.W, _rect.H), source);
    }

    /// <summary>Settle against nearby screen edges, like a phone's picture-in-picture.</summary>
    void Snap()
    {
        var (x, y, w, h) = (_rect.X, _rect.Y, _rect.W, _rect.H);
        var wa = Win.WorkAreaOf(_rect.ToRECT());
        int m = Px(16), reach = Px(40);
        if (Math.Abs(x - wa.Left) < reach)
            x = wa.Left + m;
        else if (Math.Abs(wa.Right - (x + w)) < reach)
            x = wa.Right - m - w;
        if (Math.Abs(y - wa.Top) < reach)
            y = wa.Top + m;
        else if (Math.Abs(wa.Bottom - (y + h)) < reach)
            y = wa.Bottom - m - h;
        P.Position = new[] { x, y };
        MoveTo(new PxRect(x, y, w, h), animate: true);
        _hub.Dirty();
    }

    /// <summary>Glides to a spot on a 3×3 grid of the screen: 0, 1, 2 across and down.</summary>
    public void PlaceAt(int column, int row)
    {
        var wa = WorkArea();
        int m = Px(16);
        int x = column switch
        {
            0 => wa.Left + m,
            1 => (wa.Left + wa.Right - _target.W) / 2,
            _ => wa.Right - m - _target.W,
        };
        int y = row switch
        {
            0 => wa.Top + m,
            1 => (wa.Top + wa.Bottom - _target.H) / 2,
            _ => wa.Bottom - m - _target.H,
        };
        P.Position = new[] { x, y };
        MoveTo(new PxRect(x, y, _target.W, _target.H), animate: true, durationMs: 340);
        _hub.Dirty();
    }

    public void NextCorner()
    {
        var wa = WorkArea();
        bool right = _target.X + _target.W / 2 > (wa.Left + wa.Right) / 2;
        bool bottom = _target.Y + _target.H / 2 > (wa.Top + wa.Bottom) / 2;
        // Clockwise round the screen.
        var (column, row) = (right, bottom) switch
        {
            (true, true) => (0, 2),
            (false, true) => (0, 0),
            (false, false) => (2, 0),
            _ => (2, 2),
        };
        PlaceAt(column, row);
    }

    // ------------------------------------------------------------------------------------------
    // Look
    // ------------------------------------------------------------------------------------------
    public string Title => Info.Title;
    public string AppName => Info.App;

    /// <summary>Opacity as 10–100.</summary>
    public double OpacityPercent
    {
        get => Math.Round(P.Opacity * 100);
        set => SetOpacity(value / 100, hud: false);
    }

    public void SetOpacity(double value, bool hud = true)
    {
        value = Math.Round(Math.Clamp(value, 0.1, 1.0), 2);
        if (value != P.Opacity)
        {
            P.Opacity = value;
            Notify(nameof(OpacityPercent));
            _hub.Dirty();
        }
        _alpha = TargetAlpha();
        _overlay.SetAlpha(_alpha);
        SyncHalo();
        if (hud && _overlay.Visible)
            _hub.Hud.Show($"Opacity {Math.Round(value * 100)}%", Ic.Opacity, this, meter: value);
    }

    public double ScalePercent
    {
        get => Math.Round((P.Scale ?? 1) * 100);
        set => SetScale(value / 100, hud: false);
    }

    public string SizeText => _target.W > 0 ? $"{_target.W} × {_target.H}" : "";

    public void SetScale(double scale, bool hud = true)
    {
        P.Scale = scale;
        Layout(animate: hud);
        _hub.Dirty();
        if (hud && _overlay.Visible)
            _hub.Hud.Show($"Size {Math.Round((P.Scale ?? 1) * 100)}%  ·  {_target.W} × {_target.H}", Ic.ZoomIn, this);
    }

    public void StepScale(int direction)
    {
        double current = P.Scale ?? 1.0;
        double next = Math.Round(current * (direction > 0 ? 1.1 : 1 / 1.1), 2);
        if (next == current)
            next = current + (direction > 0 ? 0.01 : -0.01);
        SetScale(next);
    }

    public string Corners
    {
        get => P.Corners;
        set
        {
            if (P.Corners == value)
                return;
            P.Corners = value;
            ApplyCorners();
            SyncHalo();
            Notify();
            _hub.Dirty();
        }
    }

    void ApplyCorners() => Win.SetCorners(_overlay.Handle, P.Corners switch
    {
        "square" => DWMWCP_DONOTROUND,
        "small" => DWMWCP_ROUNDSMALL,
        _ => DWMWCP_ROUND,
    });

    public bool Shadow
    {
        get => P.Shadow;
        set
        {
            if (P.Shadow == value)
                return;
            P.Shadow = value;
            SyncHalo();
            Notify();
            _hub.Dirty();
        }
    }

    void SyncHalo()
    {
        if (_disposed)
            return;
        bool wanted = _overlay.Visible && _alpha > 2 && (P.Shadow || !Locked);
        _halo.Sync(wanted, _rect, _overlay.DpiScale, P.Corners, P.Shadow, !Locked, _alpha / 255, _overlay.Handle);
    }

    public bool HideFromCapture
    {
        get => P.HideFromCapture;
        set
        {
            if (P.HideFromCapture == value)
                return;
            P.HideFromCapture = value;
            ApplyCaptureRule();
            Notify();
            _hub.Dirty();
        }
    }

    void ApplyCaptureRule()
    {
        Win.SetExcludedFromCapture(_overlay.Handle, P.HideFromCapture);
        _halo.SetExcludedFromCapture(P.HideFromCapture);
    }

    // ------------------------------------------------------------------------------------------
    // Crop
    // ------------------------------------------------------------------------------------------
    public double[] Crop => P.Crop;
    public (int W, int H) SourceSize => _sourceSize;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string CropText
    {
        get
        {
            var (sw, sh) = _sourceSize;
            int w = (int)Math.Round(P.Crop[2] * sw), h = (int)Math.Round(P.Crop[3] * sh);
            return w > 0 && h > 0 ? $"{w} × {h}  ·  {SmartCrop.RatioLabel(w, h)}" : "";
        }
    }

    /// <summary>Applies a crop. Bursts of changes (a drag, repeated nudges) undo as one step.</summary>
    public void SetCrop(double[] crop, bool saveUndo = true)
    {
        if (saveUndo && !crop.SequenceEqual(P.Crop))
        {
            if (DateTime.UtcNow - _lastUndoPush > TimeSpan.FromMilliseconds(600))
            {
                _undo.Add((double[])P.Crop.Clone());
                _redo.Clear();
            }
            _lastUndoPush = DateTime.UtcNow;
        }
        P.Crop = crop;
        Layout();
        Notify(nameof(Crop));
        Notify(nameof(CanUndo));
        Notify(nameof(CanRedo));
        _hub.Dirty();
    }

    void Swap(List<double[]> from, List<double[]> to)
    {
        if (from.Count == 0)
            return;
        to.Add((double[])P.Crop.Clone());
        var crop = from[^1];
        from.RemoveAt(from.Count - 1);
        _lastUndoPush = default;
        SetCrop(crop, saveUndo: false);
    }

    public void Undo() => Swap(_undo, _redo);
    public void Redo() => Swap(_redo, _undo);

    // ------------------------------------------------------------------------------------------
    // Behaviour
    // ------------------------------------------------------------------------------------------
    public bool Locked
    {
        get => _locked;
        set
        {
            if (!Set(ref _locked, value))
                return;
            ApplyInputMode();
            SyncHalo();
            Notify(nameof(Unlocked));
        }
    }

    public bool Unlocked => !_locked;

    void ApplyInputMode()
    {
        bool through = _locked && !_tempGrab;
        _overlay.SetClickThrough(through);
        Win.SetBorderColor(_overlay.Handle, through ? DWMWA_COLOR_NONE : Theme.AccentColorRef);
    }

    public bool FadeOnHover
    {
        get => P.FadeOnHover;
        set
        {
            if (P.FadeOnHover == value)
                return;
            P.FadeOnHover = value;
            Notify();
            _hub.Dirty();
        }
    }

    public double HoverOpacityPercent
    {
        get => Math.Round(P.HoverOpacity * 100);
        set
        {
            P.HoverOpacity = Math.Clamp(value / 100, 0, 1);
            Notify();
            _hub.Dirty();
        }
    }

    public bool HideWhenSourceActive
    {
        get => P.HideWhenSourceActive;
        set
        {
            if (P.HideWhenSourceActive == value)
                return;
            P.HideWhenSourceActive = value;
            Notify();
            _hub.Dirty();
        }
    }

    public bool KeepSourceAlive
    {
        get => P.KeepSourceAlive;
        set
        {
            if (P.KeepSourceAlive == value)
                return;
            P.KeepSourceAlive = value;
            Notify();
            _hub.Dirty();
        }
    }

    public bool TabLock
    {
        get => P.TabLock;
        set
        {
            if (P.TabLock == value)
                return;
            P.TabLock = value;
            RefreshTabMatch();
            Notify();
            _hub.Dirty();
        }
    }

    public string TabKeyword
    {
        get => P.TabKeyword;
        set
        {
            P.TabKeyword = (value ?? "").Trim();
            RefreshTabMatch();
            Notify();
            _hub.Dirty();
        }
    }

    /// <summary>Only lock if the keyword fits the window right now; otherwise it would hide straight away.</summary>
    void RefreshTabMatch()
    {
        string keyword = P.TabLock ? P.TabKeyword.Trim() : "";
        _tabMatch = keyword.Length > 0 && Win.Title(Source).Contains(keyword, StringComparison.OrdinalIgnoreCase) ? keyword : "";
        if (_tabMatch.Length == 0)
            _hiddenForTab = false;
    }

    double TargetAlpha()
    {
        if (!_visibleWanted || _hub.GhostHidden || _hiddenForTab || _sourceMinimized || _hiddenForFocus)
            return 0;
        double opacity = P.Opacity;
        if (_hoverFading)
            opacity = Math.Min(opacity, P.HoverOpacity);
        return opacity * 255;
    }

    /// <summary>Runs about 30 times a second. Returns false when the source is gone.</summary>
    public bool Tick(int n)
    {
        if (!IsWindow(Source))
            return false;

        if (n % 8 == 0)
            CheckTab();
        if (n % 4 == 0)
            CheckMinimized();
        if (n % 16 == 0 && _overlay.Visible)
        {
            // Games and other topmost apps can bury us; also follow the source if it was resized.
            Win.KeepOnTop(_overlay.Handle);
            if (_halo.IsReady && _halo.IsVisible)
                Win.KeepOnTop(_halo.Handle);
            if (Win.ClientSize(Source) is var size && size != _sourceSize && size.W > 1 && size.H > 1)
                Layout();
            else
                UpdateThumb();
        }

        _hiddenForFocus = P.HideWhenSourceActive && GetForegroundWindow() == Source;

        var cursor = Win.VisibleCursor();
        bool over = cursor is { } c && _rect.Contains(c);
        _hoverFading = P.FadeOnHover && _locked && over && _drag == DragKind.None;

        // Holding Ctrl+Alt over a locked mirror grabs it without unlocking.
        bool grab = _locked && over && Win.IsKeyDown(VK_CONTROL) && Win.IsKeyDown(VK_MENU);
        if (grab != _tempGrab && _drag == DragKind.None)
        {
            _tempGrab = grab;
            ApplyInputMode();
            if (grab)
                _hub.Hud.Show("Grabbed. Drag to move, scroll to resize", Ic.Move, this, seconds: 1.2);
        }

        StepAlpha();
        return true;
    }

    void StepAlpha()
    {
        double target = TargetAlpha();
        if (Math.Abs(_alpha - target) >= 0.5)
        {
            double step = (target - _alpha) * 0.32;
            _alpha = Math.Abs(step) < 1 ? target : _alpha + step;
            if (target > 0 && !_overlay.Visible)
                _overlay.Show();
            _overlay.SetAlpha(_alpha);
        }
        if (_alpha < 0.5 && target == 0 && _overlay.Visible)
        {
            _overlay.Hide();
        }
        SyncHalo();
    }

    void CheckTab()
    {
        if (_tabMatch.Length == 0)
            return;
        if (Win.Title(Source).Contains(_tabMatch, StringComparison.OrdinalIgnoreCase))
        {
            _hiddenForTab = false;
            return;
        }
        // The tab moved (e.g. dragged into its own window): follow it.
        nint other = WindowScanner.FindByTitle(_tabMatch, exclude: Source);
        if (other != 0 && _hub.Find(other) is null && Attach(other))
        {
            _hiddenForTab = false;
            Layout();
            _hub.Hud.Show($"Following {_tabMatch} to another window", Ic.Pip, this);
        }
        else
        {
            _hiddenForTab = true;
        }
    }

    void CheckMinimized()
    {
        bool minimized = IsIconic(Source);
        if (minimized && P.KeepSourceAlive)
        {
            Win.RestoreBehind(Source);
            minimized = false;
        }
        if (minimized != _sourceMinimized)
        {
            _sourceMinimized = minimized;
            if (minimized)
                _hub.Hud.Show($"{AppName} is minimized. The mirror resumes when it's back", Ic.Warning, null, seconds: 2.4);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Mouse, while unlocked or grabbed
    // ------------------------------------------------------------------------------------------
    int Corner(POINT p)
    {
        int reach = Px(18);
        bool left = p.X - _rect.X < reach, right = _rect.Right - p.X < reach;
        bool top = p.Y - _rect.Y < reach, bottom = _rect.Bottom - p.Y < reach;
        if (top && left) return 1;
        if (top && right) return 2;
        if (bottom && left) return 3;
        if (bottom && right) return 4;
        return 0;
    }

    public nint OverlayCursor(POINT p) => LoadCursorW(0, Corner(p) switch
    {
        1 or 4 => IDC_SIZENWSE,
        2 or 3 => IDC_SIZENESW,
        _ => IDC_SIZEALL,
    });

    public void OverlayPress(POINT p)
    {
        _move?.Cancel();
        _target = _rect;
        _dragStart = p;
        _dragRect = _rect;
        _dragMoved = false;
        int corner = Corner(p);
        if (corner == 0)
        {
            _drag = DragKind.Move;
            return;
        }
        _drag = DragKind.Resize;
        _dragAnchor = corner switch
        {
            1 => (_rect.Right, _rect.Bottom),
            2 => (_rect.X, _rect.Bottom),
            3 => (_rect.Right, _rect.Y),
            _ => (_rect.X, _rect.Y),
        };
    }

    public void OverlayDrag(POINT p)
    {
        if (Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) > 1)
            _dragMoved = true;
        if (_drag == DragKind.Move)
        {
            var r = _dragRect with { X = _dragRect.X + p.X - _dragStart.X, Y = _dragRect.Y + p.Y - _dragStart.Y };
            _target = r;
            P.Position = new[] { r.X, r.Y };
            Apply(r);
        }
        else if (_drag == DragKind.Resize)
        {
            var (cw, ch) = CropPixels();
            var (lo, hi) = ScaleLimits();
            double scale = Math.Clamp(Math.Max(Math.Abs(p.X - _dragAnchor.X) / cw, Math.Abs(p.Y - _dragAnchor.Y) / ch), lo, hi);
            int w = (int)Math.Round(cw * scale), h = (int)Math.Round(ch * scale);
            int x = p.X < _dragAnchor.X ? _dragAnchor.X - w : _dragAnchor.X;
            int y = p.Y < _dragAnchor.Y ? _dragAnchor.Y - h : _dragAnchor.Y;
            P.Scale = scale;
            P.Position = new[] { x, y };
            _target = new PxRect(x, y, w, h);
            Apply(_target);
            Notify(nameof(ScalePercent));
            Notify(nameof(SizeText));
        }
    }

    public void OverlayRelease()
    {
        var kind = _drag;
        _drag = DragKind.None;
        if (kind == DragKind.None || !_dragMoved)
            return;
        Snap();
    }

    public void OverlayDoubleClick() => Win.Focus(Source);

    public void OverlayWheel(int delta) => StepScale(delta > 0 ? 1 : -1);

    public void OverlayContextMenu(POINT p) => _hub.ShowMenuFor(this, p);
}
