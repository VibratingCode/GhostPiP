using System.Windows;
using GhostPiP.Core;
using GhostPiP.Mirroring;
using GhostPiP.Views;
using static GhostPiP.Core.Native;

namespace GhostPiP;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        using var instance = new SingleInstance();
        if (!instance.IsFirst)
            return SingleInstance.Forward(args) ? 0 : 1;
        var app = new App();
        app.InitializeComponent();
        app.Begin(args, instance);
        return app.Run();
    }
}

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static MediaHub Media { get; } = new();
    public static MirrorManager Mirrors { get; private set; } = null!;
    public static HotkeyManager Hotkeys { get; private set; } = null!;

    static MainWindow? s_main;
    static MessageWindow? s_messages;
    static TrayIcon? s_tray;
    static System.Windows.Threading.DispatcherTimer? s_mediaTimer;

    public void Begin(string[] args, SingleInstance instance)
    {
        Settings = SettingsStore.Load(SettingsStore.DataFolder());
        Animator.ReduceMotion = Settings.ReduceMotion;

        Mirrors = new MirrorManager(Settings, new HudWindow(), new ControllerWindow());

        s_messages = new MessageWindow();
        Hotkeys = new HotkeyManager(s_messages.Handle);
        s_messages.Message += (msg, wParam, _) =>
        {
            if (msg != WM_HOTKEY || Hotkeys.ActionFor((int)wParam) is not { } action)
                return false;
            MirrorCommands.Run(action);
            return true;
        };
        ApplyHotkeys();

        s_tray = new TrayIcon(s_messages);
        s_tray.Clicked += ShowMain;
        s_tray.MenuRequested += p => MirrorMenu.ShowApp(p);

        _ = Task.Run(Media.StartAsync);
        s_mediaTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        s_mediaTimer.Tick += (_, _) => _ = Task.Run(Media.RefreshAsync);
        s_mediaTimer.Start();

        instance.Listen(forwarded => Dispatcher.BeginInvoke(() => HandleArgs(forwarded)));
        Dispatcher.BeginInvoke(() => HandleArgs(args));
    }

    public static void ApplyHotkeys()
    {
        Hotkeys.Apply(Settings.Hotkeys);
        if (Hotkeys.Failed.Count > 0)
            Mirrors.Hud.Show($"{Hotkeys.Failed.Count} hotkey(s) already in use by another app", Ic.Warning, seconds: 3.5);
    }

    static void HandleArgs(string[] args)
    {
        if (args.Length == 0)
        {
            ShowMain();
            return;
        }
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--background": break;
                case "--home": ShowMain(); break;
                case "--studio": ShowStudio(Mirrors.Active); break;
                case "--quit": Quit(); break;
                case "--pixel": ShowStudio(Mirrors.Active, precision: true); break;
                case "--unlock": MirrorCommands.ToggleLock(Mirrors.Active); break;
                case "--peek": Mirrors.Controller.Peek(30); break;
                case var _ when arg.StartsWith("--search="):
                    ShowMain();
                    s_main!.Search(arg["--search=".Length..]);
                    break;
                default:
                    nint hwnd = WindowScanner.FindByTitle(arg);
                    if (hwnd != 0)
                        Mirrors.Start(hwnd);
                    else
                        ShowMain();
                    break;
            }
        }
    }

    public static void ShowMain()
    {
        s_main ??= new MainWindow();
        s_main.Present();
    }

    public static void ShowStudio(Mirror? mirror, bool precision = false)
    {
        s_main ??= new MainWindow();
        s_main.Present();
        s_main.OpenStudio(mirror, precision);
    }

    public static void Quit()
    {
        s_mediaTimer?.Stop();
        Mirrors.Shutdown();
        Hotkeys.Clear();
        s_tray?.Dispose();
        s_messages?.Dispose();
        Current.Shutdown();
    }
}
