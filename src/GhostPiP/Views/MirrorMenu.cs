using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GhostPiP.Core;
using GhostPiP.Mirroring;
using static GhostPiP.Core.Native;

namespace GhostPiP.Views;

/// <summary>A small glass menu for the tray icon and right-clicking an unlocked mirror.</summary>
public sealed class MirrorMenu : Window
{
    readonly StackPanel _items = new();

    MirrorMenu()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Border
        {
            Width = 250,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("HairlineStrong"),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x13, 0x1F)),
            Padding = new Thickness(6),
            Child = _items,
        };
        Deactivated += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    void Item(string icon, string text, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = icon, Style = (Style)FindResource("T.Icon"), Width = 24, Foreground = (Brush)FindResource("Text2") });
        row.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("T.Body"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = row,
            Style = (Style)FindResource("Btn"),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 0, 6, 0),
            Focusable = false,
        };
        button.Click += (_, _) =>
        {
            Close();
            action();
        };
        _items.Children.Add(button);
    }

    void Rule() => _items.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4), Background = (Brush)FindResource("Hairline") });

    void Open(POINT at)
    {
        Left = -32000;
        Show();
        UpdateLayout();
        double scale = Math.Max(1, GetDpiForWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle)) / 96.0;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int w = (int)(ActualWidth * scale), h = (int)(ActualHeight * scale);
        var wa = Win.WorkAreaAt(at);
        int x = Math.Clamp(at.X, wa.Left, wa.Right - w);
        int y = Math.Clamp(at.Y - h, wa.Top, wa.Bottom - h);
        SetWindowPos(handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE);
        SetForegroundWindow(handle);
        Activate();
    }

    public static void Show(Mirror mirror, POINT at)
    {
        var menu = new MirrorMenu();
        menu.Item(mirror.Locked ? Ic.Unlock : Ic.Lock, mirror.Locked ? "Unlock" : "Lock", () => MirrorCommands.ToggleLock(mirror));
        menu.Item(Ic.Move, "Next corner", mirror.NextCorner);
        menu.Item(Ic.Crop, "Open Studio", () => App.ShowStudio(mirror));
        menu.Item(Ic.Pip, "Go to source", () => Win.Focus(mirror.Source));
        menu.Rule();
        menu.Item(Ic.Close, "Stop this mirror", () => App.Mirrors.Stop(mirror));
        menu.Open(at);
    }

    public static void ShowApp(POINT at)
    {
        var menu = new MirrorMenu();
        var mirrors = App.Mirrors;
        menu.Item(Ic.Home, "Open Ghost PiP", App.ShowMain);
        foreach (var mirror in mirrors.Items)
            menu.Item(Ic.Close, $"Stop {mirror.AppName}", () => mirrors.Stop(mirror));
        if (mirrors.Items.Count > 0)
            menu.Item(mirrors.GhostHidden ? Ic.View : Ic.Hide, mirrors.GhostHidden ? "Show all mirrors" : "Hide all mirrors", mirrors.ToggleGhost);
        menu.Rule();
        menu.Item(Ic.Close, "Quit", App.Quit);
        menu.Open(at);
    }
}
