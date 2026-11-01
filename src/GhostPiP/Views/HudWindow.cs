using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GhostPiP.Core;
using GhostPiP.Mirroring;
using static GhostPiP.Core.Native;

namespace GhostPiP.Views;

/// <summary>Brief click-through feedback ("Opacity 70%"), because the app is usually hidden when you need it.</summary>
public sealed class HudWindow : FloatingWindow
{
    readonly TextBlock _icon = new() { FontSize = 14, Margin = new Thickness(0, 0, 10, 0) };
    readonly TextBlock _text = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    readonly Border _meterTrack = new() { Width = 64, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(12, 0, 0, 0) };
    readonly Border _meterFill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border _pill = new() { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(6) };
    DateTime _until;
    Animator.Tween? _fade;
    bool _showing;

    public HudWindow() : base(clickThrough: true)
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        _icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentSolid");
        _icon.VerticalAlignment = _text.VerticalAlignment = _meterTrack.VerticalAlignment = VerticalAlignment.Center;
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
        _meterTrack.SetResourceReference(Border.BackgroundProperty, "Fill3");
        _meterFill.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
        _meterTrack.Child = _meterFill;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(_text);
        row.Children.Add(_meterTrack);
        _pill.Child = row;
        _pill.SetResourceReference(Border.BackgroundProperty, "GlassFill");
        _pill.SetResourceReference(Border.BorderBrushProperty, "HairlineStrong");
        Content = _pill;
    }

    public void Show(string text, string? icon = null, Mirror? near = null, double? meter = null, double seconds = 1.4)
    {
        _icon.Text = icon ?? "";
        _icon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = text;
        _meterTrack.Visibility = meter is null ? Visibility.Collapsed : Visibility.Visible;
        if (meter is { } m)
            _meterFill.Width = 64 * Math.Clamp(m, 0, 1);

        _fade?.Cancel();
        Opacity = 1;
        if (!_showing)
        {
            _showing = true;
            Show();
        }
        UpdateLayout();
        int w = (int)(ActualWidth * Scale);
        var wa = near is { IsVisibleNow: true } ? Win.WorkAreaOf(near.Rect.ToRECT()) : Win.WorkAreaAt(Win.Cursor());
        int x, y;
        if (near is { IsVisibleNow: true })
        {
            x = near.Rect.X + (near.Rect.W - w) / 2;
            y = near.Rect.Y + (int)(10 * Scale);
        }
        else
        {
            x = (wa.Left + wa.Right - w) / 2;
            y = wa.Top + (int)(48 * Scale);
        }
        x = Math.Clamp(x, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - w - 8));
        SetWindowPos(Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        _until = DateTime.UtcNow.AddSeconds(seconds);
    }

    public void Tick()
    {
        if (!_showing || DateTime.UtcNow < _until)
            return;
        _showing = false;
        _fade = Animator.Run(200, t => Opacity = 1 - t, Ease.OutCubic, Hide);
    }
}
