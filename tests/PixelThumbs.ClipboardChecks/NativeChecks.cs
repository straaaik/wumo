using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelThumbs;
using PixelThumbs.ClipboardHistory;
using static PixelThumbs.ClipboardHistory.ClipboardNative;
using App = System.Windows.Forms.Application;

internal static class NativeChecks
{
    internal static int Run(string[] args)
    {
        // Each station has its own clipboard. Fail closed if Windows denies creating the station.
        int child = Array.IndexOf(args, "--child");
        if (child >= 0)
        {
            var name = new StringBuilder(256);
            NativeRequire(GetUserObjectInformation(GetProcessWindowStation(), 2, name, 512, out _) &&
                name.ToString() == args[child + 1], "clipboard isolation");
            Exception? failure = null;
            var sta = new Thread(() =>
            {
                string profile = Path.Combine(Path.GetTempPath(), $"WumoClipboardCheck-{Environment.ProcessId}");
                Environment.SetEnvironmentVariable("PIXELTHUMBS_DATA_DIR", profile);
                try
                {
                    Directory.CreateDirectory(profile);
                    RunOnDesktop(args);
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    File.Delete(Path.Combine(profile, "settings.json"));
                    File.Delete(Path.Combine(profile, "clipboard-history.dat"));
                    Directory.Delete(profile);
                }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return 0;
        }
        string stationName = "WumoClipboardCheck" + Environment.ProcessId;
        nint station = CreateWindowStation(stationName, 0, 0x02000000, 0);
        NativeRequire(station != 0 && SetProcessWindowStation(station), "private window station");
        nint desktop = CreateDesktop("Default", 0, 0, 0, 0x02000000, 0);
        NativeRequire(desktop != 0, "private desktop");
        string exe = Environment.ProcessPath!;
        string flags = string.Join(' ', args.Where(arg => arg is "--native" or "--idle" or "--off" or "--open"));
        var command = new StringBuilder($"\"{exe}\" {flags} --child {stationName}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = stationName + @"\Default" };
        NativeRequire(CreateProcess(exe, command, 0, 0, false, 0, 0, null, ref startup, out var process), "start in private desktop");
        CloseHandle(process.Thread);
        try
        {
            NativeRequire(WaitForSingleObject(process.Process, 340_000) == 0, "wait for test process");
            NativeRequire(GetExitCodeProcess(process.Process, out uint exit), "get test result");
            return (int)exit;
        }
        finally
        {
            CloseHandle(process.Process);
        }
    }

    private static void RunOnDesktop(string[] args)
    {
        _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        if (args.Contains("--idle"))
        {
            MeasureIdle(args);
            return;
        }
        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        using var publisher = new Control();
        _ = publisher.Handle;
        using var widget = new HistoryWidget(dispatcher);
        var enabled = new ClipboardHistoryOptions { Enabled = true };
        widget.Configure(enabled);
        Require(widget.Active, "listener subscription");
        Exception? failure = null;
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await CheckNative(dispatcher, publisher.Handle, widget, enabled);
            }
            catch (Exception error) { failure = error; }
            finally { App.ExitThread(); }
        });
        App.Run();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void MeasureIdle(string[] args)
    {
        bool off = args.Contains("--off");
        bool open = args.Contains("--open");
        SettingsStore.Save(new AppSettings { ClipboardHistory = new() { Enabled = !off } });
        ConsoleTheme.ConfigureNativeControls();
        using var publisher = new Control();
        using var tray = new PixelThumbs.Program.TrayContext(new TrayChannel(".ClipboardIdle" + Environment.ProcessId), openSettings: false);
        _ = publisher.Handle;
        Exception? failure = null;
        publisher.BeginInvoke(async () =>
        {
            try
            {
                if (!off && open)
                {
                    Set(publisher.Handle, (Unicode, Encoding.Unicode.GetBytes("idle panel\0")));
                    await Until(() => Shelf() is not null);
                    Shelf()!.Open();
                }
                using var process = Process.GetCurrentProcess();
                await Task.Delay(1000);
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                long memory = process.PrivateMemorySize64;
                var watch = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromMinutes(5));
                process.Refresh();
                Console.WriteLine($"idle off={off} open={open} wall={watch.Elapsed.TotalSeconds:F1}s " +
                    $"cpu={(process.TotalProcessorTime - cpu).TotalMilliseconds:F1}ms private={memory / 1048576d:F1}→{process.PrivateMemorySize64 / 1048576d:F1} MiB");
            }
            catch (Exception error) { failure = error; }
            finally { tray.ExitThread(); }
        });
        App.Run(tray);
        if (failure is not null)
            throw failure;
    }

