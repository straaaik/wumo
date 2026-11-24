using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Microsoft.Win32;
using static PixelThumbs.ClipboardHistory.ClipboardNative;

namespace PixelThumbs.ClipboardHistory;

internal sealed class HistoryWidget : IDisposable
{
    private readonly Control dispatcher;
    private readonly HistoryStore history = new();
    private readonly HistoryJournal journal = new(Path.Combine(SettingsStore.DataDirectory, "clipboard-history.dat"));
    private readonly HistoryJournal favoritesJournal = new(Path.Combine(SettingsStore.DataDirectory, "clipboard-favorites.dat"));
    private readonly DispatcherTimer expiration = new();
    private readonly DispatcherTimer deferredSave = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private ClipboardShelf? shelf;
    private ClipboardHistoryOptions options = new();
    private Listener? listener;
    private CaptureWorker? worker;
    private ClipboardTransfer? published;
    private HistoryEntry? current;
    private uint currentSequence;
    private nint ownClipboard;
    private Pending? pending;
    private bool capturing;
    private bool dragging;
    private bool checkingPaths;
    private bool importing;
    private bool disposed;
    private bool waitingToSave;
    private bool journalReady;
    private bool journalDirty;
    private bool favoritesLoading;
    private bool favoritesReady;
    private Task journalQueue = Task.CompletedTask;
    private Task<bool>? favoritesSave;
    private int journalEpoch;
    private int generation;
    private int restoreVersion;

    internal event Action? TrayChanged;
    internal bool Active => listener is not null;

    internal void PreviewShelf() => shelf?.Preview();

    internal HistoryWidget(Control dispatcher)
    {
        this.dispatcher = dispatcher;
        expiration.Tick += (_, _) => { PruneExpired(); };
        deferredSave.Tick += SaveAfterJournal;
    }

    internal void Configure(ClipboardHistoryOptions next)
    {
        bool persisted = Active && options.KeepBetweenSessions;
        bool persistenceDisabled = options.Enabled && options.KeepBetweenSessions &&
            !(next.Enabled && next.KeepBetweenSessions);
        options = next;
        if (!options.Enabled)
        {
            if (listener is not null)
            {
                listener.Dispose();
                listener = null;
                CloseShelf();
                worker?.Dispose();
                worker = null;
                // OLE owns the published transfer until the Windows clipboard changes.
                published = null;
                current = null;
                ClearHistory();
            }
        }
        else
        {
            if (listener is null)
            {
                Application.OleRequired();
                try { listener = new Listener(dispatcher.Handle, ClipboardChanged); }
                catch (Win32Exception error) { Trace.TraceWarning($"Clipboard listener: {error.Message}"); }
            }
            if (Active)
            {
                EnsureShelf();
                if (!favoritesReady && !favoritesLoading)
                    LoadFavorites();
            }
        }
        shelf?.Configure(options);
        bool persist = Active && options.KeepBetweenSessions;
        if (persist != persisted || persistenceDisabled)
        {
            journalEpoch++;
            deferredSave.Stop();
            journalReady = journalDirty = false;
            if (persist)
                LoadJournal();
            else
                _ = RunJournal(journal.Delete);
        }
        PruneExpired();
        TrayChanged?.Invoke();
    }

    private void EnsureShelf()
    {
        if (shelf is not null)
            return;
        shelf = new ClipboardShelf();
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(shelf);
        shelf.Restore += RestoreToClipboard;
        shelf.Drag += BeginDrag;
        shelf.Remove += RemoveEntry;
        shelf.RemoveFolder += RemoveFolder;
        shelf.TogglePin += TogglePin;
        shelf.Rename += RenameEntry;
        shelf.RenameFolder += RenameFolder;
        shelf.Group += GroupEntries;
        shelf.ClearClipboard += ClearSystemClipboard;
        shelf.Clear += ClearSelection;
        shelf.Imported += ImportDropped;
        UpdateShelf();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
    }

    private void CloseShelf()
    {
        if (shelf is not { } panel)
            return;
        shelf = null;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        panel.Close();
    }

    private void AdvanceGeneration()
    {
        generation++;
        pending = null;
    }

