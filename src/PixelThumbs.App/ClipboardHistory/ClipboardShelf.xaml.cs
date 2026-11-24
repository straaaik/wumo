using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using ActionResult = System.Windows.Forms.DialogResult;
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
    private IReadOnlyList<HistoryEntry> favorites = [];
    private IReadOnlyList<HistoryFolder> folders = [];
    private HistoryEntry? dragSource;
    private Guid? openFolder;
    private NameEdit? nameEdit;
    private bool dragging, menuOpen, externalDrag, positioning, keyboardActive;
    private bool enabled, expanded, rendering, suppressHover, confirmingRemoval;
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
            Choice("Изображения", "ImageGlyph"), Choice("Файлы", "FileGlyph"), Choice("Другое", "OtherGlyph"),
            Choice("Избранное", "StarGlyph")
        ];
        Categories.ItemsSource = categoryChoices;
        Categories.SelectedIndex = 0;
        SourceInitialized += (_, _) =>
        {
            PanelPlacement.Configure(this);
            HwndSource.FromHwnd(PanelPlacement.Handle(this)).AddHook(MouseActivation);
        };
        MouseEnter += (_, e) =>
        {
            closeTimer.Stop();
            TryHover(e);
        };
        MouseMove += (_, e) => { if (!expanded && copyStarted != 0) TryHover(e); };
        MouseLeftButtonUp += (_, _) => { if (!expanded && !suppressHover) Open(); };
        MouseLeave += PointerLeft;
        AddHandler(System.Windows.Input.Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(SearchMouseDown), true);
        PreviewKeyDown += KeyPressed;
        Deactivated += (_, _) =>
        {
            FinishNameEdit(save: true);
            keyboardActive = false;
            previousForeground = 0;
            PanelPlacement.Configure(this);
            ScheduleClose();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition);
        Closing += (_, _) =>
        {
            FinishNameEdit(save: false);
            ReleaseKeyboard();
        };
        WireDrop();
        InitializeClearMenu();
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            var pointer = PanelPlacement.Cursor();
            if (!dragging && !menuOpen && !confirmingRemoval && !(keyboardActive && IsActive) &&
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
    internal event Action<Guid>? RemoveFolder;
    internal event Action<HistoryEntry>? TogglePin;
    internal event Func<HistoryEntry, string, HistoryEntry?>? Rename;
    internal event Func<Guid, string, bool>? RenameFolder;
    internal event Func<HistoryEntry, HistoryEntry?, Guid?, bool>? Group;
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
        FinishNameEdit(save: true);
        ReleaseKeyboard();
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
        // Leave room for the category icons on either side of the centered field.
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
        if (immediate || Math.Abs(progress - motionTo) < .002 || !UiMotion.IsEnabled)
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
        bool animate = UiMotion.IsEnabled;
        if (motionStarted != 0)
        {
            double fraction = Stopwatch.GetElapsedTime(motionStarted).TotalMilliseconds / motionDuration;
            if (!animate || fraction >= 1)
                FinishMotion();
            else
                progress = ShelfMotion.Lerp(motionFrom, motionTo, fraction);
        }
        if (copyStarted != 0)
        {
            double milliseconds = Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
            if (milliseconds >= ShelfMotion.CopyDuration)
                copyStarted = 0;
            copyAmplitude = copyStarted == 0 ? 0 : animate ? ShelfMotion.CopyAmplitude(milliseconds, copyFrom) : 1;
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

    internal void SetEntries(IReadOnlyList<HistoryEntry> items, IReadOnlyList<HistoryEntry> saved, IReadOnlyList<HistoryFolder> groups)
    {
        history = items;
        favorites = saved;
        folders = groups;
        if (expanded)
            Refresh();
    }

    private ShelfFilter Filter => (ShelfFilter)Math.Max(0, Categories.SelectedIndex);
    private string Query => Search.Text.Trim();
    private HistoryFolder? CurrentFolder => openFolder is { } id ? folders.FirstOrDefault(folder => folder.Id == id) : null;

    private IEnumerable<HistoryEntry> VisibleEntries() => history.Where(entry =>
        (entry.Item.Kind & options.Types) != 0 && ShelfFilters.Matches(entry.Item, Filter, "") && Matches(entry, Query));

    private static bool Matches(HistoryEntry entry, string query) => ShelfFilters.Contains(entry.Name, query) ||
        ShelfFilters.Matches(entry.Item, ShelfFilter.All, query);

    private IEnumerable<ShelfRow> CategoryRows(HistoryFolder? folder)
    {
        if (folder is not null)
        {
            foreach (var entry in folder.Entries)
                yield return EntryRow(entry, inFolder: true);
            yield break;
        }
        foreach (var entry in favorites)
            if (Filter == ShelfFilter.Favorites || InCategory(entry))
                yield return EntryRow(entry, pinned: true);
        foreach (var entry in history)
            if (Filter != ShelfFilter.Favorites && InCategory(entry))
                yield return EntryRow(entry);
        if (Filter is ShelfFilter.All or ShelfFilter.Favorites)
            foreach (var group in folders)
                yield return FolderRow(group);

        bool InCategory(HistoryEntry entry) => (entry.Item.Kind & options.Types) != 0 &&
            ShelfFilters.Matches(entry.Item, Filter, "");

        ShelfRow EntryRow(HistoryEntry entry, bool pinned = false, bool inFolder = false) =>
            new(entry, categoryChoices[(int)ShelfFilters.Category(entry.Item)].Icon, pinned, inFolder);
    }

    private void Refresh(bool reveal = false)
    {
        if (Entries is null || Search is null || CategoryCount is null || nameEdit is not null)
            return;
        var folder = CurrentFolder;
        var category = CategoryRows(folder).ToArray();
        string query = Query;
        bool showPinsFirst = folder is null && Filter != ShelfFilter.Favorites;
        var rows = category.Where(row => row.Entry is { } entry ? Matches(entry, query) :
            ShelfFilters.Contains(row.Folder!.Name, query) || row.Folder.Entries.Any(child => Matches(child, query)))
            .OrderByDescending(row => showPinsFirst && row.Pinned).ThenByDescending(row => row.Time).ToArray();
        Entries.ItemsSource = rows;
        CategoryCount.Text = category.Length.ToString();
        FolderNavigation.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        FolderTitle.DataContext = folder is null ? null : FolderRow(folder);
        Empty.Text = query.Length > 0 ? "Ничего не найдено" : folder is not null ? "Папка пуста" :
            Filter == ShelfFilter.Favorites ? "Закрепите запись, чтобы сохранить её здесь" :
            "Скопируйте или перетащите сюда то, что хотите сохранить";
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearShelf.IsEnabled = folder is null && Filter != ShelfFilter.Favorites && history.Count > 0;
        if (reveal && Surface.IsHitTestVisible)
        {
            UiMotion.Reveal(rows.Length == 0 ? Empty : Entries, offset: 0);
            if (folder is not null)
                UiMotion.Reveal(FolderNavigation, offset: 0);
        }
    }

    private ShelfRow FolderRow(HistoryFolder folder) => new(folder, (Geometry)FindResource("FolderGlyph"));

    private void Search_Changed(object sender, TextChangedEventArgs e) { searchTimer.Stop(); searchTimer.Start(); }
    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (openFolder is not null)
            ChangeFolder(null);
        else
        {
            FinishNameEdit(save: true);
            Refresh(reveal: true);
        }
    }

    private void ChangeFolder(Guid? id)
    {
        FinishNameEdit(save: true);
        openFolder = id;
        Search.Clear();
        searchTimer.Stop();
        Refresh(reveal: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ChangeFolder(null);

    private nint MouseActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int mouseActivate = 0x0021, leftDown = 0x0201, activate = 1;
        if (message != mouseActivate || (lParam.ToInt64() >> 16 & 0xffff) != leftDown || !Surface.IsHitTestVisible)
            return 0;
        var cursor = PanelPlacement.Cursor();
        if (FindKeyboardInput(InputHitTest(PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y)))) is null)
            return 0;
        // Activate before WPF dispatches the first click to an editable field.
        EnableKeyboard();
        handled = true;
        return activate;
    }

    private void SearchMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindKeyboardInput(e.OriginalSource) != Search)
            return;
        FocusInput(Search);
    }

    private TextBox? FindKeyboardInput(object? hit)
    {
        for (var element = hit as Visual; element is not null; element = VisualTreeHelper.GetParent(element) as Visual)
            if (element is TextBox field && (field == Search || field.DataContext is ShelfRow { CanRename: true }))
                return field;
        return null;
    }

    private void FocusInput(TextBox field)
    {
        EnableKeyboard();
        if (!IsActive)
            Activate();
        Keyboard.Focus(field);
    }

    private void EnableKeyboard()
    {
        if (keyboardActive)
            return;
        previousForeground = PanelPlacement.Foreground();
        keyboardActive = true;
        PanelPlacement.EnableActivation(this);
    }

    private void ReleaseKeyboard()
    {
        if (!keyboardActive)
            return;
        nint target = previousForeground;
        keyboardActive = false;
        previousForeground = 0;
        Keyboard.ClearFocus();
        PanelPlacement.Configure(this);
        PanelPlacement.ReturnFocus(this, target);
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (nameEdit is { Field.IsKeyboardFocusWithin: true } && (e.Key is Key.Enter or Key.Escape))
        {
            e.Handled = true;
            FinishNameEdit(save: e.Key == Key.Enter);
            ReleaseKeyboard();
        }
        else if (e.Key == Key.Escape)
        {
            suppressHover = true;
            Collapse();
            e.Handled = true;
        }
    }

    private void Name_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && BeginNameEdit((TextBox)sender))
            e.Handled = true;
    }

    private void Name_KeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Enter or Key.F2) && BeginNameEdit((TextBox)sender))
            e.Handled = true;
    }

    private bool BeginNameEdit(TextBox field)
    {
        if (!field.IsReadOnly || field.DataContext is not ShelfRow { CanRename: true } row)
            return false;
        FinishNameEdit(save: true);
        nameEdit = new(field, row);
        field.IsReadOnly = false;
        FocusInput(field);
        field.SelectAll();
        return true;
    }

    private void Name_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (nameEdit is not { } edit || edit.Field != sender)
            return;
        Dispatcher.InvokeAsync(() =>
        {
            if (nameEdit == edit && !edit.Field.IsKeyboardFocusWithin)
                FinishNameEdit(save: true);
        }, DispatcherPriority.Background);
    }

    private void FinishNameEdit(bool save)
    {
        if (nameEdit is not { } edit)
            return;
        var (field, row) = edit;
        string title = field.Text.Trim();
        // Keep the card alive during the model update so a click on another control can finish.
        if (save && title.Length > 0 && title != row.Title)
        {
            if (row.Folder is { } folder && RenameFolder?.Invoke(folder.Id, title) == true)
            {
                row.Folder = folder with { Name = title };
                row.Title = title;
            }
            else if (row.Entry is { } entry && Rename?.Invoke(entry, title) is { } renamed)
            {
                row.Entry = renamed;
                row.Title = title;
            }
        }
        field.SetCurrentValue(TextBox.TextProperty, row.Title);
        field.IsReadOnly = true;
        nameEdit = null;
        Dispatcher.InvokeAsync(() => { if (expanded) Refresh(); }, DispatcherPriority.ContextIdle);
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
        var menu = new ContextMenu();
        var open = Add("Вернуть в буфер", OpenRow);
        var pin = Add("Закрепить в избранном", PinRow);
        var remove = Add("Удалить запись", RemoveRow);
        var clearClipboard = Add("Очистить системный буфер", row =>
        {
            if (row.Entry is { } entry)
                ClearClipboard?.Invoke(entry, this);
        });
        menu.Opened += (_, _) =>
        {
            if (card.DataContext is not ShelfRow row)
                return;
            open.Header = row.Folder is null ? "Вернуть в буфер" : "Открыть папку";
            pin.Header = row.PinTitle;
            remove.Header = row.RemoveTitle;
            pin.Visibility = clearClipboard.Visibility = row.EntryActions;
            menuOpen = true;
            closeTimer.Stop();
        };
        menu.Closed += (_, _) => { menuOpen = false; ScheduleClose(); };
        HistoryCard.Attach(card, () => WithRow(OpenRow), origin => WithRow(row =>
        {
            FinishNameEdit(save: true);
            if (row.Entry is { } entry)
                Drag?.Invoke(entry, this, origin);
        }), menu);

        MenuItem Add(string title, Action<ShelfRow> action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => WithRow(action);
            menu.Items.Add(item);
            return item;
        }

        void WithRow(Action<ShelfRow> action)
        {
            if (card.DataContext is ShelfRow row)
                action(row);
        }
    }

    private void OpenRow(ShelfRow row)
    {
        FinishNameEdit(save: true);
        if (row.Folder is { } folder)
            ChangeFolder(folder.Id);
        else if (row.Entry is { } entry)
        {
            Restore?.Invoke(entry, this);
            ReleaseKeyboard();
        }
    }

    private void PinRow(ShelfRow row)
    {
        FinishNameEdit(save: true);
        if (row.Entry is { } entry)
            TogglePin?.Invoke(entry);
    }

    private void RemoveRow(ShelfRow row)
    {
        FinishNameEdit(save: true);
        if (row.Entry is { } entry)
            Remove?.Invoke(entry);
        else if (row.Folder is { } folder)
            ConfirmFolderRemoval(folder);
    }

    private void ConfirmFolderRemoval(HistoryFolder folder)
    {
        confirmingRemoval = true;
        closeTimer.Stop();
        ReleaseKeyboard();
        nint target = PanelPlacement.Foreground();
        try
        {
            string message = folder.Entries.Length == 0 ? $"Удалить папку «{folder.Name}»?" :
                $"Удалить папку «{folder.Name}» со всеми записями в ней?";
            if (ConsoleDialog.Show(this, "Удаление папки", message, ActionResult.Cancel,
                new("Отмена", ActionResult.Cancel), new("Удалить", ActionResult.OK, Dangerous: true)) == ActionResult.OK)
                RemoveFolder?.Invoke(folder.Id);
        }
        finally
        {
            PanelPlacement.ReturnFocus(this, target);
            confirmingRemoval = false;
            ScheduleClose();
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is ShelfRow row)
            PinRow(row);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is ShelfRow row)
            RemoveRow(row);
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
            Entries.SelectedItem = null;
            ScheduleClose();
        };
        PreviewDrop += (_, e) =>
        {
            var effect = SafeEffect(e.AllowedEffects);
            bool accepted = false;
            if (effect != DragDropEffects.None)
            {
                if (dragSource is not null)
                    accepted = AcceptGroupDrop(e.Data, e.GetPosition(Entries));
                else
                    accepted = AcceptDrop(e.Data);
            }
            externalDrag = false;
            DropTarget.Visibility = Visibility.Collapsed;
            Entries.SelectedItem = null;
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
        if (dragSource is { } source)
        {
            var row = effect == DragDropEffects.None ? null : FindGroupTarget(e.Data, e.GetPosition(Entries), source);
            Entries.SelectedItem = row;
            e.Effects = row is null ? DragDropEffects.None : effect;
            e.Handled = true;
            return;
        }
        bool accepted = !dragging && effect != DragDropEffects.None && ClipboardDrop.Supports(e.Data, options.Types);
        if (accepted && !externalDrag)
        {
            Open();
            externalDrag = true;
            DropTarget.Visibility = Visibility.Visible;
            UiMotion.Reveal(DropTarget, offset: 0);
        }
        if (accepted)
            closeTimer.Stop();
        e.Effects = accepted ? effect : DragDropEffects.None;
        e.Handled = true;
    }

    internal bool AcceptGroupDrop(System.Windows.IDataObject data, System.Windows.Point point) =>
        dragSource is { } source && FindGroupTarget(data, point, source) is { } row &&
        Group?.Invoke(source, row.Entry, row.Folder?.Id) == true;

    private ShelfRow? FindGroupTarget(System.Windows.IDataObject data, System.Windows.Point point, HistoryEntry source)
    {
        if (openFolder is not null || !expanded || !data.GetDataPresent(ClipboardTransfer.ShelfEntryFormat, autoConvert: false))
            return null;
        if (Entries.InputHitTest(point) is not DependencyObject hit ||
            ItemsControl.ContainerFromElement(Entries, hit) is not ListBoxItem { DataContext: ShelfRow row } || row.Entry == source)
            return null;
        return row;
    }

    private static DragDropEffects SafeEffect(DragDropEffects allowed) =>
        (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : allowed & DragDropEffects.Link;

    internal void SetStatus(string message)
    {
        if (!enabled)
            return;
        Status.Text = message;
        if (Status.Visibility != Visibility.Visible)
        {
            Status.Visibility = Visibility.Visible;
            UiMotion.Reveal(Status, offset: 0);
        }
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
        {
            SetDragSource(null);
            Entries.SelectedItem = null;
            ScheduleClose();
        }
    }

    internal void SetDragSource(HistoryEntry? source) => dragSource = source;

    private sealed record CategoryChoice(string Name, Geometry Icon);

    private sealed record NameEdit(TextBox Field, ShelfRow Row);

    private sealed class ShelfRow
    {
        public HistoryEntry? Entry { get; set; }
        public HistoryFolder? Folder { get; set; }
        public Geometry Icon { get; }
        public System.Windows.Media.Imaging.BitmapSource? Thumbnail => Entry?.Item.Thumbnail;
        public DateTime Time => Entry?.Time ?? Folder!.Time;
        public string Meta { get; }
        public string Title { get; set; }
        public bool Pinned { get; }
        public string PinTitle { get; } = "Закрепить в избранном";
        public string RemoveTitle => Folder is null ? "Удалить запись" : "Удалить папку";
        public Visibility EntryActions => Entry is null ? Visibility.Collapsed : Visibility.Visible;
        public bool CanRename => Folder is not null || Pinned;
        public string Badge { get; } = "";
        public string Details { get; } = "";

        public ShelfRow(HistoryFolder folder, Geometry icon)
        {
            Folder = folder;
            Icon = icon;
            Title = folder.Name;
            Meta = $"Записей: {folder.Entries.Length}";
        }

        public ShelfRow(HistoryEntry entry, Geometry icon, bool pinned, bool inFolder)
        {
            Entry = entry;
            Icon = icon;
            Pinned = pinned || inFolder;
            PinTitle = pinned ? "Открепить из избранного" : inFolder ? "Вынести в избранное" : PinTitle;
            string time = entry.Time.ToLocalTime().ToString("HH:mm");
            Meta = entry.Copies > 1 ? $"×{entry.Copies}   {time}" : time;
            var item = entry.Item;
            Title = item.Label;
            if (item.Kind == ClipboardKind.Files)
            {
                if (item.Paths.Length <= 1)
                {
                    string extension = System.IO.Path.GetExtension(item.Label);
                    Badge = extension.Length is > 1 and <= 9 ? extension[1..].ToUpperInvariant() : "";
                }
                if (item.Paths.Length > 0)
                    Details = item.Paths[0];
            }
            else if (ShelfFilters.Category(item) == ShelfFilter.Links &&
                Uri.TryCreate(item.TextPreview?.Trim(), UriKind.Absolute, out var address) && address.Host.Length > 0)
            {
                Title = address.Host;
                Details = item.TextPreview!.Trim();
            }
            if (entry.Name is { } name)
            {
                Details = Details.Length == 0 ? Title : Details;
                Title = name;
            }
            if (item.Incomplete)
                Details = "Некоторые форматы недоступны";
        }
    }
}
