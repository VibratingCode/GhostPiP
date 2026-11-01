using Frame = GhostPiP.Core.Frame;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GhostPiP.Core;
using GhostPiP.Mirroring;

namespace GhostPiP.Views;

/// <summary>Crop, look and behaviour for one mirror, around a live preview of its source.</summary>
public sealed class StudioPage : UserControl
{
    readonly Grid _body = new();
    readonly StackPanel _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly WrapPanel _mirrorChips = new();
    readonly Border _stage = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    readonly PixelTrim _pixels = new() { Visibility = Visibility.Collapsed };
    readonly RadioButton _frameMode, _pixelMode;
    readonly WrapPanel _pixelTools = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _inspector = new();
    readonly TextBlock _cropInfo;
    readonly Button _find, _undo, _redo;
    readonly Border _firefoxBanner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    readonly TextBlock _bannerText;
    readonly Button _bannerFix;
    readonly Slider _volume = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    Mirror? _mirror;
    DwmThumbnail? _dim, _bright;
    CropSurface? _surface;
    bool _active, _scanning;

    public StudioPage()
    {
        // Left: the stage -------------------------------------------------------------------
        var left = new DockPanel { Margin = new Thickness(0, 26, 18, 0) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _frameMode = Chip("Frame", "Mode", true, () => SetPixelMode(false));
        _pixelMode = Chip("Pixel", "Mode", false, () => SetPixelMode(true));
        _undo = IconButton(Ic.Undo, "Undo (Ctrl+Z)", () => _mirror?.Undo());
        _redo = IconButton(Ic.Redo, "Redo (Ctrl+Y)", () => _mirror?.Redo());
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        modes.Children.Add(_frameMode);
        modes.Children.Add(_pixelMode);
        modes.Children.Add(_undo);
        modes.Children.Add(_redo);
        DockPanel.SetDock(modes, Dock.Right);
        top.Children.Add(modes);
        top.Children.Add(_mirrorChips);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);

        var tools = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var actions = new DockPanel();
        _find = TextButton("Find video", FindVideo, primary: true);
        _cropInfo = new TextBlock { Style = (Style)FindResource("T.Secondary"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_cropInfo, Dock.Right);
        actions.Children.Add(_cropInfo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_find);
        buttons.Children.Add(TextButton("Reset", () => _mirror?.SetCrop(new double[] { 0, 0, 1, 1 })));
        foreach (var (label, a, b) in new[] { ("16:9", 16, 9), ("4:3", 4, 3), ("21:9", 21, 9), ("1:1", 1, 1), ("9:16", 9, 16) })
            buttons.Children.Add(TextButton(label, () => FitRatio((double)a / b)));
        actions.Children.Add(buttons);
        tools.Children.Add(actions);

        foreach (var (label, z) in new[] { ("2×", 2), ("4×", 4), ("8×", 8), ("16×", 16) })
            _pixelTools.Children.Add(Chip(label, "Zoom", z == 8, () => _pixels.SetZoom(z)));
        _pixelTools.Children.Add(TextButton("Snap all", () => _pixels.SnapAll()));
        _pixelTools.Children.Add(TextButton("Shave flat bars", () => _pixels.ShaveAll()));
        _pixelTools.Children.Add(TextButton("Fresh snapshot", () => _pixels.Refresh()));
        tools.Children.Add(_pixelTools);

        _bannerText = new TextBlock { Style = (Style)FindResource("T.Secondary"), VerticalAlignment = VerticalAlignment.Center };
        _bannerFix = TextButton("Fix it", FixFirefox);
        var banner = new DockPanel();
        DockPanel.SetDock(_bannerFix, Dock.Right);
        banner.Children.Add(_bannerFix);
        banner.Children.Add(_bannerText);
        _firefoxBanner.Child = banner;
        _firefoxBanner.BorderBrush = (Brush)FindResource("Warn");
        tools.Children.Add(_firefoxBanner);
        DockPanel.SetDock(tools, Dock.Bottom);
        left.Children.Add(tools);

        _stage.Background = (Brush)FindResource("Well");
        _stage.BorderBrush = (Brush)FindResource("Hairline");
        _stage.SizeChanged += (_, _) => PlaceStage();
        var stageHost = new Grid();
        stageHost.Children.Add(_stage);
        stageHost.Children.Add(_pixels);
        left.Children.Add(stageHost);

        // Right: the inspector ---------------------------------------------------------------
        var scroll = new ScrollViewer { Content = _inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 318, Margin = new Thickness(0, 26, 0, 0), Focusable = false };
        BuildInspector();

        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(scroll, 1);
        _body.Children.Add(left);
        _body.Children.Add(scroll);

        _empty.Children.Add(new TextBlock { Text = "Nothing floating yet", Style = (Style)FindResource("T.Display"), HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock { Text = "Pick a window on the Windows page, then shape it here.", Style = (Style)FindResource("T.Secondary"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });

        var root = new Grid();
        root.Children.Add(_body);
        root.Children.Add(_empty);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            if (e.Key == Key.Z) { _mirror?.Undo(); e.Handled = true; }
            if (e.Key == Key.Y) { _mirror?.Redo(); e.Handled = true; }
        };
        _timer.Tick += (_, _) =>
        {
            PlaceStage();
            SyncAudio();
        };
        App.Mirrors.Items.CollectionChanged += (_, _) =>
        {
            if (_mirror is not null && !App.Mirrors.Items.Contains(_mirror))
                Select(App.Mirrors.Active, false);
            RebuildChips();
        };
    }

    // Little builders ---------------------------------------------------------------------------
    RadioButton Chip(string text, string group, bool on, Action action)
    {
        var chip = new RadioButton { Content = text, GroupName = group, IsChecked = on, Style = (Style)FindResource("Chip") };
        chip.Checked += (_, _) => action();
        return chip;
    }

    Button IconButton(string glyph, string tip, Action action)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("Btn.Icon") };
        b.Click += (_, _) => action();
        return b;
    }

