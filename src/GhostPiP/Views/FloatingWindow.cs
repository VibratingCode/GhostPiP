using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GhostPiP.Core;
using GhostPiP.Mirroring;
using static GhostPiP.Core.Native;

namespace GhostPiP.Views;

/// <summary>A borderless, per-pixel-alpha, always-on-top window that never takes focus from your game.</summary>
public class FloatingWindow : Window
{
    readonly bool _clickThrough;

    public nint Handle { get; private set; }
    public bool IsReady => Handle != 0;

    public FloatingWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        Left = Top = -32000;
        Handle = new WindowInteropHelper(this).EnsureHandle();
        Win.AddExStyle(Handle, add: WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (_clickThrough ? WS_EX_TRANSPARENT : 0));
    }

    public double Scale => Math.Max(1, GetDpiForWindow(Handle)) / 96.0;

    public void SetExcludedFromCapture(bool excluded) => Win.SetExcludedFromCapture(Handle, excluded);
}

/// <summary>Soft shadow and unlock glow around a mirror. A ring only: the middle is cut out so a translucent mirror stays clean.</summary>
public sealed class HaloWindow : FloatingWindow
{
    const double MarginDip = 44;
    readonly System.Windows.Controls.Grid _root = new();
    readonly System.Windows.Controls.Border _shadow = new() { Background = Brushes.Black };
    readonly System.Windows.Controls.Border _glow = new() { BorderThickness = new Thickness(2) };
    (int W, int H, double Radius) _shape;

    public HaloWindow() : base(clickThrough: true)
    {
        _shadow.Effect = new DropShadowEffect { BlurRadius = 38, ShadowDepth = 9, Direction = 270, Opacity = 0.55, Color = Colors.Black };
        _glow.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "AccentGradient");
        _glow.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9, Color = Color.FromRgb(0x9B, 0xC6, 0xFF) };
        _root.Children.Add(_shadow);
        _root.Children.Add(_glow);
        Content = _root;
    }

    public nint Defer(nint batch, PxRect r, double dpi)
    {
        int m = (int)Math.Round(MarginDip * dpi);
        return DeferWindowPos(batch, Handle, 0, r.X - m, r.Y - m, r.W + m * 2, r.H + m * 2, SWP_NOACTIVATE | SWP_NOZORDER);
    }

    public void Sync(bool wanted, PxRect r, double dpi, string corners, bool shadow, bool glow, double strength, nint overlay)
    {
        if (!wanted)
        {
            if (IsVisible)
                Hide();
            return;
        }
        double radius = corners switch { "square" => 0, "small" => 4, _ => 8 };
        _shadow.Visibility = shadow ? Visibility.Visible : Visibility.Hidden;
        _glow.Visibility = glow ? Visibility.Visible : Visibility.Hidden;
        if (Math.Abs(_root.Opacity - strength) > 0.03)
            _root.Opacity = strength;

        int m = (int)Math.Round(MarginDip * dpi);
        if (!IsVisible)
        {
            Show();
            SetWindowPos(Handle, HWND_TOPMOST, r.X - m, r.Y - m, r.W + m * 2, r.H + m * 2, SWP_NOACTIVATE);
        }
        if (_shape != (r.W, r.H, radius))
        {
            _shape = (r.W, r.H, radius);
            double w = r.W / dpi, h = r.H / dpi;
            _shadow.Width = w;
            _shadow.Height = h;
            _shadow.CornerRadius = new CornerRadius(radius);
            _glow.Width = w + 4;
            _glow.Height = h + 4;
            _glow.CornerRadius = new CornerRadius(radius + 2);
            double total = MarginDip * 2;
            _root.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, w + total, h + total)),
                new RectangleGeometry(new Rect(MarginDip, MarginDip, w, h), radius, radius));
        }
    }
}
