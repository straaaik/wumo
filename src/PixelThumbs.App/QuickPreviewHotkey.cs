using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PixelThumbs;

internal sealed class QuickPreviewHotkey : IDisposable
{
    private const int ResolveInterval = 180;
    private static readonly int[] ModifierKeys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
    private readonly Control dispatcher;
    private readonly System.Windows.Forms.Timer pointerTimer = new() { Interval = 45 };
    private readonly QuickPreviewBitmapCache cache = new();
    private QuickPreviewInputHook? hook;
    private AppSettings? settings;
    private int key = QuickPreviewKeys.Space;
    private bool pressed;
    private bool consumed;
    private nint owner;
    private QuickPreviewForm? preview;
    private string? previewPath;
    private PreviewCancellation? rendering;
    private Lookup? lookup;
    private Result? latest;
    private QuickPreviewTarget.FocusedView? selectedView;
    private Task<string?>? selectionLookup;
    private int revision;
    private int holdVersion;
    private bool disposed;

    internal QuickPreviewHotkey(Control dispatcher)
    {
        this.dispatcher = dispatcher;
        pointerTimer.Tick += (_, _) =>
        {
            if (owner == 0 && selectedView is null)
                pointerTimer.Stop();
            UpdatePointer();
        };
    }

    internal void Configure(AppSettings next)
    {
        EndHold();
        InvalidateTarget();
        settings = next;
        int binding = next.QuickPreviewKey;
        if (key != binding)
        {
            key = binding;
            pressed = consumed = false;
            hook?.Dispose();
            hook = null;
        }

        if (!next.QuickPreview || !next.ThumbnailsWidgetEnabled)
        {
            pointerTimer.Stop();
            hook?.Dispose();
            hook = null;
            pressed = consumed = false;
            cache.Clear();
            return;
        }

        try
        {
            bool followsPointer = key != QuickPreviewKeys.Space;
            hook ??= new QuickPreviewInputHook(OnInput, followsPointer ? OnPointerChanged : null,
                trackMouse: followsPointer, pointerKey: followsPointer ? key : 0);
            if (followsPointer)
                pointerTimer.Start();
        }
        catch (Win32Exception error)
        {
            Trace.TraceWarning($"PixelThumbs quick preview input hook: {error.Message}");
        }
    }

    private bool OnInput(int binding, bool down, Point pointer)
    {
        if (binding != key)
        {
            // Navigation and rename can change the item or enable an editor without moving the pointer.
            if (down)
            {
                InvalidateTarget();
                if (selectedView is not null)
                {
                    selectedView = null;
                    ++holdVersion;
                    pointerTimer.Stop();
                    Post(ClearPreview);
                }
                else if (key != QuickPreviewKeys.Space)
                    pointerTimer.Start();
            }
            return false;
        }
        if (pressed == down)
            return consumed || (down && owner != 0);
        pressed = down;

        if (!down)
        {
            bool handled = consumed;
            bool hadPreview = owner != 0 || selectedView is not null;
            consumed = false;
            owner = 0;
            selectedView = null;
            pointerTimer.Stop();
            ++holdVersion;
            if (hadPreview)
                Post(ClearPreview);
            return handled;
        }

        if (HasModifier())
            return false;
        if (binding == QuickPreviewKeys.Space)
        {
            if (QuickPreviewTarget.GetFocusedView() is not { } focused)
                return false;
            // Preserve the selection as it was when Space was pressed. Explorer's default
            // Space action in the file pane can otherwise select the focused item during lookup.
            selectedView = focused;
            consumed = true;
            pointerTimer.Start();
            int version = holdVersion;
            Post(() => _ = OpenSelectedAsync(focused, version));
            return true;
        }
        if (QuickPreviewTarget.GetView(pointer) is not { } view)
            return false;
        var request = new Request(view, pointer);
        FinishLookup();
        if (!TryGetLatest(request, out var hit, allowStale: true))
        {
            // The global input hook never waits for Explorer or UI Automation.
            Post(UpdatePointer);
            return false;
        }
        if (hit is null || !IsFormatEnabled(hit.Value.Path))
            return false;

        consumed = true;
        owner = view.Window;
        pointerTimer.Start();
        Post(UpdatePointer);
        return true;
    }

