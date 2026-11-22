using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using PixelPoint = System.Drawing.Point;

namespace PixelThumbs.ClipboardHistory;

internal sealed partial class ClipboardShelf : Window
{
    private readonly CategoryChoice[] categoryChoices;
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private ClipboardHistoryOptions options = new();
    private IReadOnlyList<HistoryEntry> history = [];
    private bool dragging, menuOpen, externalDrag, positioning, keyboardActive;
    private bool enabled, expanded, rendering, suppressHover;
    private long motionStarted, copyStarted;
    private double progress, motionFrom, motionTo, copyAmplitude, copyFrom;
    private int motionDuration;
    private nint previousForeground;

    internal bool IsExpanded => expanded;
    internal double Expansion => progress;

    internal ClipboardShelf()
    {
        InitializeComponent();
        categoryChoices =
        [
            Choice("Все", "AllGlyph"), Choice("Текст", "TextGlyph"), Choice("Ссылки", "LinkGlyph"),
            Choice("Изображения", "ImageGlyph"), Choice("Файлы", "FileGlyph"), Choice("Другое", "OtherGlyph")
        ];
        Categories.ItemsSource = categoryChoices;
        Categories.SelectedIndex = 0;
        SourceInitialized += (_, _) => PanelPlacement.Configure(this);
        MouseEnter += (_, e) =>
        {
            closeTimer.Stop();
            TryHover(e);
        };
        MouseMove += (_, e) => { if (!expanded && copyStarted != 0) TryHover(e); };
        MouseLeftButtonUp += (_, _) => { if (!expanded && !suppressHover) Open(); };
        MouseLeave += PointerLeft;
        PreviewKeyDown += KeyPressed;
        Deactivated += (_, _) => { keyboardActive = false; previousForeground = 0; PanelPlacement.Configure(this); ScheduleClose(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
        WireDrop();
        InitializeClearMenu();
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            var pointer = PanelPlacement.Cursor();
            if (!dragging && !menuOpen && !(keyboardActive && IsActive) &&
                !Silhouette.FillContains(PointFromScreen(new System.Windows.Point(pointer.X, pointer.Y))))
                Collapse();
        };
        statusTimer.Tick += (_, _) => { statusTimer.Stop(); Status.Visibility = Visibility.Collapsed; };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Refresh(); };
        Closed += (_, _) =>
        {
            enabled = false;
            closeTimer.Stop();
            statusTimer.Stop();
            searchTimer.Stop();
            StopRendering();
        };

        CategoryChoice Choice(string name, string icon) => new(name, (Geometry)FindResource(icon));
    }

    internal event Action<HistoryEntry, ClipboardShelf>? Restore;
    internal event Action<HistoryEntry, ClipboardShelf, Border>? Drag;
    internal event Action<HistoryEntry>? Remove;
    internal event Action<HistoryEntry, ClipboardShelf>? ClearClipboard;
    internal event Action<HistoryEntry[]?>? Clear;
    internal event Action<ClipboardDrop>? Imported;

    internal void Configure(ClipboardHistoryOptions value)
    {
        options = value;
        enabled = true;
        new WindowInteropHelper(this).EnsureHandle();
        Reposition();
        if (!IsVisible)
        {
            Show();
            Reposition();
        }
        if (expanded)
            Refresh();
    }

    internal void Open()
    {
        if (!enabled)
            return;
        closeTimer.Stop();
        if (expanded)
            return;
        expanded = true;
        Refresh();
        MoveShape(open: true);
    }

    internal void Preview()
    {
        if (!enabled)
            return;
        Collapse(immediate: true);
        Open();
    }

    internal void NotifyCopy()
    {
        if (!enabled)
            return;
        copyFrom = copyAmplitude;
        copyStarted = Stopwatch.GetTimestamp();
        EnsureRendering();
    }

    private void Collapse(bool immediate = false)
    {
        closeTimer.Stop();
        expanded = externalDrag = false;
        ClearShelf.ContextMenu.IsOpen = false;
        DropTarget.Visibility = Visibility.Collapsed;
        if (keyboardActive)
        {
            nint target = previousForeground;
            Keyboard.ClearFocus();
            PanelPlacement.ReturnFocus(this, target);
        }
        keyboardActive = false;
        previousForeground = 0;
        if (PanelPlacement.Handle(this) != 0)
            PanelPlacement.Configure(this);
        MoveShape(open: false, immediate);
    }

    private void PointerLeft(object sender, MouseEventArgs e)
    {
        suppressHover = false;
        ScheduleClose();
    }

    private void TryHover(MouseEventArgs e)
    {
        if (suppressHover)
            return;
        // Animated pixels can appear beneath a stationary pointer outside the normal tab.
        if (!expanded && copyStarted != 0 &&
            !ShelfMotion.Frame(Width, Height, options.Edge, progress).Bounds.Contains(e.GetPosition(this)))
            return;
        Open();
    }

