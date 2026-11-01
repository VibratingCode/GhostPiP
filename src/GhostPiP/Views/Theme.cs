using System.Windows;
using System.Windows.Media;

namespace GhostPiP.Views;

/// <summary>Segoe Fluent Icons glyphs, by what they mean here.</summary>
public static class Ic
{
    public const string Play = "", Pause = "", Previous = "", Next = "",
        Rewind = "", Forward = "", Lock = "", Unlock = "", Crop = "",
        Settings = "", Home = "", Close = "", Search = "", Refresh = "",
        Volume = "", Mute = "", Warning = "", Pip = "", Opacity = "",
        Move = "", Undo = "", Redo = "", Check = "", ZoomIn = "",
        ZoomOut = "", Ruler = "", Target = "", Keyboard = "", Hide = "", View = "";
}

public static class Theme
{
    /// <summary>The accent's solid colour as a Win32 COLORREF, for the DWM window border.</summary>
    public static uint AccentColorRef
    {
        get
        {
            var color = Application.Current?.TryFindResource("AccentSolidColor") is Color c ? c : Color.FromRgb(0xB9, 0xA9, 0xFF);
            return (uint)(color.R | (color.G << 8) | (color.B << 16));
        }
    }
}

/// <summary>Lets a slider be dragged from anywhere on its track, in one press, and nudged with the wheel.</summary>
public static class SliderAssist
{
    public static readonly DependencyProperty EnhancedProperty = DependencyProperty.RegisterAttached(
        "Enhanced", typeof(bool), typeof(SliderAssist), new PropertyMetadata(false, OnChanged));

    public static bool GetEnhanced(DependencyObject o) => (bool)o.GetValue(EnhancedProperty);
    public static void SetEnhanced(DependencyObject o, bool value) => o.SetValue(EnhancedProperty, value);

    static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not System.Windows.Controls.Slider slider || e.NewValue is not true)
            return;
        void Apply(System.Windows.Input.MouseEventArgs args)
        {
            double inset = 7, width = Math.Max(1, slider.ActualWidth - inset * 2);
            double f = Math.Clamp((args.GetPosition(slider).X - inset) / width, 0, 1);
            slider.Value = slider.Minimum + f * (slider.Maximum - slider.Minimum);
        }
        slider.PreviewMouseLeftButtonDown += (_, args) => { slider.CaptureMouse(); Apply(args); args.Handled = true; };
        slider.PreviewMouseMove += (_, args) => { if (slider.IsMouseCaptured) Apply(args); };
        slider.PreviewMouseLeftButtonUp += (_, args) => { slider.ReleaseMouseCapture(); args.Handled = true; };
        slider.PreviewMouseWheel += (_, args) =>
        {
            slider.Value += (args.Delta > 0 ? 1 : -1) * (slider.Maximum - slider.Minimum) / 20;
            args.Handled = true;
        };
    }
}
