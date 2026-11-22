using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace PixelThumbs;

public static partial class EmbeddedPreview
{
    private const int MaxArchiveBytes = 256 * 1024 * 1024;
    private const int MaxNestedBytes = 64 * 1024 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxMetadataBytes = 256 * 1024;
    private const int MaxDirectoryBytes = 12 * 1024 * 1024;
    private const int MaxEntries = 16_000;
    internal const int GCodeScanBytes = 1024 * 1024;
    private const string ThumbnailRelationship =
        "http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail";

    public static Bitmap? Read(Stream source, string extension, int size)
    {
        if (!source.CanRead || !source.CanSeek || size is < 1 or > 1024)
            return null;

        long originalPosition = source.Position;
        try
        {
            source.Position = 0;
            return extension.TrimStart('.').ToLowerInvariant() switch
            {
                "gcode" or "gco" => ReadGCode(source, size),
                "sldprt" or "sldasm" => ReadSolidWorks(source, size),
                "f3d" or "f3z" or "3mf" or "fcstd" => ReadArchive(source, extension, size),
                _ => null
            };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or
            ArgumentException or JsonException or XmlException or ExternalException)
        {
            return null;
        }
        finally
        {
            source.Position = originalPosition;
        }
    }

    private static Bitmap? ReadArchive(Stream source, string extension, int size)
    {
        if (!TryGetBoundedDirectory(source, out _, out _, out _))
            return null;

        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        return extension.TrimStart('.').ToLowerInvariant() switch
        {
            "f3d" => ReadFusionPreview(source, archive, size),
            "f3z" => ReadFusionAssembly(source, archive, size),
            "3mf" => ReadThreeMf(archive, size),
            "fcstd" => DecodeEntry(Find(archive, "thumbnails/thumbnail.png"), size),
            _ => null
        };
    }

    private static Bitmap? ReadFusionAssembly(Stream source, ZipArchive archive, int size)
    {
        byte[]? manifestBytes = ReadEntry(Find(archive, "Manifest.json"), MaxMetadataBytes, source);
        ZipArchiveEntry? root = null;
        if (manifestBytes is not null)
        {
            using var manifest = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (manifest.RootElement.ValueKind == JsonValueKind.Object &&
                manifest.RootElement.TryGetProperty("root", out JsonElement rootValue) &&
                rootValue.ValueKind == JsonValueKind.String && rootValue.GetString() is { } name &&
                name.EndsWith(".f3d", StringComparison.OrdinalIgnoreCase))
            {
                root = Find(archive, name);
            }
        }

        // A single contained design is unambiguous even if an older exporter omits the root field.
        if (root is null)
        {
            var designs = archive.Entries.Where(entry =>
                entry.FullName.EndsWith(".f3d", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (designs.Length == 1)
                root = designs[0];
        }

        if (root is null)
            return null;

        // A nested ZIP must be seekable. Decompress it only after checking its size.
        byte[]? nested = ReadEntry(root, MaxNestedBytes, source);
        if (nested is null)
            return null;
        using var nestedSource = new MemoryStream(nested, writable: false);
        return ReadArchive(nestedSource, ".f3d", size);
    }

    private static Bitmap? ReadFusionPreview(Stream source, ZipArchive archive, int size)
    {
        ZipArchiveEntry? canonical = Find(archive, "FusionAssetName[Active]/Previews/small.png");
        if (canonical is not null)
        {
            Bitmap? thumbnail = Decode(ReadEntry(canonical, MaxImageBytes, source), size);
            if (thumbnail is not null)
                return thumbnail;
        }

        foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => entry != canonical &&
                     entry.FullName.EndsWith("[Active]/Previews/small.png", StringComparison.OrdinalIgnoreCase)).Take(4))
        {
            Bitmap? image = Decode(ReadEntry(entry, MaxImageBytes, source), size);
            if (image is not null)
                return image;
        }
        return null;
    }

    private static Bitmap? ReadThreeMf(ZipArchive archive, int size)
    {
        var relationships = Find(archive, "_rels/.rels");
        if (ReadEntry(relationships, MaxMetadataBytes) is { } relationBytes)
        {
            using var xml = XmlReader.Create(new MemoryStream(relationBytes, writable: false), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxMetadataBytes,
                CloseInput = true
            });
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "Relationship" ||
                    xml.GetAttribute("Type") != ThumbnailRelationship ||
                    string.Equals(xml.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase) ||
                    xml.GetAttribute("Target") is not { } target || !TryNormalizePart(target, out string part))
                    continue;

