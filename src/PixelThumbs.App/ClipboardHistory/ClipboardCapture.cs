using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using static PixelThumbs.ClipboardHistory.ClipboardNative;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace PixelThumbs.ClipboardHistory;

internal static class ClipboardCapture
{
    private const int CaptureLimit = HistoryStore.MaxBytes - 256 * 1024;
    private const int MaxFormats = 128;
    private const int MaxVirtualFiles = 1024;
    private const int DescriptorChars = 260;
    private const int DescriptorPrefix = 72;
    private static readonly uint ExcludeFromHistory = RegisterClipboardFormat("ExcludeClipboardContentFromMonitor");
    private static readonly uint IncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly HashSet<string> Metadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "DataObject", "Ole Private Data", "Preferred DropEffect", "Performed DropEffect", "Logical Performed DropEffect",
        "Paste Succeeded", "DropDescription", "DragImageBits", "DragContext", "DragSourceHelperFlags", "InShellDragLoop",
        "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", "ExcludeClipboardContentFromMonitor"
    };

    internal static (uint Sequence, ClipboardItem? Item) Read(uint sequence, ClipboardKind enabled)
    {
        var formats = new List<uint>();
        var parts = new List<ClipboardPayload>();
        string[] paths = [];
        int remaining = CaptureLimit;
        nint owner;
        if (!OpenClipboard(0))
            return (0, null);
        try
        {
            if (sequence != GetClipboardSequenceNumber())
                return (GetClipboardSequenceNumber(), null);
            for (uint format = 0; formats.Count < MaxFormats && (format = EnumClipboardFormats(format)) != 0;)
                formats.Add(format);
            if (formats.Contains(ExcludeFromHistory) || formats.Contains(IncludeInHistory) &&
                ReadGlobal(GetClipboardData(IncludeInHistory), 16) is { Length: >= 4 } privacy &&
                BinaryPrimitives.ReadInt32LittleEndian(privacy) == 0)
                return (GetClipboardSequenceNumber(), null);
            if ((Classify(formats) & enabled) == 0)
                return (GetClipboardSequenceNumber(), null);
            if (formats.Contains(DropFiles))
            {
                var drop = ReadGlobal(GetClipboardData(DropFiles), remaining / 2);
                if (drop is not null)
                {
                    paths = ReadPaths(drop);
                    if (paths.Length > 0)
                    {
                        parts.Add(new(DropFiles, -1, drop));
                        remaining -= drop.Length + paths.Sum(path => path.Length * 2);
                    }
                }
            }
            bool hasDib = false;
            foreach (uint format in formats.OrderBy(Priority))
            {
                if (format == DropFiles || !CanRead(format, paths.Length > 0))
                    continue;
                // Windows synthesizes the other DIB variant. A second copy can consume another 32 MiB per 4K image.
                if (hasDib && format is Dib or DibV5)
                    continue;
                nint handle = GetClipboardData(format);
                var bytes = format == EnhancedMetafile ? ReadMetafile(handle, remaining) : ReadGlobal(handle, remaining);
                if (bytes is not null)
                {
                    parts.Add(new(format, -1, bytes));
                    remaining -= bytes.Length;
                    hasDib |= format is Dib or DibV5;
                }
            }
            sequence = GetClipboardSequenceNumber();
            owner = GetClipboardOwner();
        }
        finally { CloseClipboard(); }

        var kindAfterCapture = Classify(formats);
        var virtualFiles = parts.FirstOrDefault(part => part.Format == Descriptor || part.Format == AnsiDescriptor);
        bool incomplete = formats.Count == MaxFormats;
        if (paths.Length == 0 && formats.Any(format => format == Contents || format == Descriptor || format == AnsiDescriptor))
        {
            if (sequence == GetClipboardSequenceNumber() && OleGetClipboard(out var source) >= 0 && source is not null)
            {
                try
                {
                    virtualFiles ??= ReadOleDescriptor(source, formats, parts, ref remaining);
                    incomplete |= !ReadVirtualFiles(source, virtualFiles, parts, ref remaining);
                }
                finally { if (Marshal.IsComObject(source)) Marshal.ReleaseComObject(source); }
            }
            else
            {
                incomplete = true;
                parts.RemoveAll(part => part.Format == Descriptor || part.Format == AnsiDescriptor);
            }
        }
        if (kindAfterCapture == ClipboardKind.Other)
        {
            var missing = formats.Where(id => IsContent(id) && !parts.Exists(part => part.Format == id)).ToArray();
            if (missing.Length > 0 && sequence == GetClipboardSequenceNumber() && OleGetClipboard(out var source) >= 0 && source is not null)
            {
                try
                {
                    foreach (uint format in missing.Where(id => id >= 0xc000))
                        if (TryReadOle(source, format, -1, remaining, out var part))
                        {
                            parts.Add(part);
                            remaining -= part.Bytes.Length;
                        }
                }
                finally { if (Marshal.IsComObject(source)) Marshal.ReleaseComObject(source); }
            }
            incomplete |= missing.Any(id => !parts.Exists(part => part.Format == id));
        }
        uint capturedSequence = GetClipboardSequenceNumber();
        if (sequence != capturedSequence && GetClipboardOwner() != owner)
            capturedSequence = sequence;

        if (formats.Count == 0 || !formats.Any(IsContent))
            return (capturedSequence, null);
        if (kindAfterCapture == ClipboardKind.Text && !parts.Any(part => part.Format is Unicode or Text or OemText || part.Format == Url))
        {
            byte[]? fallback = ClipboardText.PlainFromRich(parts);
            if (fallback is not null && fallback.Length <= remaining)
                parts.Add(new(Unicode, -1, fallback));
        }
        var image = parts.FirstOrDefault(part => IsImage(part.Format));
        BitmapSource? thumbnail = image is null ? null : ClipboardImages.Thumbnail(image);
        if (thumbnail is null && image is not null)
        {
            foreach (var alternate in parts.Where(part => part != image && IsImage(part.Format)))
                if ((thumbnail = ClipboardImages.Thumbnail(alternate)) is not null)
                {
                    image = alternate;
                    break;
                }
        }
        string? preview = ReadText(parts, 280);
        string label = kindAfterCapture switch
        {
            ClipboardKind.Text => preview ?? (parts.Exists(part => part.Format == Url) ? "Ссылка" : "Форматированный текст"),
            ClipboardKind.Image => "Изображение",
            ClipboardKind.Files when paths.Length == 1 => Path.GetFileName(paths[0].TrimEnd('\\', '/')),
            ClipboardKind.Files when paths.Length > 1 => $"{paths.Length} объектов",
            ClipboardKind.Files => VirtualLabel(virtualFiles),
            _ => FormatName(formats.First(IsContent))
        };
        incomplete |= kindAfterCapture switch
        {
            ClipboardKind.Image => image is null,
            ClipboardKind.Files => paths.Length == 0 && !parts.Exists(part => part.Format == Descriptor || part.Format == AnsiDescriptor),
            ClipboardKind.Text => !parts.Exists(part => IsText(part.Format)),
            _ => parts.Count == 0
        };
        string key = Fingerprint(kindAfterCapture, capturedSequence, parts, image, paths);
        return (capturedSequence, new ClipboardItem(kindAfterCapture, key, label, [.. parts], paths, thumbnail, image, incomplete, preview));
    }

    private static ClipboardKind Classify(List<uint> formats) =>
        formats.Any(format => format == DropFiles || format == Descriptor || format == AnsiDescriptor || format == Contents) ? ClipboardKind.Files :
        formats.Any(format => IsImage(format) || format == ClipboardNative.Bitmap) ? ClipboardKind.Image :
        formats.Any(IsText) ? ClipboardKind.Text : ClipboardKind.Other;

    private static bool IsImage(uint format) => format == Png || format is Dib or DibV5 || ClipboardImages.IsEncoded(format);
    private static bool IsText(uint format) => format is Unicode or Text or OemText || format == Rtf || format == Html || format == Url;
    private static int Priority(uint format) =>
        format == Unicode || format == Png || format == Descriptor ? 0 :
        format is Dib or DibV5 || format == Html || format == Rtf || format == AnsiDescriptor ? 1 :
        format == Url ? 2 : 3;

    private static bool IsContent(uint format) => format != Locale &&
        (format < 0xc000 || !Metadata.Contains(FormatName(format)));

    private static bool CanRead(uint format, bool physicalFiles)
    {
        if (format < 0xc000)
            return format is Text or OemText or Dib or Unicode or EnhancedMetafile or Locale or DibV5 or 4 or 6 or 10 or 11 or 12;
        if (format == Contents || !IsContent(format))
            return false;
        if (!physicalFiles)
            return true;
        return IsText(format) || IsImage(format);
    }

    internal static string? ReadText(IEnumerable<ClipboardPayload> parts, int maxChars)
    {
        var part = parts.FirstOrDefault(part => part.Format == Unicode) ??
            parts.FirstOrDefault(part => part.Format == Url);
        string? decoded;
        if (part is not null)
            decoded = Encoding.Unicode.GetString(part.Bytes, 0, Math.Min(part.Bytes.Length / 2, maxChars) * 2);
        else
        {
            part = parts.FirstOrDefault(part => part.Format is Text or OemText);
            if (part is null)
                return null;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = part.Format == Text
                ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage
                : System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            decoded = Encoding.GetEncoding(codePage).GetString(part.Bytes, 0, Math.Min(part.Bytes.Length, maxChars));
        }
        int end = decoded.IndexOf('\0');
        return end < 0 ? decoded : decoded[..end];
    }

    internal static bool ContainsText(ClipboardPayload[] parts, string query)
    {
        var unicode = Array.Find(parts, part => part.Format == Unicode || part.Format == Url);
        if (unicode is null)
            return ReadText(parts, CaptureLimit)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        var text = MemoryMarshal.Cast<byte, char>(unicode.Bytes.AsSpan());
        int end = text.IndexOf('\0');
        return (end < 0 ? text : text[..end]).Contains(query.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    internal static ClipboardItem? FromDrop(ClipboardDrop source)
    {
        ClipboardKind kind;
        var parts = new List<ClipboardPayload>();
        ClipboardPayload? image = null;
        BitmapSource? thumbnail = null;
        string? preview = null;
        string label;
        if (source.Paths.Length > 0)
        {
            kind = ClipboardKind.Files;
            long chars = 1 + source.Paths.Sum(path => path.Length + 1L);
            if (chars * 4 > CaptureLimit)
                return null;
            var bytes = new byte[20 + (int)chars * 2];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 20);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1);
            int offset = 20;
            foreach (string path in source.Paths)
                offset += Encoding.Unicode.GetBytes(path.AsSpan(), bytes.AsSpan(offset)) + 2;
            parts.Add(new(DropFiles, -1, bytes));
            label = source.Paths.Length == 1 ? Path.GetFileName(source.Paths[0].TrimEnd('\\', '/')) : $"{source.Paths.Length} объектов";
            if (label.Length == 0)
                label = source.Paths[0];
        }
        else if (source.Bitmap is not null || source.Png is not null)
        {
            kind = ClipboardKind.Image;
            if (source.Bitmap is { } bitmap)
                image = ClipboardImages.FromBitmap(bitmap, CaptureLimit);
            else if (source.Png is { Length: > 0 and <= CaptureLimit } png)
                image = new(Png, -1, png);
            if (image is null || (thumbnail = ClipboardImages.Thumbnail(image)) is null)
                return null;
            parts.Add(image);
            label = "Изображение";
        }
        else if (!string.IsNullOrWhiteSpace(source.Text) && (source.Text.Length + 1L) * 2 <= CaptureLimit)
        {
            kind = ClipboardKind.Text;
            byte[] unicode = Encoding.Unicode.GetBytes(source.Text + '\0');
            parts.Add(new(Unicode, -1, unicode));
            if (source.Html is { Length: > 0 } html && Encoding.UTF8.GetByteCount(html) + 1L + unicode.Length <= CaptureLimit)
                parts.Add(new(Html, -1, Encoding.UTF8.GetBytes(html + '\0')));
            label = preview = source.Text[..Math.Min(280, source.Text.Length)];
        }
        else
            return null;

        return new ClipboardItem(kind, Fingerprint(kind, 0, parts, image, source.Paths), label,
            [.. parts], source.Paths, thumbnail, image, false, preview);
    }

    private static string[] ReadPaths(byte[] bytes)
    {
        if (bytes.Length < 20)
            return [];
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (offset < 20 || offset >= bytes.Length)
            return [];
        bool wide = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)) != 0;
        Encoding encoding = Encoding.Unicode;
        if (!wide)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        var paths = new List<string>();
        int step = wide ? 2 : 1;
        int start = offset;
        for (int end = offset; end <= bytes.Length - step; end += step)
        {
            if (bytes[end] != 0 || wide && bytes[end + 1] != 0)
                continue;
            if (end == start)
                return [.. paths];
            if (paths.Count == 100_000)
                return [];
            paths.Add(encoding.GetString(bytes, start, end - start));
            start = end + step;
        }
        return [];
    }

    private static string Fingerprint(ClipboardKind kind, uint sequence, List<ClipboardPayload> parts, ClipboardPayload? image, string[] paths)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (paths.Length > 0)
        {
            foreach (string path in paths.Order(StringComparer.Ordinal))
            {
                digest.AppendData(MemoryMarshal.AsBytes(path.AsSpan()));
                digest.AppendData([0, 0]);
            }
        }
        else
        {
            IEnumerable<ClipboardPayload> identity = kind switch
            {
                ClipboardKind.Image when image is not null => [image],
                ClipboardKind.Text => parts.Where(part => part.Format == Unicode || part.Format == Rtf || part.Format == Html),
                _ => parts.Where(part => part.Format != Locale)
            };
            if (!identity.Any())
                identity = parts;
            Span<byte> header = stackalloc byte[12];
            foreach (var (part, name) in identity.Select(part => (Part: part, Name: part.Format >= 0xc000 ? FormatName(part.Format) : ""))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal).ThenBy(entry => entry.Name.Length == 0 ? entry.Part.Format : 0)
                .ThenBy(entry => entry.Part.Index))
            {
                int length = part.Bytes.Length;
                if (part.Format == Unicode)
                {
                    int terminator = MemoryMarshal.Cast<byte, char>(part.Bytes.AsSpan()).IndexOf('\0');
                    if (terminator >= 0)
                        length = terminator * 2;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(header, name.Length == 0 ? part.Format : 0xc000);
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], part.Index);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], length);
                digest.AppendData(header);
                if (name.Length > 0)
                {
                    digest.AppendData(Encoding.UTF8.GetBytes(name));
                    digest.AppendData([0]);
                }
                digest.AppendData(part.Bytes.AsSpan(0, length));
            }
            if (parts.Count == 0)
                digest.AppendData(BitConverter.GetBytes(sequence));
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static ClipboardPayload? ReadOleDescriptor(ComDataObject source, List<uint> formats,
        List<ClipboardPayload> parts, ref int remaining)
    {
        foreach (uint id in formats.Where(id => id == Descriptor || id == AnsiDescriptor))
            if (TryReadOle(source, id, -1, remaining, out var descriptor))
            {
                parts.Add(descriptor);
                remaining -= descriptor.Bytes.Length;
                return descriptor;
            }
        return null;
    }

    private static int DescriptorSize(ClipboardPayload descriptor) =>
        DescriptorPrefix + DescriptorChars * (descriptor.Format == Descriptor ? 2 : 1);

    private static bool ReadVirtualFiles(ComDataObject source, ClipboardPayload? descriptor,
        List<ClipboardPayload> parts, ref int remaining)
    {
        int count = descriptor is { Bytes.Length: >= 4 } ? BinaryPrimitives.ReadInt32LittleEndian(descriptor.Bytes) : 0;
        int size = descriptor is null ? 0 : DescriptorSize(descriptor);
        if (descriptor is null || count <= 0 || count > MaxVirtualFiles || 4L + size * count > descriptor.Bytes.Length)
        {
            parts.RemoveAll(part => part.Format == Descriptor || part.Format == AnsiDescriptor);
            return false;
        }
        var available = new List<int>(count);
        for (int index = 0; index < count; index++)
        {
            var data = descriptor.Bytes.AsSpan(4 + size * index, size);
            int flags = BinaryPrimitives.ReadInt32LittleEndian(data);
            int attributes = BinaryPrimitives.ReadInt32LittleEndian(data[36..]);
            bool directory = (flags & 4) != 0 && (attributes & 0x10) != 0;
            if (!directory)
            {
                if (!TryReadOle(source, Contents, index, remaining, out var part) &&
                    (count != 1 || !TryReadOle(source, Contents, -1, remaining, out part)))
                    continue;
                parts.Add(part with { Index = available.Count });
                remaining -= part.Bytes.Length;
            }
            available.Add(index);
        }
        if (available.Count == count)
            return true;
        parts.RemoveAll(part => part.Format == Descriptor || part.Format == AnsiDescriptor);
        if (available.Count > 0)
        {
            var filtered = new byte[4 + available.Count * size];
            BinaryPrimitives.WriteInt32LittleEndian(filtered, available.Count);
            for (int index = 0; index < available.Count; index++)
                descriptor.Bytes.AsSpan(4 + available[index] * size, size).CopyTo(filtered.AsSpan(4 + index * size, size));
            parts.Add(descriptor with { Bytes = filtered });
        }
        return false;
    }

    private static string VirtualLabel(ClipboardPayload? descriptor)
    {
        if (descriptor is null || descriptor.Bytes.Length < 4 + DescriptorSize(descriptor))
            return "Виртуальный файл";
        int count = BinaryPrimitives.ReadInt32LittleEndian(descriptor.Bytes);
        if (count != 1)
            return $"Виртуальные файлы ({count})";
        var encoding = descriptor.Format == Descriptor ? Encoding.Unicode : Encoding.Latin1;
        return encoding.GetString(descriptor.Bytes, 4 + DescriptorPrefix, DescriptorSize(descriptor) - DescriptorPrefix).Split('\0')[0];
    }

    private static bool TryReadOle(ComDataObject source, uint format, int index, int remaining, out ClipboardPayload part)
    {
        return TryReadOle(source, format, index, TYMED.TYMED_ISTREAM, remaining, out part) ||
            TryReadOle(source, format, index, TYMED.TYMED_HGLOBAL, remaining, out part);
    }

    private static bool TryReadOle(ComDataObject source, uint format, int index, TYMED medium, int remaining, out ClipboardPayload part)
    {
        part = null!;
        var request = Format(format, index, medium);
        try
        {
            source.GetData(ref request, out var storage);
            try
            {
                var bytes = storage.tymed switch
                {
                    TYMED.TYMED_HGLOBAL => ReadGlobal(storage.unionmember, remaining),
                    TYMED.TYMED_ISTREAM => ReadStream(storage.unionmember, remaining),
                    _ => null
                };
                if (bytes is null)
                    return false;
                part = new ClipboardPayload(format, index, bytes, storage.tymed == TYMED.TYMED_ISTREAM);
                return true;
            }
            finally { ReleaseStgMedium(ref storage); }
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static byte[]? ReadStream(nint pointer, int remaining)
    {
        if (pointer == 0 || Marshal.GetObjectForIUnknown(pointer) is not IStream stream)
            return null;
        nint read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            // Some providers leave the stream at the end after writing it.
            try { stream.Seek(0, 0, 0); }
            catch (COMException) { }
            using var content = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int requested = Math.Min(buffer.Length, remaining - (int)content.Length + 1);
                Marshal.WriteInt32(read, 0);
                stream.Read(buffer, requested, read);
                int received = Marshal.ReadInt32(read);
                if (received == 0)
                    return content.ToArray();
                if (received < 0 || received > requested || received > remaining - content.Length)
                    return null;
                content.Write(buffer, 0, received);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
            if (Marshal.IsComObject(stream))
                Marshal.ReleaseComObject(stream);
        }
    }
}
