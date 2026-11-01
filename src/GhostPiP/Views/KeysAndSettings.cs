using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostPiP.Core;

namespace GhostPiP.Views;

/// <summary>Every global hotkey, click-to-rebind.</summary>
public sealed class KeysPage : UserControl
{
    readonly StackPanel _list = new();
    HotkeyDef? _recording;

    public KeysPage()
    {
        var root = new DockPanel { Margin = new Thickness(0, 26, 0, 0) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var reset = new Button { Content = "Reset all", Style = (Style)FindResource("Btn") };
        reset.Click += (_, _) =>
        {
            App.Settings.Hotkeys.Clear();
            Commit();
        };
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = "Keys", Style = (Style)FindResource("T.Display") });
        words.Children.Add(new TextBlock { Text = "They work from anywhere, even in a game. Click one, then press the new combination. Esc cancels.", Style = (Style)FindResource("T.Secondary"), Margin = new Thickness(0, 3, 0, 0) });
        head.Children.Add(words);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = root;
        Focusable = true;
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => Build();
    }

    void Build()
    {
        _list.Children.Clear();
        foreach (var group in HotkeyManager.All.GroupBy(d => d.Group))
        {
            _list.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("T.Section"), Margin = new Thickness(0, 8, 0, 6) });
            var card = new StackPanel();
            _list.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 8), Child = card });
            foreach (var def in group)
                card.Children.Add(Row(def));
        }
    }

    FrameworkElement Row(HotkeyDef def)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal };
        if (_recording == def)
        {
            caps.Children.Add(new TextBlock { Text = "Press keys…", Style = (Style)FindResource("T.Body"), Foreground = (Brush)FindResource("AccentSolid") });
        }
        else
        {
            foreach (string part in HotkeyManager.ChordFor(def, App.Settings.Hotkeys).Parts())
                caps.Children.Add(new Border { Style = (Style)FindResource("Keycap"), Child = new TextBlock { Text = part, Style = (Style)FindResource("T.Caption"), Foreground = (Brush)FindResource("Text1") } });
        }
        var row = new DockPanel();
        DockPanel.SetDock(caps, Dock.Right);
        row.Children.Add(caps);
        if (App.Hotkeys.Failed.Contains(def.Action))
        {
            var taken = new TextBlock { Text = "in use by another app", Style = (Style)FindResource("T.Caption"), Foreground = (Brush)FindResource("Warn"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(taken, Dock.Right);
            row.Children.Add(taken);
        }
        row.Children.Add(new TextBlock { Text = def.Label, Style = (Style)FindResource("T.Body"), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = row, Style = (Style)FindResource("Btn"), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 0, 8, 0), MinHeight = 34 };
        button.Template = StretchTemplate(button);
        button.Click += (_, _) =>
        {
            _recording = def;
            App.Hotkeys.Clear(); // or the combination being pressed would just fire
            Build();
            Focus();
        };
        return button;
    }

    static ControlTemplate StretchTemplate(Button _)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 8, 7));
        border.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(content);
        template.VisualTree = border;
        return template;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_recording is not { } def)
            return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _recording = null;
            Commit();
            return;
        }
        if (KeyChord.FromKeyEvent(e) is { } chord)
        {
            App.Settings.Hotkeys[def.Action.ToString()] = chord.ToString();
            _recording = null;
            Commit();
        }
    }

    void Commit()
    {
        App.ApplyHotkeys();
        App.Mirrors.Dirty();
        Build();
    }
}

/// <summary>App-wide preferences.</summary>
public sealed class SettingsPage : UserControl
{
    public SettingsPage()
    {
        var list = new StackPanel { Margin = new Thickness(0, 26, 10, 0) };
        list.Children.Add(new TextBlock { Text = "Settings", Style = (Style)FindResource("T.Display"), Margin = new Thickness(0, 0, 0, 14) });

        var s = App.Settings;
        var general = Section(list, "General");
        general.Children.Add(Toggle("Start with Windows", "Runs quietly in the tray, hotkeys ready.", s.StartWithWindows, v =>
        {
            s.StartWithWindows = v;
            Autostart.Set(v);
        }));
        general.Children.Add(Toggle("Keep running in the tray when closed", "Otherwise closing the window quits, unless something is floating.", s.KeepInTray, v => s.KeepInTray = v));
        general.Children.Add(Toggle("Reduce motion", "Turns off the glides and fly-ins.", s.ReduceMotion, v =>
        {
            s.ReduceMotion = v;
            Animator.ReduceMotion = v;
        }));

        var controller = Section(list, "Controller");
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in new[] { ("Auto-hide", "hover"), ("Always", "always"), ("Off", "off") })
        {
            var chip = new RadioButton { Content = label, GroupName = "Controller", IsChecked = s.Controller == value, Style = (Style)FindResource("Chip") };
            chip.Checked += (_, _) =>
            {
                s.Controller = value;
                App.Mirrors.Dirty();
            };
            modes.Children.Add(chip);
        }
        controller.Children.Add(modes);

        var about = Section(list, "About");
        about.Children.Add(new TextBlock { Text = $"Ghost PiP {typeof(App).Assembly.GetName().Version?.ToString(3)}", Style = (Style)FindResource("T.Body") });
        about.Children.Add(new TextBlock { Text = "Your settings live in " + Path.GetDirectoryName(s.Path), Style = (Style)FindResource("T.Secondary"), Margin = new Thickness(0, 4, 0, 10) });
        var links = new StackPanel { Orientation = Orientation.Horizontal };
        var folder = new Button { Content = "Open settings folder", Style = (Style)FindResource("Btn"), Margin = new Thickness(0, 0, 6, 0) };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(s.Path)}\"") { UseShellExecute = true });
        var github = new Button { Content = "GitHub", Style = (Style)FindResource("Btn") };
        github.Click += (_, _) => Process.Start(new ProcessStartInfo("https://github.com/VibratingCode/GhostPiP") { UseShellExecute = true });
        links.Children.Add(folder);
        links.Children.Add(github);
        about.Children.Add(links);

        Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    StackPanel Section(Panel list, string title)
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("T.Section"), Margin = new Thickness(0, 0, 0, 10) });
        list.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 10), Child = inner });
        return inner;
    }

    FrameworkElement Toggle(string label, string hint, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value };
        box.Checked += (_, _) => { changed(true); App.Mirrors.Dirty(); };
        box.Unchecked += (_, _) => { changed(false); App.Mirrors.Dirty(); };
        var words = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        words.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("T.Body") });
        words.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("T.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(box);
        row.Children.Add(words);
        return row;
    }
}