    private void OnPointerChanged(bool scrolled)
    {
        if (scrolled)
            InvalidateTarget();
        pointerTimer.Start();
    }

    private void Post(Action action)
    {
        try { dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void UpdatePointer()
    {
        if (disposed || hook is null)
            return;
        if (key == QuickPreviewKeys.Space)
        {
            if (selectedView is { } selected && QuickPreviewTarget.GetFocusedView() != selected)
                EndHold();
            return;
        }
        Point pointer = Cursor.Position;
        if (owner != 0 && preview?.Bounds.Contains(pointer) == true)
            preview.PlaceOnMonitor(pointer);
        FinishLookup();
        if (QuickPreviewTarget.GetView(pointer) is not { } view || HasModifier())
        {
            latest = null;
            if (owner != 0 && QuickPreviewTarget.GetFocusedView()?.Window != owner)
                EndHold();
            else
                ClearPreview();
            return;
        }

        var request = new Request(view, pointer);
        bool resolved = TryGetLatest(request, out var hit, allowStale: owner == 0);
        if (!resolved || (owner != 0 &&
            latest is { } result && Environment.TickCount64 - result.Timestamp >= ResolveInterval))
            StartLookup(request);

        if (owner == 0)
            return;
        if (owner != view.Window || hit is null || !IsFormatEnabled(hit.Value.Path))
        {
            ClearPreview();
            return;
        }
        if (string.Equals(previewPath, hit.Value.Path, StringComparison.Ordinal))
            return;

        OpenPreview(hit.Value.Path, pointer);
    }

    private bool TryGetLatest(Request at, out QuickPreviewTarget.Hit? hit, bool allowStale)
    {
        hit = null;
        if (latest is not { } result || result.Request.View != at.View ||
            ((!allowStale || result.Hit is null) && Environment.TickCount64 - result.Timestamp > ResolveInterval * 2) ||
            (result.Request.Pointer != at.Pointer && result.Hit?.Bounds.Contains(at.Pointer) != true))
            return false;
        hit = result.Hit;
        return true;
    }

    private void StartLookup(Request request)
    {
        FinishLookup();
        if (lookup is not null)
            return;
        var completion = new TaskCompletionSource<QuickPreviewTarget.Hit?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(QuickPreviewTarget.Resolve(request.View, request.Pointer)); }
            catch (Exception error)
            {
                Trace.TraceWarning($"PixelThumbs quick preview hover: {error.Message}");
                completion.SetResult(null);
            }
            Post(LookupCompleted);
        })
        { IsBackground = true, Name = "PixelThumbs Explorer hit test" };
        thread.SetApartmentState(ApartmentState.STA);
        lookup = new Lookup(request, completion.Task, revision);
        thread.Start();
    }

    private void LookupCompleted()
    {
        FinishLookup();
        UpdatePointer();
    }

    private void FinishLookup()
    {
        if (lookup is { Work.IsCompleted: true } pending)
        {
            lookup = null;
            if (pending.Revision == revision)
                latest = new Result(pending.Request, pending.Work.Result, Environment.TickCount64);
        }
    }

    private void InvalidateTarget()
    {
        latest = null;
        ++revision;
    }

    private bool IsFormatEnabled(string path) => settings?.Formats.GetValueOrDefault(Path.GetExtension(path))?.Enabled is true;

    private async Task OpenSelectedAsync(QuickPreviewTarget.FocusedView view, int version)
    {
        // Bound the number of COM lookups even if Explorer is busy and Space is pressed repeatedly.
        while (selectionLookup is { IsCompleted: false } busy)
            await busy;
        if (selectedView != view || version != holdVersion || QuickPreviewTarget.GetFocusedView() != view)
            return;
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        selectionLookup = completion.Task;
        var thread = new Thread(() =>
        {
            try { completion.SetResult(QuickPreviewTarget.ResolveSelection(view)); }
            catch (Exception error)
            {
                Trace.TraceWarning($"PixelThumbs Explorer selection: {error.Message}");
                completion.SetResult(null);
            }
        }) { IsBackground = true, Name = "PixelThumbs Explorer selection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        string? path = await completion.Task;
        if (selectedView != view || version != holdVersion)
            return;
        if (path is null || QuickPreviewTarget.GetFocusedView() != view)
            EndHold();
        else
            OpenPreview(path, Screen.FromHandle(view.Window).WorkingArea.Location);
    }

    private void OpenPreview(string path, Point anchor)
    {
        ClearPreview();
        previewPath = path;
        var snapshot = settings!;
        var window = new QuickPreviewForm(snapshot.QuickPreviewSize, anchor, Path.GetFileName(path));
        preview = window;
        window.Show();
        var cancellation = new PreviewCancellation();
        rendering = cancellation;
        _ = RenderAsync(window, path, snapshot, cancellation, holdVersion);
    }

    private async Task RenderAsync(QuickPreviewForm window, string path, AppSettings snapshot,
        PreviewCancellation cancellation, int version)
    {
        Bitmap? image = null;
        try
        {
            var result = await Task.Run(() => RenderFile(path, snapshot, cancellation.Token));
            image = result.Image;
            if (version != holdVersion ||
                !ReferenceEquals(rendering, cancellation) || cancellation.IsCancellationRequested)
                return;
            if (image is null)
            {
                window.ShowDetails(result.Properties, result.Reason);
                return;
            }
            window.ShowImage(image);
            image = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Trace.TraceWarning($"PixelThumbs quick preview render: {error.Message}");
            if (ReferenceEquals(preview, window) && version == holdVersion && !cancellation.IsCancellationRequested)
                window.ShowDetails("", PreviewService.FailureMessage(RenderFailure.WorkerError));
        }
        finally
        {
            image?.Dispose();
            if (ReferenceEquals(rendering, cancellation))
                rendering = null;
            await cancellation.DisposeAsync();
        }
    }

    private (Bitmap? Image, string Properties, string Reason) RenderFile(string path, AppSettings snapshot,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path);
        string properties = $"Тип файла  {extension}";
        var file = new FileInfo(path);
        long length;
        FileAttributes attributes;
        try
        {
            length = file.Length;
            attributes = file.Attributes;
            properties += $"\r\nРазмер  {length:N0} байт\r\nИзменён  {file.LastWriteTime:g}";
        }
        catch (FileNotFoundException) { return (null, properties, "Файл больше недоступен."); }
        catch (UnauthorizedAccessException) { return (null, properties, "Нет доступа к файлу."); }
        catch (IOException) { return (null, properties, "Не удалось прочитать свойства файла."); }

        var format = snapshot.Formats[extension];
        if (!format.Enabled)
            return (null, properties, "Предпросмотр этого формата отключён в настройках.");
        const FileAttributes recallOnDataAccess = (FileAttributes)0x400000;
        if ((attributes & (FileAttributes.Offline | recallOnDataAccess)) != 0)
            return (null, properties, "Файл недоступен локально. Загрузите его для просмотра.");
        if (length == 0)
            return (null, properties, "Файл пуст.");
        var failure = RenderFailure.None;
        var image = cache.Render(file, snapshot.QuickPreviewSize, format.Style ?? snapshot.DefaultStyle,
            cancellation, report => failure = report.Failure);
        return (image, properties, image is null ? PreviewService.FailureMessage(failure) : "");
    }

    private void ClearPreview()
    {
        var pending = rendering;
        rendering = null;
        pending?.Cancel();
        preview?.Dispose();
        preview = null;
        previewPath = null;
    }

    private void EndHold()
    {
        owner = 0;
        selectedView = null;
        pointerTimer.Stop();
        ++holdVersion;
        ClearPreview();
    }

    private bool HasModifier() => ModifierKeys.Any(modifier => modifier != key && GetAsyncKeyState(modifier) < 0);

    public void Dispose()
    {
        disposed = true;
        EndHold();
        pointerTimer.Dispose();
        hook?.Dispose();
        cache.Dispose();
    }

    private readonly record struct Request(QuickPreviewTarget.ViewContext View, Point Pointer);
    private sealed record Lookup(Request Request, Task<QuickPreviewTarget.Hit?> Work, int Revision);
    private sealed record Result(Request Request, QuickPreviewTarget.Hit? Hit, long Timestamp);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
