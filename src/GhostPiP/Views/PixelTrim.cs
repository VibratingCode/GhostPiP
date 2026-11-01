using Frame = GhostPiP.Core.Frame;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostPiP.Core;
using GhostPiP.Mirroring;

namespace GhostPiP.Views;

/// <summary>One magnified view of a cut line: nearest-neighbour pixels, dimmed where they're trimmed away.</summary>
public sealed class Loupe : FrameworkElement
{
    public Side Side { get; init; }
    public PixelTrim Owner { get; init; } = null!;
    public bool Active { get; set; }
    int _x0, _y0;

    public Loupe()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double W = ActualWidth, H = ActualHeight;
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        if (Owner.Picture is not { } image || Owner.Snap is null)
            return;
        int z = Owner.Zoom;
        int nx = (int)Math.Ceiling(W / z), ny = (int)Math.Ceiling(H / z);
        var e = Owner.Edges();
        int line = PixelTrim.LineOf(Side, e);
        bool across = Side is Side.Top or Side.Bottom;
        _x0 = across ? (e[0] + e[2]) / 2 - nx / 2 : line - nx / 2;
        _y0 = across ? line - ny / 2 : (e[1] + e[3]) / 2 - ny / 2;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));
        dc.DrawImage(image, new Rect(-_x0 * z, -_y0 * z, image.PixelWidth * (double)z, image.PixelHeight * (double)z));
        var dim = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0));
        double kl = (e[0] - _x0) * z, kt = (e[1] - _y0) * z, kr = (e[2] - _x0) * z, kb = (e[3] - _y0) * z;
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, W, H)),
            new RectangleGeometry(new Rect(new Point(kl, kt), new Point(kr, kb))));
        dc.DrawGeometry(dim, null, outside);
        if (z >= 8)
        {
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
            for (int x = 0; x <= nx; x++)
                dc.DrawLine(grid, new Point(x * z + 0.5, 0), new Point(x * z + 0.5, H));
            for (int y = 0; y <= ny; y++)
                dc.DrawLine(grid, new Point(0, y * z + 0.5), new Point(W, y * z + 0.5));
        }
        var cut = new Pen((Brush)FindResource("AccentSolid"), 2);
        if (across)
            dc.DrawLine(cut, new Point(0, (line - _y0) * z), new Point(W, (line - _y0) * z));
        else
            dc.DrawLine(cut, new Point((line - _x0) * z, 0), new Point((line - _x0) * z, H));
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Cut(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            Cut(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    void Cut(Point p)
    {
        Owner.SetActive(Side);
        int z = Owner.Zoom;
        Owner.SetLine(Side, Side is Side.Top or Side.Bottom ? _y0 + (int)Math.Round(p.Y / z) : _x0 + (int)Math.Round(p.X / z));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Owner.SetActive(Side);
        Owner.SetLine(Side, PixelTrim.LineOf(Side, Owner.Edges()) + (e.Delta > 0 ? -1 : 1));
        e.Handled = true;
    }
}

/// <summary>Pixel-exact cuts: a loupe on each edge, typed values, arrow-key nudges, snap and shave.</summary>
public sealed class PixelTrim : UserControl
{
    const int MinPx = 16;
    static readonly Side[] Sides = { Side.Top, Side.Bottom, Side.Left, Side.Right };

    readonly Dictionary<Side, Loupe> _loupes = new();
    readonly Dictionary<Side, TextBox> _fields = new();
    readonly Dictionary<Side, TextBlock> _heads = new();
    Mirror? _mirror;
    Side _active = Side.Top;

    public int Zoom { get; private set; } = 8;
    public Frame? Snap { get; private set; }
    public System.Windows.Media.Imaging.BitmapSource? Picture { get; private set; }

    public PixelTrim()
    {
        var grid = new Grid();
        for (int i = 0; i < 2; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int i = 0; i < Sides.Length; i++)
        {
            var cell = MakeCell(Sides[i]);
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
        Content = grid;
        Focusable = true;
        PreviewKeyDown += OnKey;
    }

    FrameworkElement MakeCell(Side side)
    {
        var head = new TextBlock { Style = (Style)FindResource("T.Section"), FontSize = 12, Margin = new Thickness(0, 0, 0, 5) };
        _heads[side] = head;
        var snap = new Button { Content = "Snap", Style = (Style)FindResource("Btn"), MinHeight = 24, FontSize = 11, Padding = new Thickness(9, 0, 9, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        snap.Click += (_, _) => SnapSide(side, quiet: false);
        var loupe = new Loupe { Side = side, Owner = this };
        _loupes[side] = loupe;
        var well = new Border { Child = loupe, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("HairlineStrong"), ClipToBounds = true };

        var field = new TextBox { Width = 60, MinHeight = 26, TextAlignment = TextAlignment.Center };
        field.GotKeyboardFocus += (_, _) => SetActive(side);
        field.LostKeyboardFocus += (_, _) => Typed(side);
        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Typed(side); e.Handled = true; } };
        _fields[side] = field;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (int d in new[] { -10, -1 })
            row.Children.Add(Nudger(side, d));
        row.Children.Add(field);
        row.Children.Add(new TextBlock { Text = "px", Style = (Style)FindResource("T.Caption"), Margin = new Thickness(5, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (int d in new[] { 1, 10 })
            row.Children.Add(Nudger(side, d));

        var cell = new DockPanel { Margin = new Thickness(5) };
        var top = new Grid();
        top.Children.Add(head);
        top.Children.Add(snap);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(row, Dock.Bottom);
        cell.Children.Add(top);
        cell.Children.Add(row);
        cell.Children.Add(well);
        return cell;
    }

    Button Nudger(Side side, int amount)
    {
        var b = new Button { Content = amount > 0 ? $"+{amount}" : $"−{-amount}", Style = (Style)FindResource("Btn"), MinHeight = 26, FontSize = 11, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 0), Focusable = false };
        b.Click += (_, _) => Nudge(side, amount);
        return b;
    }

    public void Bind(Mirror? mirror)
    {
        _mirror = mirror;
        Refresh();
    }

    /// <summary>Takes a fresh snapshot of the source, off the UI thread because a busy app can take a moment.</summary>
    public async void Refresh()
    {
        if (_mirror is not { } m)
            return;
        nint hwnd = m.Source;
        var frame = await Task.Run(() => WindowCapture.Snapshot(hwnd));
        if (_mirror != m)
            return;
        if (frame is null)
        {
            App.Mirrors.Hud.Show("No snapshot. Is the window minimized?", Ic.Warning, seconds: 2);
            return;
        }
        Snap = frame;
        Picture = frame.ToBitmap();
        Redraw();
    }

    public void SetZoom(int zoom)
    {
        Zoom = zoom;
        Redraw();
    }

    public int[] Edges()
    {
        var (sw, sh) = Snap is { } f ? (f.Width, f.Height) : (1, 1);
        double[] c = _mirror?.Crop ?? new double[] { 0, 0, 1, 1 };
        return new[] { (int)Math.Round(c[0] * sw), (int)Math.Round(c[1] * sh), (int)Math.Round((c[0] + c[2]) * sw), (int)Math.Round((c[1] + c[3]) * sh) };
    }

    public static int LineOf(Side side, int[] e) => side switch
    {
        Side.Left => e[0],
        Side.Top => e[1],
        Side.Right => e[2],
        _ => e[3],
    };

    public void SetLine(Side side, int line)
    {
        if (Snap is null || _mirror is null)
            return;
        var e = Edges();
        switch (side)
        {
            case Side.Top: e[1] = Math.Clamp(line, 0, e[3] - MinPx); break;
            case Side.Bottom: e[3] = Math.Clamp(line, e[1] + MinPx, Snap.Height); break;
            case Side.Left: e[0] = Math.Clamp(line, 0, e[2] - MinPx); break;
            default: e[2] = Math.Clamp(line, e[0] + MinPx, Snap.Width); break;
        }
        Commit(e);
    }

    void Commit(int[] e)
    {
        if (Snap is null || _mirror is null)
            return;
        double sw = Snap.Width, sh = Snap.Height;
        _mirror.SetCrop(new[] { e[0] / sw, e[1] / sh, (e[2] - e[0]) / sw, (e[3] - e[1]) / sh });
        Redraw();
    }

    void Nudge(Side side, int cut)
    {
        SetActive(side);
        int line = LineOf(side, Edges());
        SetLine(side, side is Side.Top or Side.Left ? line + cut : line - cut);
    }

    void Typed(Side side)
    {
        if (Snap is null)
            return;
        if (!int.TryParse(_fields[side].Text.Trim(), out int amount))
        {
            Redraw();
            return;
        }
        SetLine(side, side switch
        {
            Side.Bottom => Snap.Height - amount,
            Side.Right => Snap.Width - amount,
            _ => amount,
        });
    }

    public bool SnapSide(Side side, bool quiet)
    {
        if (Snap is null)
            return false;
        var e = Edges();
        bool across = side is Side.Top or Side.Bottom;
        int? found = SmartCrop.StrongestEdge(Snap, side, LineOf(side, e), across ? e[0] : e[1], across ? e[2] : e[3]);
        if (found is null)
        {
            if (!quiet)
                App.Mirrors.Hud.Show($"No clear edge near the {side.ToString().ToLowerInvariant()} cut", Ic.Warning);
            return false;
        }
        SetActive(side);
        SetLine(side, found.Value);
        return true;
    }

    public void SnapAll()
    {
        int hits = Sides.Count(s => SnapSide(s, quiet: true));
        App.Mirrors.Hud.Show(hits > 0 ? $"Snapped {hits} of 4 edges" : "No clear edges nearby", hits > 0 ? Ic.Target : Ic.Warning);
    }

    public void ShaveAll()
    {
        if (Snap is null)
            return;
        int hits = 0;
        foreach (var side in Sides)
        {
            if (SmartCrop.ShaveFlat(Snap, side, Edges(), MinPx) is { } line)
            {
                SetLine(side, line);
                hits++;
            }
        }
        App.Mirrors.Hud.Show(hits > 0 ? $"Shaved {hits} flat edge{(hits == 1 ? "" : "s")}" : "No flat bars to shave", hits > 0 ? Ic.Crop : Ic.Warning);
    }

    public void SetActive(Side side)
    {
        _active = side;
        foreach (var s in Sides)
            _heads[s].Foreground = (Brush)FindResource(s == side ? "AccentSolid" : "Text1");
    }

    public void Redraw()
    {
        if (Snap is null)
            return;
        var e = Edges();
        foreach (var side in Sides)
        {
            _heads[side].Text = side switch
            {
                Side.Top => $"Top   y = {e[1]}",
                Side.Bottom => $"Bottom   y = {e[3]}",
                Side.Left => $"Left   x = {e[0]}",
                _ => $"Right   x = {e[2]}",
            };
            if (!_fields[side].IsKeyboardFocused)
            {
                _fields[side].Text = (side switch
                {
                    Side.Top => e[1],
                    Side.Bottom => Snap.Height - e[3],
                    Side.Left => e[0],
                    _ => Snap.Width - e[2],
                }).ToString();
            }
            _loupes[side].InvalidateVisual();
        }
        SetActive(_active);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        bool inField = Keyboard.FocusedElement is TextBox;
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        int line = LineOf(_active, Edges());
        switch (e.Key)
        {
            case Key.Up when _active is Side.Top or Side.Bottom: SetLine(_active, line - step); break;
            case Key.Down when _active is Side.Top or Side.Bottom: SetLine(_active, line + step); break;
            case Key.Left when !inField && _active is Side.Left or Side.Right: SetLine(_active, line - step); break;
            case Key.Right when !inField && _active is Side.Left or Side.Right: SetLine(_active, line + step); break;
            case Key.Tab:
                int next = (Array.IndexOf(Sides, _active) + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 3 : 1)) % 4;
                SetActive(Sides[next]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
