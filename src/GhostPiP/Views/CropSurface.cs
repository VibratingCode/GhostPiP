using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GhostPiP.Core;
using GhostPiP.Mirroring;

namespace GhostPiP.Views;

/// <summary>
/// The crop frame and its handles. DWM draws thumbnails above all of a window's own content, so anything
/// that must appear on top of the live preview has to be a separate window sitting over it.
/// </summary>
public sealed class CropSurface : Window
{
    const double MinFraction = 0.05;

    readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
    readonly Rectangle _frame = new() { StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly Rectangle[] _grips = new Rectangle[8];
    readonly Line[] _thirds = new Line[4];
    readonly Border _tag = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), IsHitTestVisible = false };
    readonly TextBlock _tagText = new() { FontSize = 11 };

    enum Mode { None, Move, Edge, Draw }
    Mode _mode;
    Point _start;
    double[] _startCrop = { 0, 0, 1, 1 };
    Point _anchor;

    public Mirror? Mirror { get; set; }
    public Rect Picture { get; set; }   // where the source is drawn inside this window, in DIPs
    public event Action? CropChanged;

    public CropSurface()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Left = Top = -32000;
        Width = Height = 10;
        _frame.SetResourceReference(Shape.StrokeProperty, "AccentSolid");
        _canvas.Children.Add(_frame);
        for (int i = 0; i < _thirds.Length; i++)
        {
            _thirds[i] = new Line { Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _canvas.Children.Add(_thirds[i]);
        }
        for (int i = 0; i < _grips.Length; i++)
        {
            _grips[i] = new Rectangle { RadiusX = 2, RadiusY = 2, Fill = Brushes.White, IsHitTestVisible = false };
            _canvas.Children.Add(_grips[i]);
        }
        _tag.Background = new SolidColorBrush(Color.FromArgb(230, 0x16, 0x13, 0x1F));
        _tagText.Foreground = Brushes.White;
        _tag.Child = _tagText;
        _canvas.Children.Add(_tag);
        Content = _canvas;

        MouseLeftButtonDown += Press;
        MouseMove += Motion;
        MouseLeftButtonUp += (_, _) =>
        {
            _mode = Mode.None;
            ReleaseMouseCapture();
            ShowThirds(false);
        };
        SourceInitialized += (_, _) =>
            Win.AddExStyle(new System.Windows.Interop.WindowInteropHelper(this).Handle, add: Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
    }

    (double L, double T, double R, double B) FrameRect()
    {
        double[] c = Mirror?.Crop ?? new double[] { 0, 0, 1, 1 };
        return (Picture.X + c[0] * Picture.Width, Picture.Y + c[1] * Picture.Height,
            Picture.X + (c[0] + c[2]) * Picture.Width, Picture.Y + (c[1] + c[3]) * Picture.Height);
    }

    public void Redraw()
    {
        var (l, t, r, b) = FrameRect();
        Canvas.SetLeft(_frame, l);
        Canvas.SetTop(_frame, t);
        _frame.Width = Math.Max(1, r - l);
        _frame.Height = Math.Max(1, b - t);

        double mx = (l + r) / 2, my = (t + b) / 2;
        (double X, double Y, double W, double H)[] spots =
        {
            (l, t, 10, 10), (r, t, 10, 10), (l, b, 10, 10), (r, b, 10, 10),
            (mx, t, 22, 5), (mx, b, 22, 5), (l, my, 5, 22), (r, my, 5, 22),
        };
        for (int i = 0; i < spots.Length; i++)
        {
            var (x, y, w, h) = spots[i];
            _grips[i].Width = w;
            _grips[i].Height = h;
            Canvas.SetLeft(_grips[i], x - w / 2);
            Canvas.SetTop(_grips[i], y - h / 2);
        }
        double tx = r - l, ty = b - t;
        (double X1, double Y1, double X2, double Y2)[] lines =
        {
            (l + tx / 3, t, l + tx / 3, b), (l + tx * 2 / 3, t, l + tx * 2 / 3, b),
            (l, t + ty / 3, r, t + ty / 3), (l, t + ty * 2 / 3, r, t + ty * 2 / 3),
        };
        for (int i = 0; i < lines.Length; i++)
        {
            _thirds[i].X1 = lines[i].X1;
            _thirds[i].Y1 = lines[i].Y1;
            _thirds[i].X2 = lines[i].X2;
            _thirds[i].Y2 = lines[i].Y2;
        }
        _tagText.Text = Mirror?.CropText ?? "";
        Canvas.SetLeft(_tag, l + 8);
        Canvas.SetTop(_tag, t + 8);
    }

    void ShowThirds(bool on)
    {
        foreach (var line in _thirds)
            line.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    string Hit(Point p)
    {
        var (l, t, r, b) = FrameRect();
        const double tol = 9;
        bool nearL = Math.Abs(p.X - l) <= tol, nearR = Math.Abs(p.X - r) <= tol;
        bool nearT = Math.Abs(p.Y - t) <= tol, nearB = Math.Abs(p.Y - b) <= tol;
        bool inY = p.Y >= t - tol && p.Y <= b + tol, inX = p.X >= l - tol && p.X <= r + tol;
        string hit = "";
        if (nearL && inY) hit += "l";
        else if (nearR && inY) hit += "r";
        if (nearT && inX) hit += "t";
        else if (nearB && inX) hit += "b";
        return hit;
    }

    void Press(object sender, MouseButtonEventArgs e)
    {
        if (Mirror is null)
            return;
        var p = e.GetPosition(_canvas);
        _start = p;
        _startCrop = (double[])Mirror.Crop.Clone();
        string hit = Hit(p);
        var (l, t, r, b) = FrameRect();
        if (hit.Length > 0)
        {
            _mode = Mode.Edge;
            _hit = hit;
        }
        else if (p.X > l && p.X < r && p.Y > t && p.Y < b)
        {
            _mode = Mode.Move;
        }
        else
        {
            _mode = Mode.Draw;
            _anchor = Clamp(p);
        }
        ShowThirds(true);
        CaptureMouse();
    }

    string _hit = "";

    Point Clamp(Point p) => new(
        Math.Clamp(p.X, Picture.Left, Picture.Right),
        Math.Clamp(p.Y, Picture.Top, Picture.Bottom));

    void Motion(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_mode == Mode.None)
        {
            string hit = Hit(p);
            var (l0, t0, r0, b0) = FrameRect();
            Cursor = hit switch
            {
                "lt" or "rb" => Cursors.SizeNWSE,
                "lb" or "rt" => Cursors.SizeNESW,
                "l" or "r" => Cursors.SizeWE,
                "t" or "b" => Cursors.SizeNS,
                _ => p.X > l0 && p.X < r0 && p.Y > t0 && p.Y < b0 ? Cursors.SizeAll : Cursors.Cross,
            };
            return;
        }
        if (Mirror is null || Picture.Width <= 0)
            return;

        double[] s = _startCrop;
        double l = s[0], t = s[1], w = s[2], h = s[3];
        if (_mode == Mode.Move)
        {
            l = Math.Clamp(l + (p.X - _start.X) / Picture.Width, 0, 1 - w);
            t = Math.Clamp(t + (p.Y - _start.Y) / Picture.Height, 0, 1 - h);
        }
        else if (_mode == Mode.Edge)
        {
            double r = l + w, b = t + h;
            double nx = Math.Clamp((p.X - Picture.X) / Picture.Width, 0, 1);
            double ny = Math.Clamp((p.Y - Picture.Y) / Picture.Height, 0, 1);
            if (_hit.Contains('l')) l = Math.Min(nx, r - MinFraction);
            if (_hit.Contains('r')) r = Math.Max(nx, l + MinFraction);
            if (_hit.Contains('t')) t = Math.Min(ny, b - MinFraction);
            if (_hit.Contains('b')) b = Math.Max(ny, t + MinFraction);
            w = r - l;
            h = b - t;
        }
        else
        {
            var q = Clamp(p);
            w = Math.Max(Math.Abs(q.X - _anchor.X) / Picture.Width, MinFraction);
            h = Math.Max(Math.Abs(q.Y - _anchor.Y) / Picture.Height, MinFraction);
            l = Math.Min((Math.Min(q.X, _anchor.X) - Picture.X) / Picture.Width, 1 - w);
            t = Math.Min((Math.Min(q.Y, _anchor.Y) - Picture.Y) / Picture.Height, 1 - h);
        }
        Mirror.SetCrop(new[] { Math.Round(l, 5), Math.Round(t, 5), Math.Round(w, 5), Math.Round(h, 5) });
        Redraw();
        CropChanged?.Invoke();
    }
}