    private void ScheduleClose()
    {
        if (expanded)
        {
            closeTimer.Stop();
            closeTimer.Start();
        }
    }

    internal void Reposition()
    {
        if (positioning || !enabled)
            return;
        positioning = true;
        try
        {
            var screen = Screen.AllScreens.FirstOrDefault(candidate => candidate.DeviceName == options.Monitor) ??
                Screen.PrimaryScreen ?? Screen.FromPoint(PixelPoint.Empty);
            var work = screen.WorkingArea;
            double scale = PanelPlacement.Scale(screen);
            bool horizontal = ShelfMotion.Horizontal(options.Edge);
            var layout = ShelfMotion.Layout;
            Width = Math.Min(horizontal ? layout.HorizontalWidth : layout.SideWidth, Math.Max(1, work.Width / scale));
            Height = Math.Min(horizontal ? layout.HorizontalHeight : layout.SideHeight, Math.Max(1, work.Height / scale));
            int panelWidth = (int)Math.Ceiling(Width * scale);
            int panelHeight = (int)Math.Ceiling(Height * scale);
            int centerX = work.Left + (work.Width - panelWidth) / 2;
            int centerY = work.Top + (work.Height - panelHeight) / 2;
            var panel = options.Edge switch
            {
                ClipboardEdge.Top => new PixelPoint(centerX, work.Top),
                ClipboardEdge.Bottom => new PixelPoint(centerX, work.Bottom - panelHeight),
                ClipboardEdge.Left => new PixelPoint(work.Left, centerY),
                _ => new PixelPoint(work.Right - panelWidth, centerY)
            };
            if (PanelPlacement.Handle(this) != 0)
                PanelPlacement.Move(this, PanelPlacement.Clamp(panel, work, Width, Height, scale));
            ApplyAppearance(layout, horizontal);
            ApplyFrame();
        }
        finally { positioning = false; }
    }

