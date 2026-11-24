using System.Windows.Media.Imaging;

namespace PixelThumbs.ClipboardHistory;

internal sealed record ClipboardPayload(uint Format, int Index, byte[] Bytes, bool Stream = false);

internal sealed record ClipboardItem(ClipboardKind Kind, string Key, string Label, ClipboardPayload[] Payloads,
    string[] Paths, BitmapSource? Thumbnail, ClipboardPayload? Image, bool Incomplete, string? TextPreview)
{
    // Include the decoded display thumbnail and path strings retained alongside CF_HDROP.
    internal long Size => Payloads.Sum(part => (long)part.Bytes.Length) + Paths.Sum(path => path.Length * 2L) +
        (Thumbnail is null ? 0 : (long)Thumbnail.PixelWidth * Thumbnail.PixelHeight * 4) + 2L * (Label.Length + (TextPreview?.Length ?? 0));
}

internal sealed record HistoryEntry(ClipboardItem Item, DateTime Time, int Copies = 1, string? Name = null);

internal sealed record HistoryFolder(Guid Id, string Name, DateTime Time, HistoryEntry[] Entries);

internal sealed record HistoryFavorites(HistoryEntry[] Entries, HistoryFolder[] Folders);

internal sealed class HistoryStore(long maxBytes = HistoryStore.MaxBytes, int maxEntries = 30)
{
    internal const int MaxBytes = 64 * 1024 * 1024;
    private readonly List<HistoryEntry> entries = [];
    private readonly List<HistoryEntry> favorites = [];
    private readonly List<HistoryFolder> folders = [];

    internal IReadOnlyList<HistoryEntry> Entries => entries;
    internal IReadOnlyList<HistoryEntry> Favorites => favorites;
    internal IReadOnlyList<HistoryFolder> Folders => folders;
    internal long Bytes { get; private set; }

    internal HistoryEntry Add(ClipboardItem item, DateTime time, int copies = 1)
    {
        if (SavedEntries().FirstOrDefault(entry => SameItem(entry.Item, item)) is { } saved)
            return saved;
        var previous = entries.Find(entry => SameItem(entry.Item, item));
        if (previous is not null)
            Remove(previous);
        var entry = new HistoryEntry(item, time,
            (int)Math.Min(int.MaxValue, (long)copies + (previous?.Copies ?? 0)), previous?.Name);
        entries.Insert(0, entry);
        Bytes += item.Size;
        Trim();
        return entry;
    }

    internal bool Remove(HistoryEntry entry)
    {
        if (!entries.Remove(entry))
            return false;
        Bytes -= entry.Item.Size;
        return true;
    }

    internal void AddOlder(HistoryEntry entry)
    {
        if (SavedEntries().Any(saved => SameItem(saved.Item, entry.Item)))
            return;
        int index = entries.FindIndex(candidate => candidate.Time < entry.Time);
        entries.Insert(index < 0 ? entries.Count : index, entry);
        Bytes += entry.Item.Size;
        Trim();
    }

    internal bool Contains(HistoryEntry entry) => entries.Contains(entry) || SavedEntries().Contains(entry);

    internal bool TogglePin(HistoryEntry entry, DateTime time, out HistoryEntry replacement)
    {
        replacement = entry;
        if (favorites.Remove(entry))
        {
            replacement = entry with { Time = time };
            AddOlder(replacement);
            return true;
        }
        if (!Remove(entry) && !RemoveSaved(entry))
            return false;
        favorites.Add(entry);
        return true;
    }

    internal bool RemoveSaved(HistoryEntry entry) => favorites.Remove(entry) || UpdateFolderEntry(entry, null);

    internal bool RemoveFolder(Guid id) => folders.RemoveAll(folder => folder.Id == id) > 0;

    internal bool Rename(HistoryEntry entry, HistoryEntry replacement)
    {
        int index = favorites.IndexOf(entry);
        if (index < 0)
            return UpdateFolderEntry(entry, replacement);
        favorites[index] = replacement;
        return true;
    }

    internal bool RenameFolder(Guid id, string name)
    {
        int index = folders.FindIndex(folder => folder.Id == id);
        if (index < 0)
            return false;
        folders[index] = folders[index] with { Name = name };
        return true;
    }

    internal bool Group(HistoryEntry source, HistoryEntry target)
    {
        if (source == target || !IsRoot(source) || !IsRoot(target))
            return false;
        RemoveRoot(source);
        RemoveRoot(target);
        folders.Add(new(Guid.NewGuid(), "Новая папка", source.Time > target.Time ? source.Time : target.Time, [target, source]));
        return true;
    }

    internal bool AddToFolder(HistoryEntry entry, Guid id)
    {
        int index = folders.FindIndex(folder => folder.Id == id);
        if (index < 0 || !IsRoot(entry))
            return false;
        RemoveRoot(entry);
        var folder = folders[index];
        folders[index] = folder with { Entries = [.. folder.Entries, entry] };
        return true;
    }

    internal HistoryFavorites SnapshotFavorites() => new([.. favorites], [.. folders]);

    internal bool LoadFavorites(HistoryFavorites saved)
    {
        favorites.AddRange(saved.Entries);
        folders.AddRange(saved.Folders);
        var keys = SavedEntries().Select(entry => (entry.Item.Kind, entry.Item.Key)).ToHashSet();
        bool changed = false;
        foreach (var entry in entries.ToArray())
            if (keys.Contains((entry.Item.Kind, entry.Item.Key)))
                changed |= Remove(entry);
        return changed;
    }

    private IEnumerable<HistoryEntry> SavedEntries() => favorites.Concat(folders.SelectMany(folder => folder.Entries));

    private bool IsRoot(HistoryEntry entry) => entries.Contains(entry) || favorites.Contains(entry);

    private void RemoveRoot(HistoryEntry entry)
    {
        if (!Remove(entry))
            favorites.Remove(entry);
    }

    private bool UpdateFolderEntry(HistoryEntry entry, HistoryEntry? replacement)
    {
        for (int index = 0; index < folders.Count; index++)
        {
            var folder = folders[index];
            int child = Array.IndexOf(folder.Entries, entry);
            if (child < 0)
                continue;
            HistoryEntry[] children = replacement is null
                ? [.. folder.Entries.Take(child), .. folder.Entries.Skip(child + 1)]
                : [.. folder.Entries];
            if (replacement is not null)
                children[child] = replacement;
            folders[index] = folder with { Entries = children };
            return true;
        }
        return false;
    }

    private static bool SameItem(ClipboardItem left, ClipboardItem right) => left.Kind == right.Kind && left.Key == right.Key;

    private void Trim()
    {
        while (entries.Count > maxEntries || Bytes > maxBytes)
            Remove(entries[^1]);
    }

    internal void Clear()
    {
        entries.Clear();
        Bytes = 0;
    }
}
