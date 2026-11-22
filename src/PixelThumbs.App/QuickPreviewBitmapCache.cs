namespace PixelThumbs;

internal sealed class QuickPreviewBitmapCache : IDisposable
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private readonly object sync = new();
    private readonly LinkedList<Entry> recent = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> entries = new();
    private int usedBytes;
    private int generation;
    private bool disposed;

    internal Bitmap? Render(FileInfo file, int size, ThumbnailStyle style, CancellationToken cancellation,
        Action<RenderDiagnostics> diagnostics)
    {
        cancellation.ThrowIfCancellationRequested();
        string path = file.FullName;
        if (ReadVersion(file, refresh: false) is not { } version)
            return PreviewService.RenderFile(path, size, style, cancellation, diagnostics);
        var key = new Key(path, version, size, style);
        int revision;
        lock (sync)
        {
            if (disposed)
                return null;
            revision = generation;
            if (entries.TryGetValue(key, out var hit))
            {
                recent.Remove(hit);
                recent.AddFirst(hit);
                return (Bitmap)hit.Value.Image.Clone();
            }
        }

        Bitmap? image = PreviewService.RenderFile(path, size, style, cancellation, diagnostics);
        if (image is null)
            return null;
        if (cancellation.IsCancellationRequested)
        {
            image.Dispose();
            return null;
        }
        if (ReadVersion(file, refresh: true) == version)
            Store(key, image, revision);
        return image;
    }

    private void Store(Key key, Bitmap image, int revision)
    {
        int bytes = image.Width * image.Height * 4;
        lock (sync)
        {
            if (disposed || revision != generation || bytes > MaximumBytes || entries.ContainsKey(key))
                return;
            while (usedBytes > MaximumBytes - bytes && recent.Last is { } oldest)
            {
                recent.RemoveLast();
                entries.Remove(oldest.Value.Key);
                usedBytes -= oldest.Value.Bytes;
                oldest.Value.Image.Dispose();
            }
            entries.Add(key, recent.AddFirst(new Entry(key, (Bitmap)image.Clone(), bytes)));
            usedBytes += bytes;
        }
    }

    private static FileVersion? ReadVersion(FileInfo file, bool refresh)
    {
        try
        {
            if (refresh)
                file.Refresh();
            return file.Exists ? new FileVersion(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            ClearEntries();
        }
    }

    internal void Clear()
    {
        lock (sync)
            ClearEntries();
    }

    private void ClearEntries()
    {
        generation++;
        foreach (var entry in recent)
            entry.Image.Dispose();
        recent.Clear();
        entries.Clear();
        usedBytes = 0;
    }

    private readonly record struct FileVersion(long Length, DateTime LastWrite, DateTime Created);
    private readonly record struct Key(string Path, FileVersion Version, int Size, ThumbnailStyle Style);
    private sealed record Entry(Key Key, Bitmap Image, int Bytes);
}