    private void ApplyAppearance(ShelfLayout layout, bool horizontal)
    {
        Surface.Padding = new Thickness(layout.Padding);
        Resources["ShelfCardGap"] = horizontal ? new Thickness(0, 0, 10, 3) : new Thickness(0, 0, 4, 7);
        Resources["ShelfCardWidth"] = horizontal ? Math.Min(layout.CardWidth, Math.Max(1, Width - 2 * layout.Padding)) : double.NaN;
        Resources["ShelfPreviewHeight"] = horizontal ? layout.PreviewHeight : layout.PreviewHeight + 23;
        var cards = (ItemsPanelTemplate)FindResource(horizontal ? "HorizontalShelfCards" : "VerticalShelfCards");
        if (Entries.ItemsPanel != cards)
            Entries.ItemsPanel = cards;
        ScrollViewer.SetHorizontalScrollBarVisibility(Entries, horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(Entries, horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

        double available = Math.Max(1, Width - 2 * layout.Padding);
        // Six 24 DIP buttons plus a small gap on both sides of the centered field.
        double inlineWidth = available - 2 * (24 * categoryChoices.Length + 6);
        bool inline = inlineWidth >= 74;
        SearchSlot.Width = Math.Min(224, inline ? inlineWidth : available);
        SearchSlot.Margin = inline ? new Thickness(0) : new Thickness(0, 7, 0, 0);
        Grid.SetRow(SearchSlot, inline ? 0 : 1);
        Grid.SetColumn(SearchSlot, inline ? 1 : 0);
        Grid.SetColumnSpan(SearchSlot, inline ? 1 : 3);
        Grid.SetColumn(ToolbarActions, inline ? 2 : 1);
        Grid.SetColumnSpan(ToolbarActions, inline ? 1 : 2);
    }

    private void MoveShape(bool open, bool immediate = false)
    {
        motionTo = open ? 1 : 0;
        if (immediate || Math.Abs(progress - motionTo) < .002 || !Animate)
        {
            FinishMotion();
            ApplyFrame();
            if (copyStarted == 0)
                StopRendering();
            return;
        }
        motionFrom = progress;
        motionDuration = Math.Max(70, (int)(ShelfMotion.Duration(open) * Math.Abs(motionTo - motionFrom)));
        motionStarted = Stopwatch.GetTimestamp();
        EnsureRendering();
    }

    private static bool Animate => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    private void EnsureRendering()
    {
        if (!rendering)
        {
            rendering = true;
            CompositionTarget.Rendering += RenderMotion;
        }
    }

    private void RenderMotion(object? sender, EventArgs e)
    {
        if (motionStarted != 0)
        {
            double fraction = Stopwatch.GetElapsedTime(motionStarted).TotalMilliseconds / motionDuration;
            if (fraction >= 1)
                FinishMotion();
            else
                progress = ShelfMotion.Lerp(motionFrom, motionTo, fraction);
        }
        if (copyStarted != 0)
        {
            double milliseconds = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
            if (milliseconds >= ShelfMotion.CopyDuration)
                copyStarted = 0;
            copyAmplitude = copyStarted == 0 ? 0 : Animate ? ShelfMotion.CopyAmplitude(milliseconds, copyFrom) : 1;
        }
        ApplyFrame();
        if (motionStarted == 0 && copyStarted == 0)
            StopRendering();
    }

    private void FinishMotion()
    {
        motionStarted = 0;
        progress = motionTo;
        if (!expanded)
            Entries.ItemsSource = null;
    }

    private void StopRendering()
    {
        if (!rendering)
            return;
        CompositionTarget.Rendering -= RenderMotion;
        rendering = false;
    }

    private void ApplyFrame()
    {
        var frame = ShelfMotion.Frame(Width, Height, options.Edge, progress, copyAmplitude);
        Silhouette.Rect = frame.Bounds;
        Silhouette.RadiusX = Silhouette.RadiusY = frame.Radius;
        Surface.Opacity = frame.Opacity;
        Surface.Visibility = frame.Opacity > 0 ? Visibility.Visible : Visibility.Hidden;
        Surface.IsHitTestVisible = expanded && frame.Opacity > .8;
        ToolbarCopied.Opacity = copyAmplitude;
        TabCopied.Opacity = copyAmplitude * Math.Max(0, 1 - progress * 4);
        if (TabCopied.Opacity > 0)
        {
            Canvas.SetLeft(TabCopied, frame.Bounds.Left + (frame.Bounds.Width - TabCopied.Width) / 2);
            Canvas.SetTop(TabCopied, frame.Bounds.Top + (frame.Bounds.Height - TabCopied.Height) / 2);
        }
    }

    private void Entries_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!ShelfMotion.Horizontal(options.Edge) || FindScroller(Entries) is not { } scroller || scroller.ScrollableWidth <= 0)
            return;
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scroller)
                return scroller;
            if (FindScroller(child) is { } nested)
                return nested;
        }
        return null;
    }

    internal void SetEntries(IReadOnlyList<HistoryEntry> items)
    {
        history = items;
        if (expanded)
            Refresh();
    }

    private ShelfFilter Filter => (ShelfFilter)Math.Max(0, Categories.SelectedIndex);
    private string Query => Search.Text.Trim();
    private IEnumerable<HistoryEntry> CategoryEntries() => history.Where(entry =>
        (entry.Item.Kind & options.Types) != 0 && ShelfFilters.Matches(entry.Item, Filter, ""));
    private IEnumerable<HistoryEntry> VisibleEntries() => CategoryEntries().Where(entry =>
        ShelfFilters.Matches(entry.Item, ShelfFilter.All, Query));

    private void Refresh()
    {
        if (Entries is null || Search is null || CategoryCount is null)
            return;
        var category = CategoryEntries().ToArray();
        string query = Query;
        var rows = category.Where(entry => ShelfFilters.Matches(entry.Item, ShelfFilter.All, query))
            .Select(entry => new ShelfRow(entry, categoryChoices[(int)ShelfFilters.Category(entry.Item)].Icon)).ToArray();
        Entries.ItemsSource = rows;
        CategoryCount.Text = category.Length.ToString();
        Empty.Text = query.Length > 0 ? "Ничего не найдено" : "Скопируйте или перетащите сюда то, что хотите сохранить";
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearShelf.IsEnabled = history.Count > 0;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { searchTimer.Stop(); searchTimer.Start(); }
    private void Category_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Search_Click(object sender, MouseButtonEventArgs e)
    {
        if (keyboardActive)
            return;
        previousForeground = PanelPlacement.Foreground();
        keyboardActive = true;
        PanelPlacement.EnableActivation(this);
        Activate();
        Search.Focus();
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            suppressHover = true;
            Collapse();
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        suppressHover = true;
        Collapse();
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearShelf.ContextMenu.IsOpen = true;

    private void InitializeClearMenu()
    {
        var menu = new ContextMenu { PlacementTarget = ClearShelf, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        Add("Текущий список", () => Clear?.Invoke(VisibleEntries().ToArray()));
        Add("За последний час", () => ClearRecent(1));
        Add("За последние 6 часов", () => ClearRecent(6));
        Add("За последние сутки", () => ClearRecent(24));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Всю историю", () => Clear?.Invoke(null));
        menu.Opened += (_, _) => { menuOpen = true; closeTimer.Stop(); };
        menu.Closed += (_, _) => { menuOpen = false; ScheduleClose(); };
        ClearShelf.ContextMenu = menu;

        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void ClearRecent(int hours)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours);
        Clear?.Invoke(VisibleEntries().Where(entry => entry.Time >= cutoff).ToArray());
    }

    private void Entry_Loaded(object sender, RoutedEventArgs e)
    {
        var card = (Border)sender;
        if (card.ContextMenu is not null)
            return;
        HistoryCard.Attach(card,
            () => WithEntry(entry => Restore?.Invoke(entry, this)),
            origin => WithEntry(entry => Drag?.Invoke(entry, this, origin)),
            () => WithEntry(entry => Remove?.Invoke(entry)),
            () => WithEntry(entry => ClearClipboard?.Invoke(entry, this)),
            () => Clear?.Invoke(null), open => { menuOpen = open; if (open) closeTimer.Stop(); else ScheduleClose(); });

        void WithEntry(Action<HistoryEntry> action)
        {
            if (card.DataContext is ShelfRow row)
                action(row.Entry);
        }
    }

    private void Entry_Changed(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Border { ContextMenu.IsOpen: true } card)
            card.ContextMenu.IsOpen = false;
    }

    private void WireDrop()
    {
        AllowDrop = true;
        PreviewDragEnter += OnDrag;
        PreviewDragOver += OnDrag;
        PreviewDragLeave += (_, _) =>
        {
            externalDrag = false;
            DropTarget.Visibility = Visibility.Collapsed;
            ScheduleClose();
        };
        PreviewDrop += (_, e) =>
        {
            var effect = SafeEffect(e.AllowedEffects);
            bool accepted = effect != DragDropEffects.None && AcceptDrop(e.Data);
            externalDrag = false;
            DropTarget.Visibility = Visibility.Collapsed;
            e.Effects = accepted ? effect : DragDropEffects.None;
            e.Handled = true;
            ScheduleClose();
        };
    }

    internal bool AcceptDrop(System.Windows.IDataObject data)
    {
        try
        {
            if (enabled && !dragging && ClipboardDrop.Read(data, options.Types) is { } item)
            {
                Imported?.Invoke(item);
                return true;
            }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException or
            InvalidOperationException or IOException or NotSupportedException)
        {
            SetStatus("Не удалось прочитать перетащенные данные");
        }
        return false;
    }

    private void OnDrag(object sender, DragEventArgs e)
    {
        var effect = SafeEffect(e.AllowedEffects);
        bool accepted = !dragging && effect != DragDropEffects.None && ClipboardDrop.Supports(e.Data, options.Types);
        if (accepted && !externalDrag)
        {
            Open();
            externalDrag = true;
            DropTarget.Visibility = Visibility.Visible;
        }
        if (accepted)
            closeTimer.Stop();
        e.Effects = accepted ? effect : DragDropEffects.None;
        e.Handled = true;
    }

    private static DragDropEffects SafeEffect(DragDropEffects allowed) =>
        (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : allowed & DragDropEffects.Link;

    internal void SetStatus(string message)
    {
        if (!enabled)
            return;
        Status.Text = message;
        Status.Visibility = Visibility.Visible;
        statusTimer.Stop();
        statusTimer.Start();
    }

    internal void SetDragging(bool value)
    {
        if (!enabled)
            return;
        dragging = value;
        if (value)
            closeTimer.Stop();
        else
            ScheduleClose();
    }

    private sealed record CategoryChoice(string Name, Geometry Icon);

    private sealed class ShelfRow
    {
        public HistoryEntry Entry { get; }
        public Geometry Icon { get; }
        public ClipboardItem Item => Entry.Item;
        public string Meta => Entry.Copies > 1 ? $"×{Entry.Copies}   {Entry.Time.ToLocalTime():HH:mm}" : $"{Entry.Time.ToLocalTime():HH:mm}";
        public string Title { get; }
        public string Badge { get; } = "";
        public string Details { get; } = "";

        public ShelfRow(HistoryEntry entry, Geometry icon)
        {
            Entry = entry;
            Icon = icon;
            Title = Item.Label;
            if (Item.Kind == ClipboardKind.Files)
            {
                if (Item.Paths.Length <= 1)
                {
                    string extension = System.IO.Path.GetExtension(Item.Label);
                    Badge = extension.Length is > 1 and <= 9 ? extension[1..].ToUpperInvariant() : "";
                }
                if (Item.Paths.Length > 0)
                    Details = Item.Paths[0];
            }
            else if (ShelfFilters.Category(Item) == ShelfFilter.Links &&
                Uri.TryCreate(Item.TextPreview?.Trim(), UriKind.Absolute, out var address) && address.Host.Length > 0)
            {
                Title = address.Host;
                Details = Item.TextPreview!.Trim();
            }
            if (Item.Incomplete)
                Details = "Некоторые форматы недоступны";
        }
    }
}
