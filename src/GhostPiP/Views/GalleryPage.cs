using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GhostPiP.Core;
using GhostPiP.Mirroring;

namespace GhostPiP.Views;

/// <summary>Every open window as a live, clickable preview.</summary>
public sealed class GalleryPage : UserControl
{
    const double MinCard = 230, MaxCard = 400, Gap = 14, Footer = 50;

    sealed class Card
    {
        public required WindowInfo Info;
        public required Border Root;
        public required Border Slot;
        public required TextBlock Title;
        public required TextBlock Badge;
        public DwmThumbnail? Thumb;
    }

    readonly Dictionary<nint, Card> _cards = new();
    readonly List<Card> _visible = new();
    readonly Canvas _canvas = new() { VerticalAlignment = VerticalAlignment.Top };
    readonly ScrollViewer _scroll;
    readonly TextBox _search = new() { Width = 280, Tag = "Search windows", VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _empty;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1.5) };
    int _selected;
    bool _active;

    public GalleryPage()
    {
        var root = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 26, 0, 16) };
        _search.TextChanged += (_, _) => { _selected = 0; Refresh(); };
        DockPanel.SetDock(_search, Dock.Right);
        head.Children.Add(_search);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Float a window", Style = (Style)FindResource("T.Display") });
        titles.Children.Add(new TextBlock { Text = "Live previews of everything open. Pick one and it floats above your work, and your clicks pass straight through it.", Style = (Style)FindResource("T.Secondary"), Margin = new Thickness(0, 3, 16, 0) });
        head.Children.Add(titles);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        _empty = new TextBlock { Text = "No windows match", Style = (Style)FindResource("T.Secondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _canvas, Focusable = false };
        _scroll.ScrollChanged += (_, _) => PlaceThumbs();
        _scroll.SizeChanged += (_, _) => Arrange();
        var body = new Grid();
        body.Children.Add(_scroll);
        body.Children.Add(_empty);
        root.Children.Add(body);
        Content = root;

        _refresh.Tick += (_, _) => Refresh();
        PreviewKeyDown += OnKey;
    }

    public void SetSearch(string text) => _search.Text = text;

    public void SetActive(bool active)
    {
        if (_active == active)
            return;
        _active = active;
        if (active)
        {
            _refresh.Start();
            Refresh();
            _search.Focus();
        }
        else
        {
            _refresh.Stop();
            foreach (var card in _cards.Values)
            {
                card.Thumb?.Dispose();
                card.Thumb = null;
            }
        }
    }

    void Refresh()
    {
        if (!_active)
            return;
        string q = _search.Text.Trim();
        var windows = WindowScanner.List()
            .Where(w => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || w.App.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var keep = windows.Select(w => w.Hwnd).ToHashSet();
        foreach (var hwnd in _cards.Keys.Where(h => !keep.Contains(h)).ToArray())
        {
            _cards[hwnd].Thumb?.Dispose();
            _canvas.Children.Remove(_cards[hwnd].Root);
            _cards.Remove(hwnd);
        }

        _visible.Clear();
        foreach (var w in windows)
        {
            if (!_cards.TryGetValue(w.Hwnd, out var card))
                _cards[w.Hwnd] = card = MakeCard(w);
            card.Info = w;
            card.Title.Text = w.Title;
            var mirror = App.Mirrors.Find(w.Hwnd);
            bool playing = App.Media.AnyPlayingFor(w.Title, w.ExeName);
            card.Badge.Text = mirror is not null ? "LIVE" : playing ? "PLAYING" : "";
            card.Badge.Foreground = (Brush)FindResource(mirror is not null ? "Live" : "AccentSolid");
            _visible.Add(card);
        }
        _empty.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _visible.Count - 1));
        Arrange();
    }

    Card MakeCard(WindowInfo w)
    {
        var slot = new Border { Background = (Brush)FindResource("Well"), CornerRadius = new CornerRadius(6) };
        var icon = new Image { Source = AppIcons.ForWindow(w.Hwnd, w.ExePath), Width = 22, Height = 22, Margin = new Thickness(2, 0, 10, 0) };
        var title = new TextBlock { Text = w.Title, Style = (Style)FindResource("T.Body"), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var app = new TextBlock { Text = w.App, Style = (Style)FindResource("T.Caption"), TextTrimming = TextTrimming.CharacterEllipsis };
        var badge = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(title);
        words.Children.Add(app);
        var foot = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(badge, Dock.Right);
        foot.Children.Add(icon);
        foot.Children.Add(badge);
        foot.Children.Add(words);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Footer - 8) });
        Grid.SetRow(foot, 1);
        grid.Children.Add(slot);
        grid.Children.Add(foot);
        var root = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(8, 8, 8, 0),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        var card = new Card { Info = w, Root = root, Slot = slot, Title = title, Badge = badge };
        root.MouseEnter += (_, _) => Paint(card, hover: true);
        root.MouseLeave += (_, _) => Paint(card, hover: false);
        root.MouseLeftButtonUp += (_, _) => Activate(card, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _canvas.Children.Add(root);
        return card;
    }

    void Paint(Card card, bool hover)
    {
        bool selected = _visible.IndexOf(card) == _selected && _search.IsKeyboardFocused;
        card.Root.BorderBrush = (Brush)FindResource(hover || selected ? "AccentSolid" : "Hairline");
        card.Root.Background = (Brush)FindResource(hover ? "Fill2" : "Fill1");
    }

    /// <summary>Picks the biggest card size at which everything fits; scrolls only when even the smallest doesn't.</summary>
    void Arrange()
    {
        double width = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth;
        double height = _scroll.ActualHeight;
        if (width <= 0 || _visible.Count == 0)
        {
            _canvas.Height = 0;
            return;
        }
        int n = _visible.Count;
        double cardW = MinCard, cardH = 0;
        int columns = Math.Max(1, (int)((width + Gap) / (MinCard + Gap)));
        for (int c = 1; c <= n; c++)
        {
            double w = (width - Gap * (c - 1)) / c;
            if (w < MinCard)
                break;
            w = Math.Min(w, MaxCard);
            double h = (w - 16) * 10 / 16 + Footer + 8;
            int rows = (int)Math.Ceiling((double)n / c);
            if (rows * h + (rows - 1) * Gap <= height)
            {
                columns = c;
                break;
            }
        }
        cardW = Math.Min((width - Gap * (columns - 1)) / columns, MaxCard);
        cardH = (cardW - 16) * 10 / 16 + Footer + 8;
        for (int i = 0; i < n; i++)
        {
            var card = _visible[i];
            card.Root.Width = cardW;
            card.Root.Height = cardH;
            Canvas.SetLeft(card.Root, (i % columns) * (cardW + Gap));
            Canvas.SetTop(card.Root, (i / columns) * (cardH + Gap));
            Paint(card, card.Root.IsMouseOver);
        }
        _canvas.Height = (int)Math.Ceiling((double)n / columns) * (cardH + Gap) - Gap;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceThumbs);
    }

    void PlaceThumbs()
    {
        if (!_active || Window.GetWindow(this) is not MainWindow main || main.Handle == 0)
            return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var view = _scroll.TransformToAncestor(main).TransformBounds(new Rect(_scroll.RenderSize));
        foreach (var card in _visible)
        {
            if (card.Info.IsMinimized)
            {
                card.Thumb?.Hide();
                continue;
            }
            card.Thumb ??= DwmThumbnail.Create(main.Handle, card.Info.Hwnd);
            if (card.Thumb is null || !card.Slot.IsLoaded)
                continue;
            var slot = card.Slot.TransformToAncestor(main).TransformBounds(new Rect(card.Slot.RenderSize));
            var (sw, sh) = Win.ClientSize(card.Info.Hwnd);
            if (sw < 2 || sh < 2)
                continue;
            // Letterbox inside the slot, then clip to the scroll viewport by trimming source and destination together.
            double k = Math.Min(slot.Width / sw, slot.Height / sh);
            double dw = sw * k, dh = sh * k;
            var fit = new Rect(slot.X + (slot.Width - dw) / 2, slot.Y + (slot.Height - dh) / 2, dw, dh);
            var shown = Rect.Intersect(fit, view);
            if (shown.IsEmpty || shown.Height < 1)
            {
                card.Thumb.Hide();
                continue;
            }
            var source = new RECT(
                (int)((shown.X - fit.X) / k), (int)((shown.Y - fit.Y) / k),
                (int)((shown.Right - fit.X) / k), (int)((shown.Bottom - fit.Y) / k));
            card.Thumb.Update(new RECT((int)(shown.X * dpi), (int)(shown.Y * dpi), (int)(shown.Right * dpi), (int)(shown.Bottom * dpi)), source);
        }
    }

    void Activate(Card card, bool stayOpen)
    {
        if (App.Mirrors.Find(card.Info.Hwnd) is { } existing)
        {
            App.ShowStudio(existing);
            return;
        }
        // The mirror lifts off from exactly where its card is.
        PxRect? from = null;
        if (card.Slot.IsLoaded)
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var p = card.Slot.PointToScreen(new Point());
            from = new PxRect((int)p.X, (int)p.Y, (int)(card.Slot.ActualWidth * dpi), (int)(card.Slot.ActualHeight * dpi));
        }
        App.Mirrors.Start(card.Info.Hwnd, from);
        if (!stayOpen && Window.GetWindow(this) is MainWindow main)
            main.BackgroundAfterStart();
        else
            Refresh();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_visible.Count == 0)
            return;
        int columns = Math.Max(1, (int)((_scroll.ActualWidth + Gap) / (_visible[0].Root.Width + Gap)));
        int move = e.Key switch
        {
            Key.Down => columns,
            Key.Up => -columns,
            Key.Right when _search.Text.Length == 0 => 1,
            Key.Left when _search.Text.Length == 0 => -1,
            _ => 0,
        };
        if (move != 0)
        {
            _selected = Math.Clamp(_selected + move, 0, _visible.Count - 1);
            foreach (var card in _visible)
                Paint(card, card.Root.IsMouseOver);
            _visible[_selected].Root.BringIntoView();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Activate(_visible[_selected], Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_search.Text.Length > 0)
                _search.Clear();
            else
                Window.GetWindow(this)?.Close();
            e.Handled = true;
        }
    }
}