    private static async Task CheckNative(Control dispatcher, nint owner, HistoryWidget widget, ClipboardHistoryOptions enabled)
    {
        CheckSettings(enabled);
        var shelf = Shelf()!;
        nint handle = PanelPlacement.Handle(shelf);
        Require(handle != 0 && shelf.IsVisible && !shelf.IsExpanded && shelf.AllowDrop,
            "one live window shows the collapsed tab and accepts drops");
        shelf.UpdateLayout();
        var strip = RenderShelf(shelf);
        byte[] pixel = new byte[4];
        strip.CopyPixels(new Int32Rect(10, 20, 1, 1), pixel, 4, 0);
        Require(pixel[3] == 0, "the invisible part of the collapsed shelf is transparent");
        strip.CopyPixels(new Int32Rect(strip.PixelWidth - 6, strip.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Require(pixel.AsSpan().SequenceEqual(new byte[] { 10, 10, 10, 255 }), "the strip uses the exact Wumo background");
        Set(owner, (Unicode, Encoding.Unicode.GetBytes("plain\0")));
        var tabCopied = (FrameworkElement)shelf.FindName("TabCopied");
        var silhouette = (RectangleGeometry)((FrameworkElement)shelf.Content).Clip;
        await Until(() => tabCopied.Opacity > .9);
        Require(!shelf.IsExpanded && silhouette.Rect.Width > 19 &&
            Math.Abs(silhouette.Rect.Top + silhouette.Rect.Bottom - shelf.Height) < .01,
            "a real clipboard capture grows the centered tab and shows a checkmark without opening");
        await Until(() => tabCopied.Opacity == 0 && silhouette.Rect.Width == 12);
        shelf.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent
        });
        await Until(() => shelf.IsExpanded && shelf.Expansion == 1 && Rows(shelf).Items.Count == 1);
        Require(PanelPlacement.Handle(shelf) == handle &&
            System.Windows.Application.Current.Windows.OfType<ClipboardShelf>().Count() == 1,
            "hover expands the same history window");

        var categories = (System.Windows.Controls.ListBox)shelf.FindName("Categories");
        var search = (System.Windows.Controls.TextBox)shelf.FindName("Search");
        var count = (System.Windows.Controls.TextBlock)shelf.FindName("CategoryCount");
        var copied = (FrameworkElement)shelf.FindName("ToolbarCopied");
        Set(owner, (Unicode, Encoding.Unicode.GetBytes("https://example.invalid/page\0")));
        await Until(() => Rows(shelf).Items.Count == 2 && copied.Opacity > .9);
        Require(tabCopied.Opacity == 0 && count.Text == "2", "expanded copy feedback and all-items count");
        categories.SelectedIndex = (int)ShelfFilter.Links;
        await Until(() => Cards(shelf).Any(card => HasText(card, "example.invalid") && HasText(card, "https://example.invalid/page")));
        Require(Rows(shelf).Items.Count == 1 && count.Text == "1", "link filter and category count");
        search.Text = "missing";
        await Until(() => Rows(shelf).Items.Count == 0);
        Require(count.Text == "1", "search does not replace the category total");
        search.Text = "PAGE";
        await Until(() => Rows(shelf).Items.Count == 1);
        categories.SelectedIndex = (int)ShelfFilter.All;
        search.Clear();

        Set(owner, (Unicode, Encoding.Unicode.GetBytes("bold\0")), (Rtf, Encoding.ASCII.GetBytes(@"{\rtf1\b bold}" + "\0")));
        await Until(() => Cards(shelf).Any(card => CardName(card) == "bold"));
        var bold = Cards(shelf).Single(card => CardName(card) == "bold");
        InvokeCard(bold, 0);
        Require(OpenClipboard(0), "open restored rich text");
        try { Require(ReadGlobal(GetClipboardData(Rtf), 128) is { Length: > 0 }, "preserve rich text"); }
        finally { CloseClipboard(); }

        string path = Path.Combine(SettingsStore.DataDirectory, "from-explorer.step");
        File.WriteAllText(path, "drop fixture");
        try
        {
            uint sequence = GetClipboardSequenceNumber();
            var dropped = new System.Windows.DataObject();
            dropped.SetData(System.Windows.DataFormats.FileDrop, new[] { path });
            Require(shelf.AcceptDrop(dropped), "import via the drop handler");
            categories.SelectedIndex = (int)ShelfFilter.Files;
            await Until(() => Cards(shelf).Any(card => CardName(card) == "from-explorer.step"));
            Require(GetClipboardSequenceNumber() == sequence && HasText(Cards(shelf).Single(), "STEP"),
                "file cards show the extension, and drops leave the Windows clipboard untouched");
            InvokeCard(Cards(shelf).Single(), 0);
            await Until(() => GetClipboardSequenceNumber() != sequence);
            var restored = ClipboardCapture.Read(GetClipboardSequenceNumber(), ClipboardKind.All).Item!;
            Require(restored.Paths.SequenceEqual(new[] { path }), "restored original file path");
            var clear = (System.Windows.Controls.Button)shelf.FindName("ClearShelf");
            ((System.Windows.Controls.MenuItem)clear.ContextMenu.Items[0]).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            await Until(() => Rows(shelf).Items.Count == 0);
            categories.SelectedIndex = (int)ShelfFilter.All;
            Require(Rows(shelf).Items.Count == 3 && File.Exists(path), "scoped clear preserves other records and the original file");
        }
        finally { File.Delete(path); }

        var source = new ClipboardTransfer(new ClipboardItem(ClipboardKind.Text, "virtual", "virtual",
            [new(Unicode, -1, Encoding.Unicode.GetBytes("virtual\0"))], [], null, null, false, "virtual"), dragging: true);
        Require(OleSetClipboard(source) >= 0, "publish indexed OLE file");
        categories.SelectedIndex = (int)ShelfFilter.Files;
        await Until(() => Cards(shelf).Any(card => CardName(card) == "Текст.txt"));
        InvokeCard(Cards(shelf).Single(), 0);
        Require(OleGetClipboard(out var virtualFile) >= 0 && virtualFile is not null, "open restored indexed OLE file");
        var request = Format(Contents, 0, TYMED.TYMED_ISTREAM);
        virtualFile!.GetData(ref request, out var stream);
        try { Require(stream.tymed == TYMED.TYMED_ISTREAM, "virtual file stream survived the shelf"); }
        finally { ReleaseStgMedium(ref stream); }

        categories.SelectedIndex = (int)ShelfFilter.Images;
        Set(owner, (Dib, MakeDib(3840, 2160, 12)), (Unicode, Encoding.Unicode.GetBytes("https://example.invalid/image\0")));
        await Until(() => Cards(shelf).Any(card => CardName(card) == "Изображение"));
        Require(Rows(shelf).Items.Count == 1 && count.Text == "1", "image and source URL form one image entry");
        InvokeCard(Cards(shelf).Single(), 0);
        Require(OpenClipboard(0), "open restored bitmap");
        try
        {
            var dib = ReadGlobal(GetClipboardData(Dib), HistoryStore.MaxBytes)!;
            Require(BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)) == 3840 &&
                Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8))) == 2160, "restored full-size original");
        }
        finally { CloseClipboard(); }

        categories.SelectedIndex = (int)ShelfFilter.All;
        await CheckLayouts(widget, enabled, shelf, handle);
        widget.Configure(enabled);

        categories.SelectedIndex = (int)ShelfFilter.Text;
        widget.Configure(enabled with { KeepBetweenSessions = true });
        string marker = "encrypted-" + Guid.NewGuid().ToString("N");
        var textDrop = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, marker);
        Require(shelf.AcceptDrop(textDrop), "drop text directly into history");
        await Until(() => Cards(shelf).Any(card => CardName(card) == marker));
        string dataFile = Path.Combine(SettingsStore.DataDirectory, "clipboard-history.dat");
        await Until(() => File.Exists(dataFile));
        Require(!Encoding.UTF8.GetString(File.ReadAllBytes(dataFile)).Contains(marker, StringComparison.Ordinal), "journal contains no plain text");
        widget.Dispose();

        var journal = new HistoryJournal(dataFile);
        var expired = ClipboardCapture.FromDrop(new([], "expired entry"))!;
        var saved = journal.Load();
        Require(saved.Any(entry => entry.Item.TextPreview == marker), "saved history can be decrypted");
        journal.Save([.. saved.Where(entry => entry.Item.TextPreview == marker),
            new HistoryEntry(expired, DateTime.UtcNow.AddHours(-2))]);
        using var reopened = new HistoryWidget(dispatcher);
        reopened.Configure(enabled with { KeepBetweenSessions = true, ExpireHours = 1, Edge = ClipboardEdge.Left });
        var again = Shelf()!;
        again.Open();
        await Until(() => Cards(again).Any(card => CardName(card) == marker));
        Require(Rows(again).Items.Count == 1, "reload session and auto-delete expired entry");
        reopened.Configure(enabled with { Enabled = false });
        Require(!again.IsVisible && !reopened.Active && PanelPlacement.Handle(again) == 0,
            "disable destroys the shelf window and removes the clipboard listener");
        await Until(() => !File.Exists(dataFile));
        File.WriteAllBytes(dataFile, [0x57, 0x53]);
        reopened.Configure(enabled with { KeepBetweenSessions = true });
        var enabledAgain = Shelf()!;
        Require(enabledAgain != again && reopened.Active && enabledAgain.IsVisible, "re-enabling starts a fresh shelf");
        Set(owner, (Unicode, Encoding.Unicode.GetBytes("after-truncation\0")));
        await Until(() =>
        {
            try { return journal.Load().Any(entry => entry.Item.TextPreview == "after-truncation"); }
            catch (EndOfStreamException) { return false; }
        });
        Require(OleSetClipboard(source) >= 0, "publish a virtual file before disabling history");
        enabledAgain.Open();
        await Until(() => Cards(enabledAgain).Any(card => CardName(card) == "Текст.txt"));
        InvokeCard(Cards(enabledAgain).Single(card => CardName(card) == "Текст.txt"), 0);
        reopened.Configure(enabled with { Enabled = false });
        Require(OleGetClipboard(out var whileDisabled) >= 0 && whileDisabled is not null,
            "disable leaves the restored virtual file in the system clipboard");
        whileDisabled!.GetData(ref request, out var remainingContent);
        try { Require(remainingContent.tymed == TYMED.TYMED_ISTREAM, "restored OLE content remains usable while history is disabled"); }
        finally { ReleaseStgMedium(ref remainingContent); }
        await Until(() => !File.Exists(dataFile));
        Console.WriteLine("Native clipboard, hover, filters, search, inbound files/text, scoped clear, formatted text, OLE, image restore and encrypted history passed.");
    }

    private static async Task CheckLayouts(HistoryWidget widget, ClipboardHistoryOptions enabled, ClipboardShelf shelf, nint handle)
    {
        foreach (var edge in Enum.GetValues<ClipboardEdge>())
        {
            widget.Configure(enabled with { Edge = edge });
            shelf.Preview();
            await Until(() => shelf.Expansion == 1);
            shelf.UpdateLayout();
            var scroller = Rows(shelf);
            bool horizontal = edge is ClipboardEdge.Top or ClipboardEdge.Bottom;
            var monitor = Screen.PrimaryScreen!;
            var work = monitor.WorkingArea;
            double scale = PanelPlacement.Scale(monitor);
            var origin = PanelPlacement.Location(shelf);
            bool scrollsHorizontally = System.Windows.Controls.ScrollViewer.GetHorizontalScrollBarVisibility(scroller) !=
                System.Windows.Controls.ScrollBarVisibility.Disabled;
            Require(PanelPlacement.Handle(shelf) == handle && (horizontal ? shelf.Width > shelf.Height : shelf.Height > shelf.Width) &&
                scrollsHorizontally == horizontal,
                "edge sets direction on the same window");
            Require(horizontal ? Math.Abs(origin.X + shelf.Width * scale / 2 - (work.Left + work.Width / 2d)) <= 1 :
                Math.Abs(origin.Y + shelf.Height * scale / 2 - (work.Top + work.Height / 2d)) <= 1,
                "the shelf window is centered on its monitor");
            var field = (FrameworkElement)shelf.FindName("SearchSlot");
            var filters = (System.Windows.Controls.ListBox)shelf.FindName("Categories");
            var toolbar = (FrameworkElement)shelf.FindName("Toolbar");
            Require(Math.Abs(field.TranslatePoint(new System.Windows.Point(field.ActualWidth / 2, 0), toolbar).X -
                toolbar.ActualWidth / 2) <= 1 && filters.Items.Count == 6 &&
                filters.ItemContainerGenerator.ContainerFromIndex(3) is System.Windows.Controls.ListBoxItem
                { ToolTip: "Изображения" }, "search is centered and all icon filters are labelled");
            if (Environment.GetEnvironmentVariable("WUMO_SHELF_SNAPSHOT") is { Length: > 0 } path)
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(RenderShelf(shelf)));
                using var file = File.Create(Path.Combine(Path.GetDirectoryName(path)!, $"wumo-shelf-{edge.ToString().ToLowerInvariant()}.png"));
                png.Save(file);
            }
            ((System.Windows.Controls.Button)shelf.FindName("CloseShelf")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Until(() => !shelf.IsExpanded && shelf.Expansion == 0 && shelf.IsVisible);
        }
        shelf.Open();
        await Until(() => shelf.Expansion == 1);
        Console.WriteLine("Centered showcase, icon filters and the same shelf HWND verified across all four edges.");
    }

    private static RenderTargetBitmap RenderShelf(ClipboardShelf shelf)
    {
        var panel = (FrameworkElement)shelf.Content;
        var snapshot = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        snapshot.Render(panel);
        return snapshot;
    }
    private static void CheckSettings(ClipboardHistoryOptions enabled)
    {
        SettingsStore.Save(new AppSettings { ThumbnailsWidgetEnabled = false, QuickPreviewSize = 768 });
        var window = new SettingsForm();
        try
        {
            int clipboardChanges = 0;
            window.ClipboardOptionsSaved += _ => clipboardChanges++;
            window.Saved += (_, _) => throw new InvalidOperationException("clipboard invoked thumbnail settings apply");
            window.Show();
            var tab = (System.Windows.Controls.RadioButton)window.FindName("ClipboardChoice");
            tab.IsChecked = true;
            Require(((System.Windows.Controls.ScrollViewer)window.FindName("ClipboardPage")).Visibility == Visibility.Visible, "widget page");
            var toggle = (System.Windows.Controls.CheckBox)window.FindName("ClipboardSwitch");
            Require(toggle.IsChecked == false, "widget defaults off");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var after = SettingsStore.Load();
            Require(clipboardChanges == 1 && after.ClipboardHistory == enabled &&
                !after.ThumbnailsWidgetEnabled && after.QuickPreviewSize == 768, "live settings independent of 3D");
            ((System.Windows.Controls.ComboBox)window.FindName("ClipboardDockEdge")).SelectedIndex = (int)ClipboardEdge.Bottom;
            bool preview = false;
            window.ClipboardPreviewRequested += () => preview = true;
            ((System.Windows.Controls.Button)window.FindName("ClipboardPreview")).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(clipboardChanges == 2 && preview &&
                SettingsStore.Load().ClipboardHistory == (enabled with { Edge = ClipboardEdge.Bottom }),
                "saved edge applies immediately and the preview command is retained");
        }
        finally { window.CloseForShutdown(); }
    }

    private static IEnumerable<System.Windows.Controls.Border> Cards(DependencyObject root)
    {
        if (root is System.Windows.Controls.Border { ContextMenu: not null } card)
            yield return card;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var nested in Cards(VisualTreeHelper.GetChild(root, index)))
                yield return nested;
    }

    private static string CardName(System.Windows.Controls.Border card) =>
        System.Windows.Automation.AutomationProperties.GetName(card);

    private static bool HasText(DependencyObject root, string expected)
    {
        if (root is System.Windows.Controls.TextBlock { IsVisible: true } text && text.Text.StartsWith(expected, StringComparison.Ordinal))
            return true;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (HasText(VisualTreeHelper.GetChild(root, index), expected))
                return true;
        return false;
    }

    private static void InvokeCard(System.Windows.Controls.Border card, int command) =>
        ((System.Windows.Controls.MenuItem)card.ContextMenu.Items[command]).RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));

    private static ClipboardShelf? Shelf() => System.Windows.Application.Current.Windows.OfType<ClipboardShelf>().LastOrDefault();
    private static System.Windows.Controls.ListBox Rows(ClipboardShelf shelf) =>
        (System.Windows.Controls.ListBox)shelf.FindName("Entries");
    private static async Task Until(Func<bool> complete)
    {
        var timeout = Stopwatch.StartNew();
        while (!complete())
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(5), "wait for clipboard update");
            await Task.Delay(15);
        }
    }

    internal static void CheckTransfer()
    {
        Require(OleInitialize(0) >= 0, "initialize OLE");
        try
        {
            byte[] unicode = Encoding.Unicode.GetBytes("привет\0");
            var item = new ClipboardItem(ClipboardKind.Text, "key", "привет",
                [new(Unicode, -1, unicode), new(Rtf, -1, Encoding.ASCII.GetBytes(@"{\rtf1 test}" + "\0"))], [], null, null, false, "привет");
            var drop = new ClipboardTransfer(item, dragging: true);
            var text = ReadTransfer(drop, Unicode, -1);
            Require(text.AsSpan(0, unicode.Length).SequenceEqual(unicode), "Unicode drop");
            Require(Encoding.UTF8.GetString(ReadTransfer(drop, Contents, 0), 0, 12) == "привет", "virtual TXT drop");
            Require(ReadTransfer(drop, Rtf, -1)[0] == '{', "formatted text drop");
            byte[]? plain = ClipboardText.PlainFromRich([new ClipboardPayload(Rtf, -1, Encoding.ASCII.GetBytes(@"{\rtf1\b bold}"))]);
            Require(plain is not null && Encoding.Unicode.GetString(plain) == "bold\0", "rich-only clipboard can produce plain text");

            var image = new ClipboardPayload(Dib, -1, MakeDib(3840, 2160, 0));
            var thumbnail = ClipboardImages.Thumbnail(image);
            Require(thumbnail is { PixelWidth: 320, PixelHeight: 180 }, "bounded 16:9 thumbnail");
            var bottomUp = MakeDib(2, 2, 11);
            bottomUp[48] = 37;
            var previewPixels = new byte[16];
            ClipboardImages.Thumbnail(new(Dib, -1, bottomUp))!.CopyPixels(previewPixels, 8, 0);
            Require(previewPixels[0] == 37 && previewPixels[8] == 11, "DIB thumbnail preserves orientation");
            byte[] withAlpha = [40, 60, 80, 128, 17, 18, 19, 255, 30, 40, 50, 64, 70, 80, 90, 255];
            var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, withAlpha, 8);
            var dibV5 = ClipboardImages.FromBitmap(source, 1024)!;
            ClipboardImages.Thumbnail(dibV5)!.CopyPixels(previewPixels, 8, 0);
            Require(withAlpha.AsSpan().SequenceEqual(previewPixels), "DIB V5 thumbnail preserves orientation and straight alpha");
            var png = ClipboardImages.EncodePng(image);
            var frame = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Require(frame.PixelWidth == 3840 && frame.PixelHeight == 2160, "full-size PNG drop");
            Console.WriteLine("OLE format offers, full-size PNG, bounded thumbnail and virtual TXT passed.");
        }
        finally { OleUninitialize(); }
    }

    private static byte[] ReadTransfer(ClipboardTransfer transfer, uint id, int index)
    {
        var format = Format(id, index, TYMED.TYMED_HGLOBAL);
        Require(transfer.QueryGetData(ref format) == 0, "query drop format");
        transfer.GetData(ref format, out var storage);
        try { return ReadGlobal(storage.unionmember, 1024 * 1024)!; }
        finally { ReleaseStgMedium(ref storage); }
    }

    private static byte[] MakeDib(int width, int height, byte color)
    {
        byte[] content = new byte[40 + width * height * 4];
        BinaryPrimitives.WriteInt32LittleEndian(content, 40);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(8), height);
        BinaryPrimitives.WriteInt16LittleEndian(content.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(content.AsSpan(14), 32);
        content[40] = color;
        return content;
    }

    private static void Set(nint owner, params (uint Format, byte[] Bytes)[] formats)
    {
        Require(OpenClipboard(owner), "open clipboard to publish fixture");
        try
        {
            Require(EmptyClipboard(), "empty fixture clipboard");
            foreach (var (format, bytes) in formats)
            {
                nint memory = WriteGlobal(bytes);
                if (SetClipboardData(format, memory) == 0)
                {
                    GlobalFree(memory);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "publish fixture");
                }
            }
        }
        finally { CloseClipboard(); }
    }

    private static void Require(bool success, string operation)
    {
        if (!success)
            throw new InvalidOperationException(operation);
    }

    private static void NativeRequire(bool success, string operation)
    {
        if (!success)
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowStation(string name, uint flags, uint access, nint attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktop(string name, nint device, nint mode, uint flags, uint access, nint attributes);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder data, int size, out int needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes,
        bool inherit, int flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exit);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal int X, Y, Width, Height, Columns, Rows, Fill, Flags;
        internal ushort Show, ExtraSize;
        internal nint Extra, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process, Thread;
        internal int ProcessId, ThreadId;
    }
}