                Bitmap? thumbnail = DecodeEntry(Find(archive, part), size);
                if (thumbnail is not null)
                    return thumbnail;
            }
        }

        foreach (string name in new[]
        {
            "Metadata/thumbnail.png", "Metadata/thumbnail.jpg", "Metadata/plate_1.png",
            "Auxiliaries/.thumbnails/thumbnail_3mf.png", "Metadata/bbl_thumbnail.png"
        })
        {
            Bitmap? thumbnail = DecodeEntry(Find(archive, name), size);
            if (thumbnail is not null)
                return thumbnail;
        }
        return null;
    }

    private static bool TryNormalizePart(string target, out string part)
    {
        part = Uri.UnescapeDataString(target).TrimStart('/');
        return part.Length > 0 && !part.AsSpan().ContainsAny('\\', ':', '#') && !part.Contains('?') &&
            !part.Split('/').Any(segment => segment is "" or "." or "..");
    }

    private static ZipArchiveEntry? Find(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase));

    private static Bitmap? DecodeEntry(ZipArchiveEntry? entry, int size) =>
        Decode(ReadEntry(entry, MaxImageBytes), size);

    private static byte[]? ReadEntry(ZipArchiveEntry? entry, int limit, Stream? fusionSource = null)
    {
        if (entry is null || entry.Length <= 0 || entry.Length > limit ||
            entry.CompressedLength <= 0 || entry.CompressedLength > MaxArchiveBytes)
            return null;

        try
        {
            using Stream payload = entry.Open();
            byte[] bytes = new byte[(int)entry.Length];
            payload.ReadExactly(bytes);
            return payload.ReadByte() == -1 ? bytes : null;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            // .NET ZIP does not read the Zstandard compression used by some Fusion exporters.
            return fusionSource is null ? null : ReadZstandardEntry(fusionSource, entry, limit);
        }
    }

    private static bool TryGetBoundedDirectory(Stream source, out ulong centralOffset, out ulong centralLength, out ulong entryCount)
    {
        centralOffset = centralLength = entryCount = 0;
        if (source.Length is < 22 or > MaxArchiveBytes)
            return false;

        // Check the central directory before ZipArchive builds a managed list of every entry.
        int tailSize = (int)Math.Min(source.Length, 65_557);
        var tail = new byte[tailSize];
        long tailOffset = source.Length - tailSize;
        source.Position = tailOffset;
        source.ReadExactly(tail);
        bool bounded = false;
        for (int index = tailSize - 22; index >= 0; index--)
        {
            ReadOnlySpan<byte> footer = tail.AsSpan(index);
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06054B50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(footer[20..]) != footer.Length - 22)
                continue;

            ushort disk = BinaryPrimitives.ReadUInt16LittleEndian(footer[4..]);
            ushort directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(footer[6..]);
            ulong entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(footer[8..]);
            ulong entries = BinaryPrimitives.ReadUInt16LittleEndian(footer[10..]);
            ulong length = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
            ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
            long directoryEnd = tailOffset + index;
            if (disk != 0 || directoryDisk != 0)
                break;

            if (entries == ushort.MaxValue || length == uint.MaxValue || offset == uint.MaxValue)
            {
                if (!ReadZip64Directory(source, directoryEnd, out entries, out length, out offset, out directoryEnd))
                    break;
                entriesOnDisk = entries;
            }
            bounded = entries == entriesOnDisk && entries is > 0 and <= MaxEntries &&
                length <= MaxDirectoryBytes && offset <= (ulong)directoryEnd && length <= (ulong)directoryEnd - offset;
            centralOffset = offset;
            centralLength = length;
            entryCount = entries;
            break;
        }
        source.Position = 0;
        return bounded;
    }

    private static bool ReadZip64Directory(Stream source, long footerOffset, out ulong entries,
        out ulong length, out ulong offset, out long directoryEnd)
    {
        entries = length = offset = 0;
        directoryEnd = 0;
        if (footerOffset < 20)
            return false;

        Span<byte> locator = stackalloc byte[20];
        source.Position = footerOffset - locator.Length;
        source.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064B50 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[4..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(locator[16..]) != 1 || footerOffset < 76)
            return false;
        ulong zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (zip64Offset > (ulong)(footerOffset - 76))
            return false;

        Span<byte> footer = stackalloc byte[56];
        source.Position = (long)zip64Offset;
        source.ReadExactly(footer);
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06064B50 ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]) != 0)
            return false;
        entries = BinaryPrimitives.ReadUInt64LittleEndian(footer[32..]);
        length = BinaryPrimitives.ReadUInt64LittleEndian(footer[40..]);
        offset = BinaryPrimitives.ReadUInt64LittleEndian(footer[48..]);
        directoryEnd = (long)zip64Offset;
        return BinaryPrimitives.ReadUInt64LittleEndian(footer[24..]) == entries;
    }

    private static Bitmap? ReadGCode(Stream source, int size)
    {
        long length = source.Length;
        if (length == 0)
            return null;

        Bitmap? image = null;
        long bestPixels = 0;
        bool scanWholeFile = length <= 2 * GCodeScanBytes;
        byte[] buffer = new byte[scanWholeFile ? (int)length : GCodeScanBytes + 1];
        try
        {
            if (scanWholeFile)
            {
                source.ReadExactly(buffer);
                ReadGCodeBlock(buffer, size, ref image, ref bestPixels);
            }
            else
            {
                Span<byte> head = buffer.AsSpan(0, GCodeScanBytes - 1);
                source.ReadExactly(head);
                int lastNewline = head.LastIndexOfAny((byte)'\r', (byte)'\n');
                ReadGCodeBlock(head[..(lastNewline + 1)], size, ref image, ref bestPixels);

                // Include the preceding byte so a marker on the first complete line is still recognized.
                source.Position = length - buffer.Length;
                source.ReadExactly(buffer);
                int firstNewline = buffer.AsSpan().IndexOfAny((byte)'\r', (byte)'\n');
                if (firstNewline >= 0)
                    ReadGCodeBlock(buffer.AsSpan(firstNewline + 1), size, ref image, ref bestPixels);
            }
            return image;
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    private static void ReadGCodeBlock(ReadOnlySpan<byte> bytes, int size, ref Bitmap? best, ref long bestPixels)
    {
        using var reader = new StringReader(Encoding.ASCII.GetString(bytes));
        string? tag = null;
        StringBuilder payload = new();
        while (reader.ReadLine() is { } line)
        {
            if (!line.TrimStart().StartsWith(';'))
            {
                tag = null;
                continue;
            }
            string comment = line.TrimStart()[1..].Trim();
            if (comment.StartsWith("thumbnail begin ", StringComparison.OrdinalIgnoreCase) ||
                comment.StartsWith("thumbnail_JPG begin ", StringComparison.OrdinalIgnoreCase))
            {
                tag = comment[..comment.IndexOf(' ')];
                payload.Clear();
                continue;
            }
            if (tag is null)
                continue;
            if (!comment.Equals(tag + " end", StringComparison.OrdinalIgnoreCase))
            {
                payload.Append(comment);
                continue;
            }

            tag = null;
            byte[] image = new byte[(payload.Length + 3) / 4 * 3];
            if (!Convert.TryFromBase64Chars(payload.ToString(), image, out int written))
                continue;
            if (!TryImageSize(image.AsSpan(0, written), out int width, out int height))
                continue;
            long pixels = (long)width * height;
            if (pixels <= bestPixels)
                continue;
            Bitmap? candidate = Decode(image.AsSpan(0, written).ToArray(), size);
            if (candidate is null)
                continue;
            best?.Dispose();
            best = candidate;
            bestPixels = pixels;
        }
    }

    private static Bitmap? Decode(byte[]? bytes, int size)
    {
        if (bytes is null || !TryImageSize(bytes, out int width, out int height) ||
            width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 8_000_000)
            return null;

        return DecodeRaster(bytes, size, width, height);
    }

    private static Bitmap? DecodeRaster(byte[] bytes, int size, int width, int height)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width != width || image.Height != height)
                return null;

            double scale = (double)size / Math.Max(width, height);
            int scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
            int scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
            var canvas = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            try
            {
                using var graphics = Graphics.FromImage(canvas);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                using var attributes = new ImageAttributes();
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(image,
                    new Rectangle((size - scaledWidth) / 2, (size - scaledHeight) / 2, scaledWidth, scaledHeight),
                    0, 0, width, height, GraphicsUnit.Pixel, attributes);
                return canvas;
            }
            catch
            {
                canvas.Dispose();
                throw;
            }
        }
        catch (Exception error) when (error is ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ reports some malformed PNG and JPEG payloads as out of memory.
            return null;
        }
    }

    private static bool TryImageSize(ReadOnlySpan<byte> image, out int width, out int height)
    {
        width = height = 0;
        if (image.Length >= 24 && image.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            image.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = BinaryPrimitives.ReadInt32BigEndian(image[16..]);
            height = BinaryPrimitives.ReadInt32BigEndian(image[20..]);
            return true;
        }
        if (image.Length < 4 || image[0] != 0xFF || image[1] != 0xD8)
            return false;

        int position = 2;
        while (position < image.Length - 3 && image[position++] == 0xFF)
        {
            while (position < image.Length - 2 && image[position] == 0xFF)
                position++;
            byte marker = image[position++];
            if (marker is 0xD9 or 0xDA)
                return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(image[position..]);
            if (length < 2 || length > image.Length - position)
                return false;
            if (length >= 8 && marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
                    0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(image[(position + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(image[(position + 5)..]);
                return true;
            }
            position += length;
        }
        return false;
    }
}
