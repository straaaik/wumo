using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using PixelThumbs;

internal static class Program
{
    private static bool isolatedPreview;
    private static readonly string[] UnsupportedFiles = ["Unsupported.txt", "ExampleProgram.exe", "ExampleMovie.mov", "ExamplePhoto.jpeg"];
    private static readonly Condition FileItems = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--check-launch", var older, var newer])
            return CheckLaunch(older, newer);
        if (args is ["--check-restart", var installed, var update])
            return CheckRestart(installed, update);
        if (args is ["--host-preview"])
            return HostPreview();
        if (args is ["--check-preview-content"])
            return CheckPreviewContent();
        if (args is ["--check-preview-idle", .. var idleOptions])
            return CheckPreviewIdle(benchmarkOnly: idleOptions.Contains("--benchmark"));

        if (args is not [var executable, .. var options] || !File.Exists(executable))
            return 2;
        isolatedPreview = options.Contains("--isolated-preview");
        bool selectionApiOnly = options.Contains("--check-selection-api");
        if (!isolatedPreview && !selectionApiOnly && Process.GetProcessesByName("PixelThumbs").Length != 0)
        {
            Console.Error.WriteLine("Close PixelThumbs before running: PreviewChecks <PixelThumbs.exe> [--desktop] [--require-hidden] [CAD fixtures ...]");
            return 2;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string root = Path.Combine(Path.GetTempPath(), $"PixelThumbsPreview-{Guid.NewGuid():N}");
        string models = Path.Combine(root, "PixelThumbs preview " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(models);
        var originalCursor = Cursor.Position;
        nint explorer = 0;
        try
        {
            WriteFixtures(models);
            using var shell = Process.Start("explorer.exe", $"/n,\"{models}\"");
            var window = WaitFor(() => FindByName(AutomationElement.RootElement, Path.GetFileName(models), TreeScope.Children), "fixture folder");
            explorer = (nint)window.Current.NativeWindowHandle;
            if (selectionApiOnly)
            {
                CheckNativeSelection(window, explorer, models);
                return 0;
            }
            if (options.Contains("--wait-for-focus"))
            {
                Console.WriteLine($"Waiting for focus in {Path.GetFileName(models)}.");
                WaitFor(() => GetForegroundWindow() == explorer ? window : null, "externally activated fixture window", 60000);
            }
            else
                Assert(FocusExplorer(explorer), "The fixture Explorer window could not take focus.");
            string profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
            CheckPreview(executable, profile, window, models, mouse: false, requireHidden: options.Contains("--require-hidden"));
            CheckPreview(executable, profile, window, models, mouse: true);
            if (options.Where(option => option is not ("--desktop" or "--require-hidden" or "--isolated-preview" or "--wait-for-focus")).ToArray() is { Length: > 0 } fixtures)
                CheckCadPreviews(executable, profile, window, models, fixtures);
            if (options.Contains("--desktop"))
                CheckDesktop(executable, profile, models);
            if (!isolatedPreview)
                CheckSettings(executable, profile);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Cursor.Position = originalCursor;
            if (explorer != 0)
                PostMessage(explorer, 0x10, 0, 0); // WM_CLOSE on the dedicated fixture window
            Directory.Delete(root, recursive: true);
        }
    }

    private static int HostPreview()
    {
        // Host the exact packaged hook in a separate process, without starting a second tray app.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var dispatch = new Control();
        _ = dispatch.Handle;
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "PixelThumbs.dll"));
        var type = assembly.GetType("PixelThumbs.QuickPreviewHotkey", throwOnError: true)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var hook = (IDisposable)Activator.CreateInstance(type, flags, null, [dispatch], null)!;
        type.GetMethod("Configure", flags)!.Invoke(hook, [SettingsStore.Load()]);
        Assert(type.GetField("hook", flags)!.GetValue(hook) is not null, "The packaged keyboard hook could not start.");
        File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "hook-ready"), "");
        Application.Run();
        return 0;
    }

    private static int CheckPreviewIdle(bool benchmarkOnly)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "PixelThumbs.dll"));
        var type = assembly.GetType("PixelThumbs.QuickPreviewHotkey", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var dispatch = new Control();
        _ = dispatch.Handle;
        using var hook = (IDisposable)Activator.CreateInstance(type, flags, null, [dispatch], null)!;
        var timer = (System.Windows.Forms.Timer)type.GetField("pointerTimer", flags)!.GetValue(hook)!;
        int ticks = 0;
        timer.Tick += (_, _) => ticks++;
        bool idle = true;
        foreach (int key in new[] { QuickPreviewKeys.Space, QuickPreviewKeys.MouseBack })
        {
            type.GetMethod("Configure", flags)!.Invoke(hook, [new AppSettings { QuickPreviewKey = key }]);
            var input = type.GetField("hook", flags)!.GetValue(hook)!;
            nint mouse = (nint)input.GetType().GetField("mouse", flags)!.GetValue(input)!;
            Assert((mouse == 0) == (key == QuickPreviewKeys.Space), "Only pointer bindings need a global mouse hook.");
            Pump(250);
            ticks = 0;
            Pump(3000);
            Console.WriteLine($"{QuickPreviewKeys.DisplayName(key)}: {ticks} timer ticks in 3 seconds at rest.");
            idle &= ticks == 0;
        }
        type.GetMethod("Configure", flags)!.Invoke(hook, [new AppSettings { QuickPreview = false }]);
        Assert(type.GetField("hook", flags)!.GetValue(hook) is null && !timer.Enabled, "Disabling preview releases input monitoring.");
        return idle || benchmarkOnly ? 0 : 1;

        static void Pump(int milliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < milliseconds)
            {
                Application.DoEvents();
                Thread.Sleep(8);
            }
        }
    }

    private static void CheckNativeSelection(AutomationElement window, nint explorer, string models)
    {
        nint view = 0;
        EnumChildWindows(explorer, (child, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() != "SHELLDLL_DefView" || !IsWindowVisible(child))
                return true;
            view = child;
            return false;
        }, 0);
        Assert(view != 0, "The fixture does not expose an Explorer file pane.");
        var context = new QuickPreviewTarget.FocusedView(explorer, view, false);
        foreach (string file in new[] { "A.stl", "B.stl", "Folder.stl" }.Concat(UnsupportedFiles))
        {
            string label = Path.GetFileNameWithoutExtension(file);
            var item = WaitFor(() => FindByName(window, file) ?? FindByName(window, label), file);
            var selection = (SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern);
            selection.Select();
            string? expected = file == "Folder.stl" || UnsupportedFiles.Contains(file) ? null : Path.Combine(models, file);
            string? selected = QuickPreviewTarget.ResolveSelection(context);
            Assert(expected == selected, $"Selection expected {expected ?? "no file"}, received {selected ?? "no file"}.");
            selection.RemoveFromSelection();
            Assert(QuickPreviewTarget.ResolveSelection(context) is null, "The unselected file remained a preview target.");
        }
        Console.WriteLine("PASS Explorer selection returns exact model paths and ignores other files, folders and empty selections.");
    }

    private static int CheckPreviewContent()
    {
        var temp = Directory.CreateTempSubdirectory("PixelThumbsPreviewContent-");
        try
        {
            WriteFixtures(temp.FullName);
            var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "PixelThumbs.dll"));
            CheckPreviewWindow(assembly);
            CheckSettingsPixels(assembly);
            CheckNonBlockingCancellation(assembly);
            var type = assembly.GetType("PixelThumbs.QuickPreviewHotkey", throwOnError: true)!;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using var dispatch = new Control();
            using var hook = (IDisposable)Activator.CreateInstance(type, flags, null, [dispatch], null)!;
            var load = type.GetMethod("RenderFile", flags)!;
            var formats = FormatCatalog.Entries.ToDictionary(entry => entry.Extension,
                entry => new FormatSettings { Enabled = entry.Extension != ".obj" }, StringComparer.OrdinalIgnoreCase);
            var settings = new AppSettings { QuickPreviewSize = 384, Formats = formats };
            foreach (var (file, reason) in new (string, string?)[]
            {
                ("A.stl", "корректной геометрии"), ("Empty.stl", "Файл пуст"),
                ("A.obj", "отключён в настройках"), ("B.stl", null), ("Binary.stl", null)
            })
            {
                var result = ((Bitmap? Image, string Properties, string Reason))load.Invoke(hook,
                    [Path.Combine(temp.FullName, file), settings, CancellationToken.None])!;
                using var image = result.Image;
                if (reason is null)
                    Assert(image is { Width: 384, Height: 384 }, $"{file} has no rendered image: {result.Reason}");
                else
                    Assert(image is null && result.Reason.Contains(reason) && result.Properties.Contains("Размер") &&
                        result.Properties.Contains("Изменён"), $"{file} has incorrect fallback content: {result}");
                Console.WriteLine($"PASS {file}: {reason ?? "384×384 image"}.");
            }
            string changed = Path.Combine(temp.FullName, "B.stl");
            File.WriteAllText(changed, "corrupt replacement");
            var updated = ((Bitmap? Image, string Properties, string Reason))load.Invoke(hook,
                [changed, settings, CancellationToken.None])!;
            using var cachedImage = updated.Image;
            Assert(cachedImage is null && updated.Reason.Contains("корректной геометрии"),
                "A changed model reused the cached thumbnail.");
            Console.WriteLine("PASS a changed file invalidates its preview cache entry.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { temp.Delete(recursive: true); }
    }

    private static void CheckPreviewWindow(Assembly assembly)
    {
        var type = assembly.GetType("PixelThumbs.QuickPreviewForm", throwOnError: true)!;
        var screen = SystemInformation.VirtualScreen;
        using var preview = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [384, Screen.PrimaryScreen!.WorkingArea.Location, "B.stl"], null)!;
        preview.Location = new Point(screen.Right + 64, screen.Top);
        nint foreground = GetForegroundWindow();
        preview.Show();
        nint handle = preview.Handle;
        Assert(IsWindowVisible(handle), "The quick preview window was not shown immediately.");
        Assert(GetLayeredWindowAttributes(handle, out _, out byte alpha, out uint flags) && (flags & 2) != 0 && alpha == 255,
            $"The quick preview window must start at full opacity. Actual alpha: {alpha}.");
        Assert(GetForegroundWindow() == foreground, "Showing the quick preview took foreground focus.");
        preview.Dispose();
        Assert(!IsWindow(handle), "Disposing the quick preview left its window open.");
        Console.WriteLine("PASS quick preview starts fully opaque and closes immediately without taking focus.");
    }

    private static void CheckSettingsPixels(Assembly assembly)
    {
        using var bitmap = new Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.FromArgb(128, 140, 90, 20));
        bitmap.SetPixel(1, 1, Color.FromArgb(255, 123, 45, 67));
        var method = assembly.GetType("PixelThumbs.SettingsForm")!.GetMethod("ToImageSource",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(Bitmap)])!;
        var source = (System.Windows.Media.Imaging.BitmapSource)method.Invoke(null, [bitmap])!;
        byte[] pixels = new byte[16];
        source.CopyPixels(pixels, 8, 0);
        Assert(source.IsFrozen && source.Format == System.Windows.Media.PixelFormats.Pbgra32 &&
            pixels.AsSpan(0, 4).SequenceEqual(new byte[] { 10, 45, 70, 128 }) &&
            pixels.AsSpan(12, 4).SequenceEqual(new byte[] { 67, 45, 123, 255 }),
            $"The WPF transfer changed colors, alpha or the image orientation: {string.Join(", ", pixels)}.");
        Console.WriteLine("PASS WPF transfer preserves pixel orientation, color and alpha.");
    }

    private static void CheckNonBlockingCancellation(Assembly assembly)
    {
        var type = assembly.GetType("PixelThumbs.PreviewCancellation", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var request = (IAsyncDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        var token = (CancellationToken)type.GetProperty("Token", flags)!.GetValue(request)!;
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var registration = token.Register(() => { entered.Set(); release.Wait(1500); });
        try
        {
            var watch = Stopwatch.StartNew();
            type.GetMethod("Cancel", flags)!.Invoke(request, null);
            Assert(token.IsCancellationRequested && watch.ElapsedMilliseconds < 500 && entered.Wait(500),
                "Cancelling a preview waited for its worker callback on the caller thread.");
            Console.WriteLine("PASS preview cancellation returns while the worker callback is blocked.");
        }
        finally
        {
            release.Set();
            request.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void WriteFixtures(string directory)
    {
        const string stl = """
            solid Tetrahedron
            facet normal 0 0 -1
              outer loop
                vertex 0 0 0
                vertex 0 1 0
                vertex 1 0 0
              endloop
            endfacet
            facet normal 0 -1 0
              outer loop
                vertex 0 0 0
                vertex 1 0 0
                vertex 0 0 1
              endloop
            endfacet
            facet normal -1 0 0
              outer loop
                vertex 0 0 0
                vertex 0 0 1
                vertex 0 1 0
              endloop
            endfacet
            facet normal 1 1 1
              outer loop
                vertex 1 0 0
                vertex 0 1 0
                vertex 0 0 1
              endloop
            endfacet
            endsolid Tetrahedron
            """;
        File.WriteAllText(Path.Combine(directory, "A.stl"), "A marks the selection. It cannot render.");
        File.WriteAllText(Path.Combine(directory, "A.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        File.WriteAllText(Path.Combine(directory, "B.stl"), stl);
        using (var binary = new BinaryWriter(File.Create(Path.Combine(directory, "Binary.stl"))))
        {
            binary.Write(new byte[80]);
            binary.Write(1u);
            foreach (float value in new float[] { 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0 })
                binary.Write(value);
            binary.Write((ushort)0);
        }
        foreach (string file in UnsupportedFiles)
            File.WriteAllBytes(Path.Combine(directory, file), []);
        File.WriteAllBytes(Path.Combine(directory, "Empty.stl"), []);
        Directory.CreateDirectory(Path.Combine(directory, "Folder.stl"));
    }

    private static void CheckPreview(string executable, string profile, AutomationElement window, string models, bool mouse,
        bool requireHidden = false)
    {
        int binding = mouse ? QuickPreviewKeys.MouseBack : QuickPreviewKeys.Space;
        WriteProfile(profile, binding, 384);
        using var app = StartApp(executable, profile, startup: true);
        try
        {
            nint foreground = (nint)window.Current.NativeWindowHandle;
            Assert(FocusExplorer(foreground), "The test could not reactivate the fixture Explorer window.");
            var a = FindFile(window, models, "A.stl");
            var b = FindFile(window, models, "B.stl");
            Select(a);
            Point pointer = HitPoint(b);
            Assert(foreground == GetForegroundWindow(), "Explorer lost focus before the hold.");
            Assert(Resolve(HitPoint(a)) == Path.Combine(models, "A.stl"), "Resolve the first of two names with the same stem.");
            var obj = FindFile(window, models, "A.obj");
            Assert(Resolve(HitPoint(obj)) == Path.Combine(models, "A.obj"), "Resolve the other extension by shell identity.");
            if (requireHidden)
                Assert(a.Current.Name == "A" && obj.Current.Name == "A", "Explorer did not hide both fixture extensions.");
            if (!mouse)
                Console.WriteLine($"Same stem: Explorer displays '{a.Current.Name}' and '{obj.Current.Name}', exact paths verified.");
            Assert(Resolve(pointer) == Path.Combine(models, "B.stl"), "The hover resolver must return the pointed-to file.");
            Assert(Resolve(HitPoint(FindFile(window, models, "Unsupported.txt", allowUnresolved: true))) is null,
                "Unsupported file must not fall back to the selection.");
            Assert(Resolve(HitPoint(FindFile(window, models, "Folder.stl", allowUnresolved: true))) is null,
                "Folder must not fall back to the selection.");

            var empty = FindEmptyPoint(b);
            Assert(Resolve(empty) is null, "Empty space must not fall back to the selection.");
            if (!mouse)
                CheckSelectedFiles(app.Id, foreground, window, models, a, b);
            Cursor.Position = empty;
            if (mouse)
                ExpectNoPreview(app.Id, binding);

            Cursor.Position = mouse ? pointer : empty;
            Thread.Sleep(500);
            double cold = CheckHold(app.Id, foreground, binding, expectedSize: 384, expectedName: "B.stl");
            double warm = CheckHold(app.Id, foreground, binding, expectedSize: 384, expectedName: "B.stl");

            // The first down/up pair may finish before rendering starts. The next hold must still work.
            Input(binding, down: true);
            Input(binding, down: false);
            Thread.Sleep(450);
            Assert(VisibleWindow(app.Id, overlay: true) == 0, "A released button caused a delayed preview.");
            CheckHold(app.Id, foreground, binding, expectedSize: 384, expectedName: "B.stl");
            Console.WriteLine($"{QuickPreviewKeys.DisplayName(binding)}: cold {cold:F0} ms, warm {warm:F0} ms. Foreground, release and rapid release passed.");
            if (!mouse)
            {
                CheckImmediateSpace(app.Id, foreground, b, empty);
                CheckViews(app.Id, foreground, window, models);
                var current = WaitFor(() => TryFindFile(window, models, "B.stl"), "B.stl in details");
                Cursor.Position = HitPoint(current);
                CheckAddressBar(app.Id);
                current.SetFocus();
                Thread.Sleep(200);
            }
        }
        finally { StopApp(app); }
    }

    private static void CheckSelectedFiles(int app, nint foreground, AutomationElement window, string models,
        AutomationElement bad, AutomationElement good)
    {
        Cursor.Position = HitPoint(good);
        Select(bad);
        Assert(QuickPreviewTarget.GetFocusedView() is { } view && view.Window == foreground &&
            QuickPreviewTarget.ResolveSelection(view) == Path.Combine(models, "A.stl"),
            "The selected STL did not resolve in the focused file pane.");
        Thread.Sleep(220);
        CheckHold(app, foreground, QuickPreviewKeys.Space, 384, expectedName: "A.stl", expectedReason: "корректной геометрии");
        Select(FindFile(window, models, "Empty.stl"));
        CheckHold(app, foreground, QuickPreviewKeys.Space, 384, expectedName: "Empty.stl", expectedReason: "Файл пуст");
        foreach (string file in UnsupportedFiles)
        {
            Select(FindFile(window, models, file, allowUnresolved: true));
            ExpectNoPreview(app, QuickPreviewKeys.Space);
        }
        Select(FindFile(window, models, "Binary.stl"));
        CheckHold(app, foreground, QuickPreviewKeys.Space, 384, expectedName: "Binary.stl");
        var folder = FindFile(window, models, "Folder.stl", allowUnresolved: true);
        Select(folder);
        ExpectNoPreview(app, QuickPreviewKeys.Space);
        var folderSelection = (SelectionItemPattern)folder.GetCurrentPattern(SelectionItemPattern.Pattern);
        folderSelection.RemoveFromSelection();
        Assert(!folderSelection.Current.IsSelected, "The fixture folder remained selected.");
        Cursor.Position = HitPoint(good);
        ExpectNoPreview(app, QuickPreviewKeys.Space);
        Select(good);
        Cursor.Position = HitPoint(bad);
        CheckHold(app, foreground, QuickPreviewKeys.Space, 384, expectedName: "B.stl");
        Console.WriteLine("Selected model overrides hover. File errors, unsupported files, folder and empty selection passed.");
    }

    private static void Select(AutomationElement item)
    {
        item.SetFocus();
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    }

    private static void ExpectNoPreview(int app, int binding)
    {
        Input(binding, down: true);
        try
        {
            Thread.Sleep(250);
            Assert(VisibleWindow(app, overlay: true) == 0, "An unselected item or folder triggered a preview.");
        }
        finally { Input(binding, down: false); }
    }

    private static void CheckImmediateSpace(int app, nint foreground, AutomationElement target, Point empty)
    {
        Select(target);
        Point point = HitPoint(target);
        var samples = new List<double>();
        int misses = 0;
        const int attempts = 16;
        for (int i = 0; i < attempts; i++)
        {
            Cursor.Position = empty;
            Thread.Sleep(80 + i * 3);
            Cursor.Position = new Point(point.X + i % 4, point.Y);
            try { samples.Add(CheckHold(app, foreground, QuickPreviewKeys.Space, expectedSize: 384, timeout: 750, expectedName: "B.stl")); }
            catch (TimeoutException) { misses++; }
        }
        Console.WriteLine($"Immediate Space after moving from empty space: {samples.Count}/{attempts}, " +
            $"maximum {(samples.Count == 0 ? 0 : samples.Max()):F0} ms.");
        Assert(misses == 0, $"{misses} immediate Space presses did not open a preview.");
    }

    private static void CheckAddressBar(int app)
    {
        Shortcut('L', 0x11);
        var address = WaitFor(() =>
        {
            var focused = AutomationElement.FocusedElement;
            return focused.TryGetCurrentPattern(ValuePattern.Pattern, out object value) ? value as ValuePattern : null;
        }, "focused address bar");
        address.SetValue("PixelThumbs");
        Tap(QuickPreviewKeys.Space);
        Thread.Sleep(100);
        Assert(address.Current.Value.Contains(' ') && VisibleWindow(app, overlay: true) == 0,
            "The preview binding did not preserve ordinary space entry in the Explorer address bar.");
        Tap(QuickPreviewKeys.Escape);
        Console.WriteLine("Space retained normal editing in the Explorer address bar.");
    }

    private static void CheckViews(int app, nint foreground, AutomationElement window, string models)
    {
        Shortcut('5', 0x11, 0x10); // Ctrl+Shift+5, list
        var list = WaitFor(() =>
        {
            var item = TryFindFile(window, models, "B.stl");
            return item?.Current.BoundingRectangle is { Width: < 350, Height: < 60 } ? item : null;
        }, "list item");
        Select(list);
        var pointer = HitPoint(list);
        WaitFor(() => Resolve(pointer) == Path.Combine(models, "B.stl") ? list : null, "the list view's exact STL");
        Cursor.Position = pointer;
        Thread.Sleep(300);
        CheckHold(app, foreground, QuickPreviewKeys.Space, expectedSize: 384);

        Shortcut('2', 0x11, 0x10); // Ctrl+Shift+2, large icons
        var icon = WaitFor(() =>
        {
            var item = TryFindFile(window, models, "B.stl");
            return item?.Current.BoundingRectangle.Height >= 80 ? item : null;
        }, "large icon");
        Select(icon);
        pointer = HitPoint(icon);
        Assert(Resolve(pointer) == Path.Combine(models, "B.stl"), "Icon view resolved a different file.");
        Cursor.Position = pointer;
        Thread.Sleep(300);
        CheckHold(app, foreground, QuickPreviewKeys.Space, expectedSize: 384);

        if (Environment.OSVersion.Version.Build >= 22621)
        {
            string tabDirectory = Directory.CreateDirectory(Path.Combine(models, "Second tab")).FullName;
            File.Copy(Path.Combine(models, "B.stl"), Path.Combine(tabDirectory, "TabTarget.stl"));
            Shortcut('T', 0x11);
            Shortcut('L', 0x11);
            WaitFor(() =>
            {
                var focused = AutomationElement.FocusedElement;
                return focused.TryGetCurrentPattern(ValuePattern.Pattern, out object value) ? value as ValuePattern : null;
            }, "address bar").SetValue(tabDirectory);
            Tap(0x0D);
            var tabItem = WaitFor(() => TryFindFile(window, tabDirectory, "TabTarget.stl"), "second tab item");
            Select(tabItem);
            pointer = HitPoint(tabItem);
            Assert(Resolve(pointer) == Path.Combine(tabDirectory, "TabTarget.stl"), "The second tab resolved a file from the first view.");
            Cursor.Position = pointer;
            Thread.Sleep(300);
            CheckHold(app, foreground, QuickPreviewKeys.Space, expectedSize: 384);
            Shortcut('W', 0x11);
            WaitFor(() => TryFindFile(window, models, "B.stl"), "first tab after closing the second");
            Console.WriteLine("The active Windows 11 Explorer tab supplied the correct preview target.");
        }
        Shortcut('6', 0x11, 0x10); // Restore details before checking the side button.
        Console.WriteLine("Explorer list and large icon views passed.");
    }

    private static void CheckCadPreviews(string executable, string profile, AutomationElement window, string models, string[] fixtures)
    {
        foreach (string fixture in fixtures)
            File.Copy(fixture, Path.Combine(models, Path.GetFileName(fixture)));

        File.WriteAllText(Path.Combine(profile, "settings.json"), JsonSerializer.Serialize(new
        {
            AutoStart = true, QuickPreviewSize = 512, F3dExecutable = @"C:\missing-old-install\f3d.exe"
        }));
        using var app = StartApp(executable, profile, startup: true);
        try
        {
            nint foreground = (nint)window.Current.NativeWindowHandle;
            Assert(FocusExplorer(foreground), "The test could not reactivate Explorer for CAD preview.");
            foreach (string fixture in fixtures)
            {
                string name = Path.GetFileName(fixture);
                var item = WaitFor(() => TryFindFile(window, models, name), name);
                if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object scroll))
                    ((ScrollItemPattern)scroll).ScrollIntoView();
                Select(item);
                var pointer = HitPoint(item);
                Assert(Resolve(pointer) == Path.Combine(models, name), "The CAD preview resolved a different file.");
                Cursor.Position = pointer;
                Thread.Sleep(300);
                double milliseconds = CheckHold(app.Id, foreground, QuickPreviewKeys.Space, expectedSize: 512, timeout: 20000);
                Console.WriteLine($"Bundled F3D preview: {name}, {milliseconds:F0} ms.");
            }
        }
        finally { StopApp(app); }
    }

    private static void CheckDesktop(string executable, string profile, string models)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string name = $"PixelThumbs desktop check {Guid.NewGuid():N}.stl";
        string path = Path.Combine(desktop, name);
        File.Copy(Path.Combine(models, "B.stl"), path);
        WriteProfile(profile, QuickPreviewKeys.Space, 512);
        using var app = StartApp(executable, profile, startup: true);
        Shortcut('D', 0x5B); // Win+D opens the desktop without navigating an existing Explorer window.
        try
        {
            nint foreground = GetForegroundWindow();
            var window = AutomationElement.FromHandle(foreground);
            var icon = WaitFor(() => TryFindFile(window, desktop, name), "desktop icon");
            Select(icon);
            var pointer = HitPoint(icon);
            Assert(Resolve(pointer) == path, "The desktop hit did not resolve the fixture PIDL.");
            Cursor.Position = pointer;
            Thread.Sleep(300);
            CheckHold(app.Id, foreground, QuickPreviewKeys.Space, expectedSize: 512);
            Console.WriteLine("The Windows desktop supplied the correct 512×512 preview.");
        }
        finally
        {
            StopApp(app);
            Shortcut('D', 0x5B);
            File.Delete(path);
        }
    }

    private static Point FindEmptyPoint(AutomationElement item)
    {
        var parent = TreeWalker.ControlViewWalker.GetParent(item);
        var view = parent.Current.BoundingRectangle;
        var items = parent.FindAll(TreeScope.Children, FileItems);
        for (int y = (int)view.Bottom - 14; y > view.Top; y -= 19)
        {
            var point = new Point((int)view.Left + 20, y);
            var physical = new System.Windows.Point(point.X, point.Y);
            if (QuickPreviewTarget.GetView(point) is not null &&
                items.Cast<AutomationElement>().All(entry => !entry.Current.BoundingRectangle.Contains(physical)))
                return point;
        }
        throw new InvalidOperationException("No empty part of the Explorer file pane was visible.");
    }

    private static double CheckHold(int id, nint foreground, int binding, int expectedSize, int timeout = 5000,
        string? expectedName = null, string? expectedReason = null)
    {
        var watch = Stopwatch.StartNew();
        Input(binding, down: true);
        double first;
        try
        {
            nint overlay = WaitFor(() =>
            {
                nint handle = VisibleWindow(id, overlay: true);
                return handle == 0 ? (nint?)null : handle;
            }, "preview window", timeout);
            GetWindowRect(overlay, out var bounds);
            Assert(bounds.Right - bounds.Left == expectedSize && bounds.Bottom - bounds.Top == expectedSize,
                "Preview has the configured square dimensions.");
            Assert((GetWindowLong(overlay, -16) & 0x00C00000) == 0, "Preview must have no standard Windows frame.");
            var window = AutomationElement.FromHandle(overlay);
            if (expectedName is not null)
                Assert(window.Current.Name == expectedName, $"Expected {expectedName}, received {window.Current.Name}.");
            WaitFor(() =>
            {
                var labels = window.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                string text = string.Join("\n", labels.Cast<AutomationElement>().Select(label => label.Current.Name));
                if (expectedReason is null)
                    return string.IsNullOrEmpty(text) ? window : null;
                return text.Contains(expectedReason, StringComparison.Ordinal) && text.Contains("Размер", StringComparison.Ordinal)
                    && text.Contains("Изменён", StringComparison.Ordinal) ? window : null;
            }, expectedReason is null ? "completed image" : $"file properties and {expectedReason}", timeout);
            first = watch.Elapsed.TotalMilliseconds;
            Assert(GetForegroundWindow() == foreground, "Preview changed the Explorer foreground window.");
        }
        finally { Input(binding, down: false); }
        Assert(SpinWait.SpinUntil(() => VisibleWindow(id, overlay: true) == 0, 200), "Preview did not close on release.");
        return first;
    }

    private static void CheckSettings(string executable, string profile)
    {
        WriteProfile(profile, QuickPreviewKeys.Space, 384);
        using var app = StartApp(executable, profile, startup: false);
        try
        {
            nint handle = WaitFor(() =>
            {
                nint window = VisibleWindow(app.Id, overlay: false);
                return window == 0 ? (nint?)null : window;
            }, "settings window");
            var window = AutomationElement.FromHandle(handle);
            Assert((GetWindowLong(handle, -16) & 0x00C00000) == 0, "Settings has a standard title bar.");
            Assert(SetForegroundWindow(handle), "Settings could not take focus for input capture.");
            CheckMove(handle, window);
            CheckMonitors(handle, window);
            Invoke(WaitFor(() => FindByName(window, "Настройки 3D Thumbnails"), "widget settings navigation"));
            var change = WaitFor(() => FindById(window, "ChangeQuickPreviewKey"), "change binding");
            var label = WaitFor(() => FindById(window, "QuickPreviewKey"), "binding label");
            Assert(label.Current.Name == QuickPreviewKeys.DisplayName(QuickPreviewKeys.Space), "Initial binding was not loaded.");
            Invoke(change);
            Tap(QuickPreviewKeys.Escape);
            Thread.Sleep(130);
            Assert(label.Current.Name == QuickPreviewKeys.DisplayName(QuickPreviewKeys.Space), "Escape changed the binding.");
            Invoke(change);
            Tap(QuickPreviewKeys.MouseBack);
            WaitFor(() => label.Current.Name == QuickPreviewKeys.DisplayName(QuickPreviewKeys.MouseBack) ? label : null, "mouse binding capture");
            Invoke(change);
            Tap(0x77); // F8
            WaitFor(() => label.Current.Name == QuickPreviewKeys.DisplayName(0x77) ? label : null, "keyboard binding capture");
            var size = WaitFor(() => FindById(window, "QuickPreviewSize"), "preview size");
            var selection = (SelectionPattern)size.GetCurrentPattern(SelectionPattern.Pattern);
            Assert(selection.Current.GetSelection().SingleOrDefault()?.Current.Name == "384 × 384",
                "Saved preview size was not loaded.");
            Invoke(WaitFor(() => FindByName(window, "Свернуть"), "minimize button"));
            Assert(SpinWait.SpinUntil(() => IsIconic(handle), 500), "The custom minimize button did not minimize settings.");
            ShowWindow(handle, 9); // SW_RESTORE
            SetForegroundWindow(handle);
            var close = WaitFor(() => FindByName(window, "Закрыть"), "close button");
            var requestClose = Task.Run(() => Invoke(close));
            var owner = WaitFor(() =>
            {
                nint popup = GetLastActivePopup(handle);
                return popup != 0 && popup != handle && IsWindowVisible(popup) ? popup : (nint?)null;
            }, "unsaved settings confirmation");
            var confirmation = AutomationElement.FromHandle(owner);
            Invoke(WaitFor(() => FindByName(confirmation, "Закрыть без сохранения"), "discard settings button"));
            requestClose.GetAwaiter().GetResult();
            Assert(SpinWait.SpinUntil(() => !IsWindowVisible(handle), 500), "The custom close button did not close settings.");
            Console.WriteLine("Borderless settings, window move/minimize/close, saved size, Escape, mouse and keyboard capture passed.");
        }
        finally { StopApp(app); }
    }

    private static int CheckLaunch(string older, string newer)
    {
        older = Path.GetFullPath(older);
        newer = Path.GetFullPath(newer);
        if (!File.Exists(older) || !File.Exists(newer) || older.Equals(newer, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide two separate versioned PixelThumbs packages.");

        string profile = Directory.CreateTempSubdirectory("PixelThumbsLaunch-").FullName;
        string configuration = Path.Combine(profile, "settings.json");
        string latest = Path.Combine(profile, "latest-launch.json");
        string draft = Path.Combine(profile, "upgrade-draft.json");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new AppSettings { AutoStart = true, QuickPreview = false }));
        var launches = new List<Process>();
        Process Launch(string path, bool startup = false)
        {
            var process = StartApp(path, profile, startup);
            launches.Add(process);
            return process;
        }

        static nint SettingsWindow(Process process) => WaitFor(() =>
        {
            nint window = VisibleWindow(process.Id, overlay: false);
            return window == 0 ? (nint?)null : window;
        }, "settings window");

        try
        {
            var original = Launch(older);
            var originalWindow = AutomationElement.FromHandle(SettingsWindow(original));
            long originalRevision = ReadLaunchRevision(latest);
            var yaw = WaitFor(() => FindById(originalWindow, "Yaw"), "model rotation slider");
            ((RangeValuePattern)yaw.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(42);
            WaitFor(() => FindById(originalWindow, "YawValue") is { } value && value.Current.Name == "42°" ? value : null,
                "old build draft");

            var updated = Launch(newer);
            Assert(original.WaitForExit(10_000), "A running older build yielded to the newer package.");
            var upgradedWindow = AutomationElement.FromHandle(SettingsWindow(updated));
            WaitFor(() => FindById(upgradedWindow, "YawValue") is { } value && value.Current.Name == "42°" ? value : null,
                "transferred draft in the newer build");
            Assert(File.Exists(draft) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(configuration))?.DefaultStyle.Yaw == 35,
                "The draft was preserved without writing live settings.");
            long updatedRevision = ReadLaunchRevision(latest);
            Assert(updatedRevision > originalRevision, $"Expected a newer recorded revision than {originalRevision}, got {updatedRevision}.");

            var oldShortcut = Launch(older);
            Assert(oldShortcut.WaitForExit(8_000) && oldShortcut.ExitCode == 0 && !updated.HasExited,
                "An old launch leaves only the newer resident active.");
            Assert(SpinWait.SpinUntil(() =>
            {
                using var child = FindLaunchProcess(newer, excluding: updated.Id);
                return child is null;
            }, 5_000), "The forwarded activation finished.");

            StopApp(updated);
            var staleOnColdStart = Launch(older, startup: true);
            Assert(staleOnColdStart.WaitForExit(8_000) && staleOnColdStart.ExitCode == 0,
                "Cold launch through the old path redirects successfully.");
            var redirected = WaitFor(() => FindLaunchProcess(newer), "redirected newer process");
            launches.Add(redirected);
            Assert(VisibleWindow(redirected.Id, overlay: false) == 0, "Autostart redirect stays in the tray.");
            StopApp(redirected);

            File.WriteAllText(latest, JsonSerializer.Serialize(new { Revision = updatedRevision, Directory = Path.Combine(profile, "missing") }));
            var unavailable = Launch(older, startup: true);
            Assert(unavailable.WaitForExit(8_000), "A blocked downgrade exits without a window.");
            Assert(unavailable.ExitCode != 0,
                $"An unavailable newer package never falls back to the older UI ({originalRevision} < {updatedRevision}).");
            Console.WriteLine("PASS single resident, draft transfer, older launch redirect and downgrade protection.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (var process in launches)
            {
                using (process)
                    StopApp(process);
            }
            using var child = FindLaunchProcess(newer);
            if (child is not null)
                StopApp(child);
            File.Delete(configuration);
            File.Delete(latest);
            File.Delete(draft);
            Directory.Delete(profile);
        }
    }

    private static int CheckRestart(string installed, string update)
    {
        installed = Path.GetFullPath(installed);
        update = Path.GetFullPath(update);
        string updateDirectory = Path.GetDirectoryName(update)!;
        string? version = FileVersionInfo.GetVersionInfo(Path.Combine(updateDirectory, "PixelThumbs.dll")).ProductVersion;
        var revision = System.Text.RegularExpressions.Regex.Match(version ?? "", @"\+pt\.(\d+)");
        Assert(revision.Success, "Use a versioned Wumo update package.");
        var temporary = Directory.CreateTempSubdirectory("WumoRestart-");
        string profile = temporary.FullName;
        string latest = Path.Combine(profile, "latest-launch.json");
        string draft = Path.Combine(profile, "upgrade-draft.json");
        string configuration = Path.Combine(profile, "settings.json");
        string stagedDirectory = Path.Combine(profile, "updates", revision.Groups[1].Value);
        string staged = Path.Combine(stagedDirectory, "PixelThumbs.exe");
        Process? original = null;
        Process? restarted = null;
        Process? updated = null;

        static AutomationElement SettingsWindow(Process process) => AutomationElement.FromHandle(WaitFor(() =>
        {
            nint window = VisibleWindow(process.Id, overlay: false);
            return window == 0 ? (nint?)null : window;
        }, "restart settings window"));
        static AutomationElement RestartButton(AutomationElement window) =>
            WaitFor(() => FindById(window, "RestartWumo"), "restart button");
        static void ClickRestart(AutomationElement button) =>
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        static void ExpectDraft(AutomationElement window) =>
            WaitFor(() => FindById(window, "YawValue") is { } value && value.Current.Name == "42°" ? value : null,
                "draft after restart");

        try
        {
            File.WriteAllText(configuration, JsonSerializer.Serialize(new AppSettings { QuickPreview = false }));
            Directory.CreateDirectory(stagedDirectory);
            File.Copy(update, staged);
            File.Copy(Path.Combine(updateDirectory, "PixelThumbs.dll"), Path.Combine(stagedDirectory, "PixelThumbs.dll"));

            original = StartApp(installed, profile, startup: false);
            var window = SettingsWindow(original);
            long originalRevision = ReadLaunchRevision(latest);
            var yaw = WaitFor(() => FindById(window, "Yaw"), "model rotation slider");
            ((RangeValuePattern)yaw.GetCurrentPattern(RangeValuePattern.Pattern)).SetValue(42);
            ExpectDraft(window);

            ClickRestart(RestartButton(window));
            Assert(original.WaitForExit(9000) && original.ExitCode == 0, "The restart releases the old resident.");
            restarted = WaitFor(() => FindLaunchProcess(installed), "same-build restart");
            window = SettingsWindow(restarted);
            ExpectDraft(window);
            Assert(ReadLaunchRevision(latest) == originalRevision, "An incomplete update leaves the current build in use.");

            foreach (string file in Directory.EnumerateFiles(updateDirectory, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(stagedDirectory, Path.GetRelativePath(updateDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            File.WriteAllText(staged, "invalid apphost");
            var button = RestartButton(window);
            ClickRestart(button);
            WaitFor(() => FindById(window, "StatusLine") is { } status &&
                status.Current.Name.Contains("повреждён", StringComparison.Ordinal) && button.Current.IsEnabled ? status : null,
                "error from an invalid apphost");
            Assert(!restarted.HasExited && ReadLaunchRevision(latest) == originalRevision,
                "The active build stays open when a staged executable cannot start.");
            File.Copy(update, staged, overwrite: true);

            ClickRestart(button);
            Assert(restarted.WaitForExit(9000), "The staged update replaces the old resident.");
            updated = WaitFor(() => FindLaunchProcess(staged), "staged update");
            ExpectDraft(SettingsWindow(updated));
            Assert(ReadLaunchRevision(latest) > originalRevision && File.Exists(draft) &&
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(configuration))?.DefaultStyle.Yaw == 35,
                "The staged revision is remembered and the draft remains separate from applied settings.");
            Console.WriteLine("PASS restart button, incomplete package fallback, failed-launch recovery, local update and draft handoff.");

            string nonce = Guid.NewGuid().ToString("N");
            var command = new ProcessStartInfo(staged) { WorkingDirectory = stagedDirectory, UseShellExecute = false };
            command.ArgumentList.Add("--restart-after");
            command.ArgumentList.Add(updated.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.ArgumentList.Add(nonce);
            command.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(profile)));
            using var successor = Process.Start(command)!;
            try
            {
                Assert(SpinWait.SpinUntil(() =>
                {
                    if (!EventWaitHandle.TryOpenExisting(@"Local\PixelThumbs.Restart." + nonce + ".Ready", out var ready))
                        return false;
                    using (ready) return ready.WaitOne(0);
                }, 5000), "The standard-user restart did not signal readiness.");
                Assert(!successor.HasExited && VisibleWindow(successor.Id, overlay: false) == 0,
                    "The standard-user restart started a second tray before the predecessor left.");
                using var proceed = EventWaitHandle.OpenExisting(@"Local\PixelThumbs.Restart." + nonce + ".Proceed");
                proceed.Set();
                StopApp(updated);
                ExpectDraft(SettingsWindow(successor));
                Assert(ReadLaunchRevision(latest) > originalRevision, "The successor did not use the same profile.");
                Console.WriteLine("PASS restart successor waits for the predecessor and opens with its profile and draft.");
            }
            finally { StopApp(successor); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (var process in new[] { original, restarted, updated, FindLaunchProcess(installed), FindLaunchProcess(staged) })
                if (process is not null)
                    using (process) StopApp(process);
            temporary.Delete(recursive: true);
        }
    }

    private static long ReadLaunchRevision(string path)
    {
        using var record = JsonDocument.Parse(File.ReadAllText(path));
        return record.RootElement.GetProperty("Revision").GetInt64();
    }

    private static Process? FindLaunchProcess(string executable, int excluding = 0)
    {
        // CheckLaunch uses dedicated package folders which are never shared with the real tray app.
        Process? match = null;
        foreach (var process in Process.GetProcessesByName("PixelThumbs"))
        {
            try
            {
                if (match is null && process.Id != excluding && process.MainModule?.FileName.Equals(executable, StringComparison.OrdinalIgnoreCase) == true)
                {
                    match = process;
                    continue;
                }
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return match;
    }

    private static void CheckMove(nint handle, AutomationElement window)
    {
        var caption = WaitFor(() => FindByName(window, "Полоса заголовка Wumo"), "window caption");
        var origin = HitPoint(caption);
        GetWindowRect(handle, out var before);
        Cursor.Position = origin;
        MouseEvent(2, 0, 0, 0, 0); // left button down
        try
        {
            Thread.Sleep(120);
            Cursor.Position = new Point(origin.X + 42, origin.Y + 34);
            Thread.Sleep(120);
        }
        finally { MouseEvent(4, 0, 0, 0, 0); }
        GetWindowRect(handle, out var after);
        Assert(after.Left != before.Left || after.Top != before.Top, "Dragging the custom caption did not move settings.");
    }

    private static void CheckMonitors(nint handle, AutomationElement window)
    {
        GetWindowRect(handle, out var original);
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle area = screen.WorkingArea;
            Assert(SetWindowPos(handle, 0, area.Left + 32, area.Top + 32, 0, 0, 0x15), "Could not move the settings window to a connected display.");
            SendMessage(handle, 0x232, 0, 0); // WM_EXITSIZEMOVE
            Thread.Sleep(150);
            GetWindowRect(handle, out var bounds);
            Assert(area.Contains(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)),
                "Settings does not fit entirely within the monitor work area.");
            var close = WaitFor(() => FindByName(window, "Закрыть"), "close button on the current display");
            Assert(!close.Current.IsOffscreen, "The close button is clipped on one of the monitors.");
            Console.WriteLine($"Settings fits {screen.DeviceName} ({area.Width}×{area.Height}px work area, {GetDpiForWindow(handle)} DPI).");
        }
        SetWindowPos(handle, 0, original.Left, original.Top, original.Right - original.Left, original.Bottom - original.Top, 0x14);
        SendMessage(handle, 0x232, 0, 0);
    }

    private static string? Resolve(Point pointer) => QuickPreviewTarget.GetView(pointer) is { } view
        ? QuickPreviewTarget.Resolve(view, pointer)?.Path : null;

    private static Point HitPoint(AutomationElement item)
    {
        var bounds = item.Current.BoundingRectangle;
        return new Point((int)bounds.Left + Math.Min(20, (int)bounds.Width / 2), (int)bounds.Top + (int)bounds.Height / 2);
    }

    private static AutomationElement FindFile(AutomationElement window, string directory, string file, bool allowUnresolved = false) =>
        TryFindFile(window, directory, file, allowUnresolved) ?? throw new InvalidOperationException($"Fixture {file} is not visible.");

    private static AutomationElement? TryFindFile(AutomationElement window, string directory, string file, bool allowUnresolved = false)
    {
        var items = window.FindAll(TreeScope.Descendants, FileItems);
        string fullPath = Path.Combine(directory, file);
        foreach (AutomationElement item in items)
        {
            if (item.Current.Name == file)
                return item;
            // Explorer may hide known extensions, including those on otherwise identical labels.
            if (item.Current.Name == Path.GetFileNameWithoutExtension(file) && (allowUnresolved || Resolve(HitPoint(item)) == fullPath))
                return item;
        }
        return null;
    }

    private static void WriteProfile(string directory, int key, int size) =>
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            JsonSerializer.Serialize(new AppSettings { AutoStart = true, QuickPreviewKey = key, QuickPreviewSize = size }));

    private static Process StartApp(string executable, string profile, bool startup)
    {
        var start = new ProcessStartInfo(isolatedPreview ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!,
            "PixelThumbs.PreviewChecks.exe") : Path.GetFullPath(executable)) { CreateNoWindow = isolatedPreview };
        start.Environment["PIXELTHUMBS_DATA_DIR"] = profile;
        if (isolatedPreview)
            start.ArgumentList.Add("--host-preview");
        else if (startup)
            start.ArgumentList.Add("--startup");
        string ready = Path.Combine(profile, "hook-ready");
        if (isolatedPreview)
            File.Delete(ready);
        var process = Process.Start(start) ?? throw new InvalidOperationException("PixelThumbs did not start.");
        if (!isolatedPreview)
            return process;
        try
        {
            WaitFor(() => File.Exists(ready) ? process : null, "isolated keyboard hook");
            return process;
        }
        catch
        {
            StopApp(process);
            process.Dispose();
            throw;
        }
    }

    private static void StopApp(Process app)
    {
        if (app.HasExited)
            return;
        app.Kill(entireProcessTree: true);
        app.WaitForExit();
    }

    private static void Tap(int binding)
    {
        Thread.Sleep(120);
        Input(binding, down: true);
        Input(binding, down: false);
    }

    private static void Shortcut(int key, params int[] modifiers)
    {
        foreach (int modifier in modifiers)
            Input(modifier, down: true);
        try { Tap(key); }
        finally
        {
            for (int i = modifiers.Length - 1; i >= 0; i--)
                Input(modifiers[i], down: false);
        }
        Thread.Sleep(250);
    }

    private static void Input(int binding, bool down)
    {
        if (QuickPreviewKeys.IsMouse(binding))
            MouseEvent(down ? 0x80u : 0x100u, 0, 0, 1, 0); // XBUTTON1
        else
            KeyboardEvent((byte)binding, 0, down ? 0u : 2u, 0);
    }

    private static void Invoke(AutomationElement item) => ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static AutomationElement? FindById(AutomationElement parent, string id) =>
        parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static AutomationElement? FindByName(AutomationElement parent, string name, TreeScope scope = TreeScope.Descendants) =>
        parent.FindAll(scope, Condition.TrueCondition).Cast<AutomationElement>()
            .FirstOrDefault(item => item.Current.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    private static T WaitFor<T>(Func<T?> action, string description, int milliseconds = 5000) where T : class
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            try
            {
                if (action() is { } result)
                    return result;
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(12);
        } while (timeout.ElapsedMilliseconds < milliseconds);
        throw new TimeoutException($"Timed out finding {description}.");
    }

    private static nint WaitFor(Func<nint?> action, string description, int milliseconds = 5000) =>
        WaitFor(() => action() is { } handle ? new Window(handle) : null, description, milliseconds).Handle;

    private static nint VisibleWindow(int id, bool overlay)
    {
        nint result = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out int process);
            const int noActivate = 0x08000000;
            if (process != id || !IsWindowVisible(window) || IsIconic(window) ||
                ((GetWindowLong(window, -20) & noActivate) != 0) != overlay)
                return true;
            result = window;
            return false;
        }, 0);
        return result;
    }

    private static void Assert(bool condition, string error)
    {
        if (!condition)
            throw new InvalidOperationException(error);
    }

    private static bool FocusExplorer(nint window) => GetForegroundWindow() == window || SetForegroundWindow(window);

    private sealed record Window(nint Handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindow(nint window, nint parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint value, nint argument);
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowRect rectangle);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nint value, nint argument);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeyboardEvent(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, nuint extra);
}
