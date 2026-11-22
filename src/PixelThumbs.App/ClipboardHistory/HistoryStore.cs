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

internal sealed record HistoryEntry(ClipboardItem Item, DateTime Time, int Copies = 1);

internal sealed class HistoryStore(long maxBytes = HistoryStore.MaxBytes, int maxEntries = 30)
{
    internal const int MaxBytes = 64 * 1024 * 1024;
    private readonly List<HistoryEntry> entries = [];

    internal IReadOnlyList<HistoryEntry> Entries => entries;
    internal long Bytes { get; private set; }

    internal HistoryEntry Add(ClipboardItem item, DateTime time, int copies = 1)
    {
        var previous = entries.Find(entry => entry.Item.Kind == item.Kind && entry.Item.Key == item.Key);
        if (previous is not null)
            Remove(previous);
        var entry = new HistoryEntry(item, time, (int)Math.Min(int.MaxValue, (long)copies + (previous?.Copies ?? 0)));
        entries.Insert(0, entry);
        Bytes += item.Size;
        while (entries.Count > maxEntries || Bytes > maxBytes)
            Remove(entries[^1]);
        return entry;
    }

    internal void Remove(HistoryEntry entry)
    {
        if (entries.Remove(entry))
            Bytes -= entry.Item.Size;
    }

    internal void AddOlder(HistoryEntry entry)
    {
        int index = entries.FindIndex(candidate => candidate.Time < entry.Time);
        entries.Insert(index < 0 ? entries.Count : index, entry);
        Bytes += entry.Item.Size;
        while (entries.Count > maxEntries || Bytes > maxBytes)
            Remove(entries[^1]);
    }

    internal void Clear()
    {
        entries.Clear();
        Bytes = 0;
    }
}