    private void ClipboardChanged()
    {
        if (!Active)
            return;
        uint sequence = GetClipboardSequenceNumber();
        if (ownClipboard != 0 && GetClipboardOwner() == ownClipboard)
        {
            currentSequence = sequence;
            return;
        }
        if (sequence == currentSequence && pending is null)
            return;
        ownClipboard = 0;
        published = null;
        current = null;
        pending = new(sequence, DateTime.UtcNow);
        StartCapture();
    }

    private void StartCapture()
    {
        if (capturing || pending is not { } request || !Active)
            return;
        pending = null;
        if (options.Types == 0)
            return;
        capturing = true;
        worker ??= new CaptureWorker();
        int revision = generation;
        ClipboardKind kinds = options.Types;
        worker.Enqueue(() =>
        {
            (uint Sequence, ClipboardItem? Item) result = default;
            try
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    result = ClipboardCapture.Read(request.Sequence, kinds);
                    if (result.Sequence != 0)
                        break;
                    Thread.Sleep(8 + 4 * attempt);
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                Trace.TraceWarning($"Clipboard capture: {error.Message}");
            }
            Post(() => FinishCapture(request, revision, result));
        });
    }

    private void FinishCapture(Pending request, int revision, (uint Sequence, ClipboardItem? Item) result)
    {
        capturing = false;
        if (!Active || disposed)
            return;
        if (revision == generation && result.Item is { } item && (item.Kind & options.Types) != 0)
        {
            var entry = history.Add(item, request.Time);
            bool retained = history.Contains(entry);
            if (pending?.Sequence == result.Sequence)
                pending = null; // Rendering a delayed format can generate another notification.
            if (GetClipboardSequenceNumber() == result.Sequence && ownClipboard == 0)
            {
                current = retained ? entry : null;
                currentSequence = result.Sequence;
            }
            HistoryChanged();
            if (retained)
                shelf?.NotifyCopy();
        }
        if (pending is null && revision == generation && result.Sequence != 0 &&
            result.Item is null && result.Sequence != request.Sequence && ownClipboard == 0)
            pending = new(result.Sequence, DateTime.UtcNow);
        StartCapture();
    }

    internal void PopulateTray(ToolStripItemCollection items)
    {
        items.Add(new ToolStripMenuItem("Открыть историю", null, (_, _) => shelf?.Open()));
        items.Add(new ToolStripMenuItem("Очистить историю", null, (_, _) => ClearHistory()) { Enabled = history.Entries.Count > 0 });
    }

    private async void ImportDropped(ClipboardDrop input)
    {
        if (importing)
        {
            shelf?.SetStatus("Предыдущая запись ещё добавляется");
            return;
        }
        int version = journalEpoch;
        importing = true;
        try
        {
            var item = await Task.Run(() => ClipboardCapture.FromDrop(input));
            if (!Active || disposed || version != journalEpoch)
                return;
            if (item is null || (item.Kind & options.Types) == 0)
                shelf?.SetStatus("Эти данные не удалось добавить");
            else
            {
                history.Add(item, DateTime.UtcNow);
                HistoryChanged();
                shelf?.SetStatus("Добавлено в историю");
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or
            NotSupportedException or COMException or InvalidDataException)
        {
            if (!disposed)
                shelf?.SetStatus("Не удалось добавить перетащенные данные");
        }
        finally { importing = false; }
    }

    private void HistoryChanged()
    {
        if (current is { } entry && !history.Contains(entry))
            current = null;
        UpdateShelf();
        ScheduleExpiration();
        if (!Active || !options.KeepBetweenSessions)
            return;
        journalDirty = true;
        if (!journalReady)
            return;
        deferredSave.Stop();
        deferredSave.Start();
    }

    private void RemoveEntry(HistoryEntry entry)
    {
        if (history.Remove(entry))
            HistoryChanged();
        else if (CanEditFavorites() && history.RemoveSaved(entry))
            FavoritesChanged();
    }

    private void RemoveFolder(Guid id)
    {
        if (CanEditFavorites() && history.RemoveFolder(id))
            FavoritesChanged();
    }

    private void TogglePin(HistoryEntry entry)
    {
        if (!CanEditFavorites() || !history.TogglePin(entry, DateTime.UtcNow, out var replacement))
            return;
        if (ReferenceEquals(current, entry))
            current = replacement;
        FavoritesChanged();
    }

    private HistoryEntry? RenameEntry(HistoryEntry entry, string name)
    {
        var replacement = entry with { Name = name };
        if (!CanEditFavorites() || !history.Rename(entry, replacement))
            return null;
        if (ReferenceEquals(current, entry))
            current = replacement;
        FavoritesChanged();
        return replacement;
    }

    private bool RenameFolder(Guid id, string name)
    {
        if (!CanEditFavorites() || !history.RenameFolder(id, name))
            return false;
        FavoritesChanged();
        return true;
    }

    private bool GroupEntries(HistoryEntry source, HistoryEntry? target, Guid? folder)
    {
        if (!CanEditFavorites())
            return false;
        bool changed = folder is { } id ? history.AddToFolder(source, id) : target is not null && history.Group(source, target);
        if (changed)
        {
            FavoritesChanged();
            shelf?.SetStatus(folder is null ? "Папка создана" : "Добавлено в папку");
        }
        return changed;
    }

    private bool CanEditFavorites()
    {
        if (favoritesReady)
            return true;
        shelf?.SetStatus(favoritesLoading ? "Избранное ещё загружается" : "Не удалось открыть избранное");
        return false;
    }

    private void FavoritesChanged()
    {
        SaveFavorites();
        HistoryChanged();
    }

    private void SaveFavorites()
    {
        var snapshot = history.SnapshotFavorites();
        favoritesSave = RunJournal(() => favoritesJournal.SaveFavorites(snapshot), "Не удалось сохранить избранное");
    }

    private void UpdateShelf() => shelf?.SetEntries(history.Entries, history.Favorites, history.Folders);

    private async void LoadFavorites()
    {
        favoritesLoading = true;
        HistoryFavorites? loaded = null;
        await RunJournal(() => loaded = favoritesJournal.LoadFavorites(), "Не удалось открыть избранное");
        favoritesLoading = false;
        if (disposed || loaded is null)
            return;
        favoritesReady = true;
        if (history.LoadFavorites(loaded))
        {
            if (current is { } entry)
                current = loaded.Entries.Concat(loaded.Folders.SelectMany(folder => folder.Entries))
                    .FirstOrDefault(saved => saved.Item.Kind == entry.Item.Kind && saved.Item.Key == entry.Item.Key) ?? entry;
            HistoryChanged();
        }
        else
            UpdateShelf();
    }

    private void ClearHistory()
    {
        AdvanceGeneration();
        journalEpoch++;
        if (Active && options.KeepBetweenSessions && !journalReady)
            journalReady = true;
        history.Clear();
        HistoryChanged();
    }

    private void ClearSelection(HistoryEntry[]? selected)
    {
        if (selected is null)
        {
            ClearHistory();
            return;
        }
        foreach (var entry in selected)
            history.Remove(entry);
        HistoryChanged();
    }

    private void PruneExpired()
    {
        if (Active && options.ExpireHours > 0)
        {
            var cutoff = DateTime.UtcNow.AddHours(-options.ExpireHours);
            var expired = history.Entries.Where(entry => entry.Time <= cutoff).ToArray();
            if (expired.Length > 0)
            {
                ClearSelection(expired);
                return;
            }
        }
        ScheduleExpiration();
    }

    private void ScheduleExpiration()
    {
        expiration.Stop();
        if (!Active || options.ExpireHours == 0 || history.Entries.Count == 0)
            return;
        var due = history.Entries.Min(entry => entry.Time).AddHours(options.ExpireHours) - DateTime.UtcNow;
        expiration.Interval = TimeSpan.FromMilliseconds(Math.Clamp(due.TotalMilliseconds, 1, int.MaxValue));
        expiration.Start();
    }

    private async void LoadJournal()
    {
        int version = journalEpoch;
        HistoryEntry[]? loaded = null;
        await RunJournal(() =>
        {
            try { loaded = journal.Load(); }
            catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
            {
                // New captures can replace a truncated journal at the next successful save.
                loaded = [];
                throw;
            }
        });
        if (disposed || version != journalEpoch || loaded is null)
            return;
        journalReady = true;
        bool changed = journalDirty;
        if (loaded.Length > 0)
        {
            var existing = history.Entries.Select(entry => (entry.Item.Kind, entry.Item.Key)).ToHashSet();
            foreach (var entry in loaded.Where(entry => !existing.Contains((entry.Item.Kind, entry.Item.Key))).OrderBy(entry => entry.Time))
                history.AddOlder(entry);
        }
        PruneExpired();
        UpdateShelf();
        if (changed)
            HistoryChanged();
    }

    private async void SaveAfterJournal(object? sender, EventArgs args)
    {
        deferredSave.Stop();
        if (waitingToSave)
            return;
        waitingToSave = true;
        try { await journalQueue; }
        finally { waitingToSave = false; }
        if (!disposed && !deferredSave.IsEnabled)
            SaveJournal();
    }

    private void SaveJournal()
    {
        if (!journalReady || !journalDirty || !options.KeepBetweenSessions ||
            favoritesSave is { IsCompletedSuccessfully: true, Result: false })
            return;
        journalDirty = false;
        var snapshot = history.Entries.ToArray();
        var archive = favoritesSave;
        _ = RunJournal(() =>
        {
            // Preserve the previous history snapshot if moving an entry to Favorites could not be saved.
            if (archive?.GetAwaiter().GetResult() != false)
                journal.Save(snapshot);
        });
    }

    private Task<bool> RunJournal(Action operation, string failureMessage = "Не удалось прочитать или сохранить историю")
    {
        var queued = journalQueue.ContinueWith(_ =>
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
                InvalidDataException or ArgumentException or NotSupportedException)
            {
                Trace.TraceWarning($"Clipboard history: {error.Message}");
                Post(() => { if (!disposed) shelf?.SetStatus(failureMessage); });
                return false;
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        journalQueue = queued;
        return queued;
    }

    private async void RestoreToClipboard(HistoryEntry entry, ClipboardShelf panel)
    {
        int revision = ++restoreVersion;
        try
        {
            if (!await Available(entry, panel))
                return;
            var transfer = new ClipboardTransfer(entry.Item, dragging: false);
            for (int attempt = 0; attempt < 4 && revision == restoreVersion && CanUse(entry); attempt++)
            {
                if (ClipboardTransfer.Publish(dispatcher.Handle, entry.Item, transfer, out bool ole))
                {
                    // Clipboard notifications are dispatched after this handler returns.
                    current = entry;
                    currentSequence = GetClipboardSequenceNumber();
                    ownClipboard = GetClipboardOwner();
                    published = ole ? transfer : null;
                    AdvanceGeneration();
                    panel.SetStatus("Запись в буфере обмена");
                    return;
                }
                if (attempt < 3)
                    await Task.Delay(15);
            }
            if (revision == restoreVersion && CanUse(entry))
                panel.SetStatus("Не удалось открыть системный буфер");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or OutOfMemoryException)
        {
            panel.SetStatus("Не удалось вернуть запись в буфер");
        }
    }

    private async void BeginDrag(HistoryEntry entry, ClipboardShelf panel, Border card)
    {
        if (dragging)
            return;
        dragging = true;
        panel.SetDragging(true);
        DragPreview? preview = null;
        bool dropped = false;
        try
        {
            bool available = await Available(entry, panel);
            if (!CanUse(entry) || !panel.IsVisible ||
                System.Windows.Input.Mouse.LeftButton != MouseButtonState.Pressed)
                return;
            var transfer = new ClipboardTransfer(entry.Item, dragging: true, fromShelf: true, internalOnly: !available);
            preview = new DragPreview(entry.Item, card);
            panel.SetDragSource(entry);
            dropped = DoDragDrop(transfer, preview, CopyEffect, out int effect) == DragDrop && effect == CopyEffect;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or OutOfMemoryException)
        {
            panel.SetStatus("Не удалось перетащить запись");
        }
        finally
        {
            preview?.Finish(dropped);
            panel.SetDragging(false);
            dragging = false;
        }
    }

    private async Task<bool> Available(HistoryEntry entry, ClipboardShelf panel)
    {
        if (!CanUse(entry))
            return false;
        if (entry.Item.Payloads.Length == 0)
        {
            panel.SetStatus("Эту запись нельзя передать повторно");
            return false;
        }
        if (entry.Item.Paths.Length == 0)
            return true;
        if (checkingPaths)
        {
            panel.SetStatus("Проверка исходного объекта ещё выполняется");
            return false;
        }
        // In particular, probing a disconnected share must never block the UI or capture worker.
        string? missing;
        checkingPaths = true;
        try { missing = await Task.Run(() => entry.Item.Paths.FirstOrDefault(path => !Path.Exists(path))); }
        finally { checkingPaths = false; }
        if (!CanUse(entry))
            return false;
        if (missing is null)
            return true;
        panel.SetStatus($"Исходный объект недоступен: {Path.GetFileName(missing.TrimEnd('\\', '/'))}");
        return false;
    }

    private bool CanUse(HistoryEntry entry) =>
        Active && history.Contains(entry);

    private void ClearSystemClipboard(HistoryEntry entry, ClipboardShelf panel)
    {
        if (!ReferenceEquals(current, entry))
        {
            panel.SetStatus("В буфере уже другая запись");
            return;
        }
        bool ownsOle = published is not null && OleIsCurrentClipboard(published) == 0;
        if (!OpenClipboard(dispatcher.Handle))
        {
            panel.SetStatus("Не удалось открыть системный буфер");
            return;
        }
        try
        {
            bool same = currentSequence == GetClipboardSequenceNumber() || (ownsOle && ownClipboard == GetClipboardOwner());
            if (!same)
                panel.SetStatus("В буфере уже другая запись");
            else if (EmptyClipboard())
            {
                current = null;
                ownClipboard = dispatcher.Handle;
                currentSequence = GetClipboardSequenceNumber();
                published = null;
                restoreVersion++;
                AdvanceGeneration();
                panel.SetStatus("Системный буфер очищен");
            }
        }
        finally { CloseClipboard(); }
    }

    private void DisplayChanged(object? sender, EventArgs args) => Post(() => shelf?.Reposition());
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (args.Category == UserPreferenceCategory.Desktop)
            Post(() => shelf?.Reposition());
    }

    private void Post(Action action)
    {
        try { dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        listener?.Dispose();
        listener = null;
        worker?.Dispose();
        expiration.Stop();
        deferredSave.Stop();
        journalQueue.GetAwaiter().GetResult();
        if (favoritesSave is { Result: false })
        {
            SaveFavorites();
            journalQueue.GetAwaiter().GetResult();
        }
        SaveJournal();
        journalQueue.GetAwaiter().GetResult();
        CloseShelf();
        history.Clear();
    }

    private readonly record struct Pending(uint Sequence, DateTime Time);

    private sealed class Listener : NativeWindow, IDisposable
    {
        private readonly Action changed;

        internal Listener(nint owner, Action changed)
        {
            this.changed = changed;
            AssignHandle(owner);
            if (!AddClipboardFormatListener(Handle))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                ReleaseHandle();
                throw error;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == UpdateMessage)
                changed();
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            RemoveClipboardFormatListener(Handle);
            ReleaseHandle();
        }
    }

    private sealed class CaptureWorker : IDisposable
    {
        private readonly BlockingCollection<Action> queue = new(1);

        internal CaptureWorker()
        {
            var thread = new Thread(Run) { Name = "Wumo clipboard capture", IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Run()
        {
            int initialized = OleInitialize(0);
            try
            {
                foreach (var action in queue.GetConsumingEnumerable())
                    action();
            }
            finally
            {
                if (initialized >= 0)
                    OleUninitialize();
                queue.Dispose();
            }
        }

        internal void Enqueue(Action action) => queue.Add(action);
        public void Dispose() => queue.CompleteAdding();
    }
}
