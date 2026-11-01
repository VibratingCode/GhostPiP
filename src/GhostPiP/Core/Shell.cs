using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static GhostPiP.Core.Native;

namespace GhostPiP.Core;

/// <summary>A hidden window that receives hotkeys and tray clicks. Top-level, because Explorer's restart broadcast skips message-only windows.</summary>
public sealed class MessageWindow : IDisposable
{
    readonly HwndSource _source;

    public nint Handle => _source.Handle;
    public event Func<uint, nint, nint, bool>? Message;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("GhostPiP Messages")
        {
            WindowStyle = unchecked((int)WS_POPUP),
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook((nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (Message?.Invoke((uint)msg, wParam, lParam) == true)
                handled = true;
            return 0;
        });
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>The notification-area icon.</summary>
public sealed class TrayIcon : IDisposable
{
    const uint CallbackMessage = WM_APP + 42;

    readonly MessageWindow _window;
    readonly uint _taskbarCreated;
    nint _icon;
    bool _added;

    public event Action? Clicked;
    public event Action<POINT>? MenuRequested;

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _window.Message += OnMessage;
        _icon = AppIcons.Handle(16);
        Add();
    }

    NOTIFYICONDATA Data(string tip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = tip,
        szInfo = "",
        szInfoTitle = "",
    };

    void Add()
    {
        var data = Data("Ghost PiP");
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    public void SetTip(string tip)
    {
        if (!_added)
            return;
        var data = Data(tip.Length > 120 ? tip[..120] : tip);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    bool OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated)
        {
            Add(); // Explorer restarted and forgot about us
            return true;
        }
        if (msg != CallbackMessage)
            return false;
        uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case NIN_SELECT:
            case NIN_KEYSELECT:
                Clicked?.Invoke();
                return true;
            case WM_CONTEXTMENU:
                MenuRequested?.Invoke(Win.Cursor());
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        _window.Message -= OnMessage;
        if (_added)
        {
            var data = Data("");
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }
}

public static class AppIcons
{
    /// <summary>Our own icon from the exe at an exact size. The caller owns the handle.</summary>
    public static nint Handle(int size)
    {
        // The .NET apphost stores ApplicationIcon as resource 32512.
        nint icon = LoadImageW(GetModuleHandleW(null), 32512, 1 /* IMAGE_ICON */, size, size, 0);
        if (icon != 0)
            return icon;
        var small = new nint[1];
        if (Environment.ProcessPath is { } exe && ExtractIconExW(exe, 0, null, small, 1) > 0)
            return small[0];
        return 0;
    }

    public static BitmapSource? FromHandle(nint icon)
    {
        if (icon == 0)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (COMException)
        {
            return null;
        }
    }

    static readonly Dictionary<nint, BitmapSource?> s_byWindow = new();
    static readonly Dictionary<string, BitmapSource?> s_byExe = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A window's own icon, falling back to its exe's. Remembered per window.</summary>
    public static BitmapSource? ForWindow(nint hwnd, string exePath)
    {
        if (s_byWindow.TryGetValue(hwnd, out var known))
            return known;
        BitmapSource? image = null;
        foreach (int which in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeoutW(hwnd, WM_GETICON, which, 0, SMTO_ABORTIFHUNG, 60, out nint res) != 0 && res != 0)
            {
                image = FromHandle(res);
                if (image is not null)
                    break;
            }
        }
        image ??= FromHandle(GetClassLongPtrW(hwnd, GCLP_HICON));
        image ??= FromHandle(GetClassLongPtrW(hwnd, GCLP_HICONSM));
        image ??= ForExe(exePath);
        return s_byWindow[hwnd] = image;
    }

    static BitmapSource? ForExe(string exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return null;
        if (s_byExe.TryGetValue(exePath, out var known))
            return known;
        BitmapSource? image = null;
        var large = new nint[1];
        if (ExtractIconExW(exePath, 0, large, null, 1) > 0 && large[0] != 0)
        {
            image = FromHandle(large[0]);
            DestroyIcon(large[0]);
        }
        return s_byExe[exePath] = image;
    }
}

/// <summary>A second launch hands its arguments to the running copy over a named pipe.</summary>
public sealed class SingleInstance : IDisposable
{
    const string MutexName = "GhostPiP.v2.App";
    const string PipeName = "GhostPiP.v2.Pipe";

    readonly Mutex _mutex;
    readonly CancellationTokenSource _stop = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        IsFirst = created;
    }

    public void Listen(Action<string[]> onArgs)
    {
        Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server);
                    string text = await reader.ReadToEndAsync(_stop.Token);
                    onArgs(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }
        });
    }

    public static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.Write(string.Join('\n', args.Length == 0 ? new[] { "--home" } : args));
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.Dispose();
    }
}
