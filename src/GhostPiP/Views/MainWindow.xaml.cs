using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GhostPiP.Core;
using GhostPiP.Mirroring;
using static GhostPiP.Core.Native;

namespace GhostPiP.Views;

public partial class MainWindow : Window
{
    public nint Handle { get; private set; }
    bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        var icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/GhostPiP.ico"));
        Icon = icon;
        var logo = new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/GhostPiP.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Logo.Source = logo.Frames.OrderByDescending(f => f.PixelWidth).First();
        SourceInitialized += (_, _) => InitBackdrop();
        NavWindows.Checked += (_, _) => ShowPage(Gallery);
        NavStudio.Checked += (_, _) => ShowPage(Studio);
        NavKeys.Checked += (_, _) => ShowPage(Keys);
        NavSettings.Checked += (_, _) => ShowPage(SettingsView);
        Closing += OnClosing;
        IsVisibleChanged += (_, _) => SyncPages();
        LocationChanged += (_, _) => Studio.WindowMoved();
        StateChanged += (_, _) =>
        {
            // A maximized WindowChrome window overhangs the screen by its resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            SyncPages();
        };
        App.Mirrors.Items.CollectionChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    void InitBackdrop()
    {
        Handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(Handle).CompositionTarget.BackgroundColor = Colors.Transparent;
        int dark = 1;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
        }
        else
        {
            Root.Background = new SolidColorBrush(Color.FromRgb(0x12, 0x0F, 0x1A));
        }
    }

    void UpdateCount()
    {
        int n = App.Mirrors.Items.Count;
        FloatingCount.Text = n == 0 ? "" : $"{n} floating";
    }

    void ShowPage(FrameworkElement page)
    {
        foreach (FrameworkElement p in new FrameworkElement[] { Gallery, Studio, Keys, SettingsView })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        SyncPages();
    }

    /// <summary>Live thumbnails sit above all WPF content, so a hidden page must take its thumbnails down.</summary>
    void SyncPages()
    {
        bool shown = IsVisible && WindowState != WindowState.Minimized;
        Gallery.SetActive(shown && Gallery.Visibility == Visibility.Visible);
        Studio.SetActive(shown && Studio.Visibility == Visibility.Visible);
    }

    public void Present()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Win.ReleaseModifiers();
        Activate();
        SetForegroundWindow(Handle);
    }

    public void OpenStudio(Mirror? mirror, bool precision)
    {
        Studio.Select(mirror, precision);
        NavStudio.IsChecked = true;
    }

    public void Search(string text)
    {
        NavWindows.IsChecked = true;
        Gallery.SetSearch(text);
    }

    public void BackgroundAfterStart() => Hide();

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting)
            return;
        if (App.Mirrors.Items.Count > 0 || App.Settings.KeepInTray)
        {
            e.Cancel = true;
            Hide();
            App.Mirrors.Hud.Show("Still running in the tray. Ctrl+Alt+H brings this back", Ic.Pip, seconds: 2.5);
            return;
        }
        _quitting = true;
        App.Quit();
    }
}