    Button TextButton(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "Btn.Primary" : "Btn"), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 0, 12, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    StackPanel Section(string title)
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("T.Section"), Margin = new Thickness(0, 0, 0, 10) });
        _inspector.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 10, 10), Child = inner });
        return inner;
    }

    static void Add(Panel panel, UIElement child) => panel.Children.Add(child);

    FrameworkElement SliderRow(string label, string property, double min, double max)
    {
        var slider = new Slider { Minimum = min, Maximum = max };
        slider.SetBinding(RangeBase.ValueProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        var value = new TextBlock { Style = (Style)FindResource("T.Secondary"), TextAlignment = TextAlignment.Right };
        value.SetBinding(TextBlock.TextProperty, new Binding(property) { StringFormat = "{0:0}%" });
        var head = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);
        head.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("T.Body") });
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(head);
        row.Children.Add(slider);
        return row;
    }

    FrameworkElement Toggle(string label, string property, string? hint = null)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch") };
        box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        var words = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        words.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("T.Body"), TextWrapping = TextWrapping.Wrap });
        if (hint is not null)
            words.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("T.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(words);
        return row;
    }

    void BuildInspector()
    {
        var look = Section("Look");
        Add(look, SliderRow("Opacity", nameof(Mirror.OpacityPercent), 10, 100));
        Add(look, SliderRow("Size", nameof(Mirror.ScalePercent), 5, 300));
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (int pct in new[] { 50, 75, 100, 150 })
            Add(sizes, TextButton($"{pct}%", () => _mirror?.SetScale(pct / 100.0)));
        Add(look, sizes);
        var corners = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (label, value) in new[] { ("Round", "round"), ("Soft", "small"), ("Square", "square") })
            Add(corners, Chip(label, "Corners", value == "round", () => { if (_mirror is { } m) m.Corners = value; }));
        Add(look, corners);
        Add(look, Toggle("Soft shadow", nameof(Mirror.Shadow)));

        var place = Section("Position");
        var spots = new UniformGrid { Rows = 3, Columns = 3, Width = 132, Height = 84, HorizontalAlignment = HorizontalAlignment.Left };
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                int c = col, r = row;
                var dot = new Button { Style = (Style)FindResource("Btn"), MinHeight = 0, Margin = new Thickness(2), Padding = new Thickness(0), Focusable = false };
                dot.Click += (_, _) => _mirror?.PlaceAt(c, r);
                spots.Children.Add(dot);
            }
        Add(place, spots);

        var behave = Section("Behaviour");
        Add(behave, Toggle("Click-through", nameof(Mirror.Locked), "Off lets you drag it and scroll to resize."));
        Add(behave, Toggle("Fade when hovered", nameof(Mirror.FadeOnHover), "See what's under it as your cursor passes."));
        Add(behave, SliderRow("Faded opacity", nameof(Mirror.HoverOpacityPercent), 0, 100));
        Add(behave, Toggle("Hide when its window is in front", nameof(Mirror.HideWhenSourceActive)));
        Add(behave, Toggle("Tab lock", nameof(Mirror.TabLock), "Hides when the title stops containing the word below, and follows that tab to a new window."));
        var keyword = new TextBox { Tag = "Word in the window title", Margin = new Thickness(0, 0, 0, 10) };
        keyword.SetBinding(TextBox.TextProperty, new Binding(nameof(Mirror.TabKeyword)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        Add(behave, keyword);
        Add(behave, Toggle("Keep its window drawing", nameof(Mirror.KeepSourceAlive), "If it gets minimized, restore it behind everything so the mirror stays live."));
        Add(behave, Toggle("Hide from screenshots and streams", nameof(Mirror.HideFromCapture)));

        var sound = Section("Sound");
        _volume.Minimum = 0;
        _volume.Maximum = 100;
        _volume.ValueChanged += (_, e) =>
        {
            if (_mirror is { } m && _volume.IsMouseCaptureWithin)
                AppAudio.SetVolume(m.Info.ExeName, (float)(e.NewValue / 100));
        };
        Add(sound, new TextBlock { Text = "This app's volume", Style = (Style)FindResource("T.Body") });
        Add(sound, _volume);
        var system = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Add(system, new TextBlock { Text = "System", Style = (Style)FindResource("T.Secondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        Add(system, IconButton("", "System volume down", () => Win.Tap(Native.VK_VOLUME_DOWN)));
        Add(system, IconButton(Ic.Mute, "System mute", () => Win.Tap(Native.VK_VOLUME_MUTE)));
        Add(system, IconButton(Ic.Volume, "System volume up", () => Win.Tap(Native.VK_VOLUME_UP)));
        Add(sound, system);
    }

    // State ------------------------------------------------------------------------------------
    public void Select(Mirror? mirror, bool precision)
    {
        mirror ??= App.Mirrors.Active;
        if (_mirror is { } old)
            old.PropertyChanged -= OnMirrorChanged;
        _mirror = mirror;
        if (mirror is not null)
            mirror.PropertyChanged += OnMirrorChanged;
        _inspector.DataContext = mirror;
        _body.Visibility = mirror is null ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = mirror is null ? Visibility.Visible : Visibility.Collapsed;
        RebuildChips();
        SyncHeader();
        RebuildThumbs();
        _pixels.Bind(mirror);
        if (precision)
            _pixelMode.IsChecked = true;
        UpdateBanner();
        PlaceStage();
    }

    void OnMirrorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Mirror.Crop) or nameof(Mirror.CropText))
        {
            SyncHeader();
            PlaceStage();
            _pixels.Redraw();
        }
    }

    void SyncHeader()
    {
        _cropInfo.Text = _mirror?.CropText ?? "";
        _undo.IsEnabled = _mirror?.CanUndo == true;
        _redo.IsEnabled = _mirror?.CanRedo == true;
    }

    void RebuildChips()
    {
        _mirrorChips.Children.Clear();
        foreach (var m in App.Mirrors.Items)
        {
            var mirror = m;
            var chip = new RadioButton { Content = mirror.AppName, GroupName = "Mirror", IsChecked = mirror == _mirror, Style = (Style)FindResource("Chip") };
            chip.Click += (_, _) => Select(mirror, false);
            _mirrorChips.Children.Add(chip);
        }
    }

    public void SetActive(bool active)
    {
        if (_active == active)
            return;
        _active = active;
        if (active)
        {
            _timer.Start();
            Select(_mirror ?? App.Mirrors.Active, false);
        }
        else
        {
            _timer.Stop();
            TearDownStage();
        }
    }

    void TearDownStage()
    {
        _dim?.Dispose();
        _bright?.Dispose();
        _dim = _bright = null;
        _surface?.Close();
        _surface = null;
    }

    void RebuildThumbs()
    {
        TearDownStage();
        if (!_active || _mirror is null || _pixelMode.IsChecked == true || Window.GetWindow(this) is not MainWindow main || main.Handle == 0)
            return;
        _dim = DwmThumbnail.Create(main.Handle, _mirror.Source);
        _bright = DwmThumbnail.Create(main.Handle, _mirror.Source);
    }

    public void WindowMoved() => PlaceStage();

    /// <summary>Fits the live preview into the stage, draws the two thumbnails and lays the crop frame over them.</summary>
    void PlaceStage()
    {
        if (!_active || _mirror is not { } m || _pixelMode.IsChecked == true || Window.GetWindow(this) is not MainWindow main || !_stage.IsLoaded || _stage.ActualWidth < 10)
            return;
        if (_dim is null)
            RebuildThumbs();
        if (_dim is null || _bright is null)
            return;
        var (sw, sh) = Win.ClientSize(m.Source);
        if (sw < 2 || sh < 2)
            return;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        const double pad = 10;
        var stage = _stage.TransformToAncestor(main).TransformBounds(new Rect(_stage.RenderSize));
        double k = Math.Min((stage.Width - pad * 2) / sw, (stage.Height - pad * 2) / sh);
        double w = sw * k, h = sh * k;
        var pic = new Rect(stage.X + (stage.Width - w) / 2, stage.Y + (stage.Height - h) / 2, w, h);
        double[] c = m.Crop;

        RECT Px(double l, double t, double r, double b) => new((int)Math.Round(l * dpi), (int)Math.Round(t * dpi), (int)Math.Round(r * dpi), (int)Math.Round(b * dpi));

        _dim.Update(Px(pic.X, pic.Y, pic.Right, pic.Bottom), new RECT(0, 0, sw, sh), 64);
        _bright.Update(
            Px(pic.X + c[0] * w, pic.Y + c[1] * h, pic.X + (c[0] + c[2]) * w, pic.Y + (c[1] + c[3]) * h),
            new RECT((int)Math.Round(c[0] * sw), (int)Math.Round(c[1] * sh), (int)Math.Round((c[0] + c[2]) * sw), (int)Math.Round((c[1] + c[3]) * sh)));

        if (_surface is null)
        {
            _surface = new CropSurface { Owner = main };
            _surface.CropChanged += SyncHeader;
        }
        var origin = _stage.PointToScreen(new Point());
        _surface.Mirror = m;
        _surface.Left = origin.X / dpi;
        _surface.Top = origin.Y / dpi;
        _surface.Width = _stage.ActualWidth;
        _surface.Height = _stage.ActualHeight;
        _surface.Picture = new Rect(pic.X - stage.X, pic.Y - stage.Y, w, h);
        if (!_surface.IsVisible)
            _surface.Show();
        _surface.Redraw();
    }

    void SetPixelMode(bool on)
    {
        if (_pixels is null)
            return;
        _stage.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        _pixels.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _pixelTools.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            TearDownStage();
            _pixels.Refresh();
        }
        else
        {
            PlaceStage();
        }
    }

    void SyncAudio()
    {
        if (_mirror is not { } m || _volume.IsMouseCaptureWithin)
            return;
        var audio = AppAudio.Get(m.Info.ExeName);
        _volume.IsEnabled = audio is not null;
        _volume.Value = (audio?.Volume ?? 1) * 100;
    }

    /// <summary>Largest box of this shape inside the current crop, centred on it.</summary>
    void FitRatio(double ratio)
    {
        if (_mirror is not { } m)
            return;
        var (sw, sh) = m.SourceSize;
        double[] c = m.Crop;
        double w = c[2] * sw, h = c[3] * sh;
        double nw = Math.Min(w, h * ratio), nh = nw / ratio;
        double l = c[0] * sw + (w - nw) / 2, t = c[1] * sh + (h - nh) / 2;
        m.SetCrop(new[] { l / sw, t / sh, nw / sw, nh / sh });
    }

    /// <summary>Watches the window for a few seconds and crops to whatever is moving.</summary>
    async void FindVideo()
    {
        if (_mirror is not { } m || _scanning)
            return;
        _scanning = true;
        _find.IsEnabled = false;
        _find.Content = "Watching…";
        App.Mirrors.Hud.Show("Watching for the playing video…", Ic.Search, seconds: 3.6);
        nint hwnd = m.Source;
        // About 3.5 s of frames, so a camera cut lights up the whole picture rather than just whoever is moving.
        var frames = await Task.Run(async () =>
        {
            var list = new List<Frame>();
            for (int i = 0; i < 12; i++)
            {
                var f = WindowCapture.Snapshot(hwnd);
                if (f is null)
                    return null;
                list.Add(f);
                await Task.Delay(300);
            }
            return list;
        });
        _scanning = false;
        _find.IsEnabled = true;
        _find.Content = "Find video";
        if (_mirror != m)
            return;
        if (frames is null)
        {
            App.Mirrors.Hud.Show("Can't see the window. Is it minimized?", Ic.Warning, seconds: 2.2);
            return;
        }
        var box = await Task.Run(() =>
        {
            var found = SmartCrop.FindMotion(frames);
            if (found is null)
                return null;
            var last = frames[^1];
            int[] e =
            {
                (int)Math.Round(found[0] * last.Width), (int)Math.Round(found[1] * last.Height),
                (int)Math.Round((found[0] + found[2]) * last.Width), (int)Math.Round((found[1] + found[3]) * last.Height),
            };
            e = SmartCrop.RefineToEdges(last, e);
            double sw = last.Width, sh = last.Height;
            return new[] { e[0] / sw, e[1] / sh, (e[2] - e[0]) / sw, (e[3] - e[1]) / sh };
        });
        if (box is null)
        {
            App.Mirrors.Hud.Show("No moving video found. Is it playing?", Ic.Warning, seconds: 2.2);
            return;
        }
        m.SetCrop(box);
        App.Mirrors.Hud.Show("Trimmed to the video", Ic.Crop, m, seconds: 2.2);
    }

    void UpdateBanner()
    {
        string exe = _mirror?.Info.ExeName ?? "";
        string message = "";
        bool canFix = false;
        if (exe == "firefox.exe")
        {
            var state = Firefox.Check(_mirror!.Info.Pid);
            if (state == FirefoxState.NeedsFix)
            {
                message = "Firefox stops drawing when it's fully covered, so this mirror can freeze behind games.";
                canFix = true;
            }
            else if (state == FirefoxState.NeedsRestart)
            {
                message = "Restart Firefox once to finish the fix. Until then the mirror can freeze while it's covered.";
            }
        }
        else if (WindowScanner.Chromium.Contains(exe))
        {
            message = $"{_mirror!.AppName} may pause when it's fully covered. Keep a sliver of it visible if the mirror freezes.";
        }
        _bannerText.Text = message;
        _bannerFix.Visibility = canFix ? Visibility.Visible : Visibility.Collapsed;
        _firefoxBanner.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FixFirefox()
    {
        App.Mirrors.Hud.Show(Firefox.Fix() ? "Firefox fixed. Restart it to apply" : "Couldn't change Firefox's settings", Ic.Check, seconds: 2.5);
        UpdateBanner();
    }
}
