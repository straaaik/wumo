using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Formats = System.Windows.DataFormats;
using Data = System.Windows.IDataObject;

namespace PixelThumbs.ClipboardHistory;

internal sealed record ClipboardDrop(string[] Paths, string? Text = null, string? Html = null, BitmapSource? Bitmap = null, byte[]? Png = null)
{
    private const string UrlFormat = "UniformResourceLocatorW";
    private const string UriList = "text/uri-list";

    internal static bool Supports(Data data, ClipboardKind types)
    {
        try
        {
            return Has(ClipboardKind.Files, Formats.FileDrop) || Has(ClipboardKind.Image, Formats.Bitmap, "PNG") ||
                Has(ClipboardKind.Text, UrlFormat, UriList, Formats.UnicodeText, Formats.Text);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException) { return false; }

        bool Has(ClipboardKind type, params string[] formats) =>
            (types & type) != 0 && formats.Any(format => data.GetDataPresent(format, autoConvert: false));
    }

    internal static ClipboardDrop? Read(Data data, ClipboardKind types)
    {
        if ((types & ClipboardKind.Files) != 0 && data.GetData(Formats.FileDrop, false) is string[] paths)
        {
            var valid = paths.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (valid.Length > 0)
                return new(valid);
        }
        if ((types & ClipboardKind.Image) != 0)
        {
            if (data.GetData(Formats.Bitmap, false) is BitmapSource bitmap &&
                (long)bitmap.PixelWidth * bitmap.PixelHeight <= HistoryStore.MaxBytes / 4)
            {
                if (!bitmap.IsFrozen)
                    bitmap = (BitmapSource)bitmap.GetAsFrozen();
                return new([], Bitmap: bitmap);
            }
            var encoded = data.GetData("PNG", false);
            if (encoded is MemoryStream { Length: > 0 and <= HistoryStore.MaxBytes } png)
                return new([], Png: png.ToArray());
            if (encoded is byte[] { Length: > 0 and <= HistoryStore.MaxBytes } bytes)
                return new([], Png: bytes);
        }
        if ((types & ClipboardKind.Text) == 0)
            return null;
        var text = data.GetData(Formats.UnicodeText, false) as string ?? data.GetData(Formats.Text, false) as string;
        var url = ReadUrl(data.GetData(UrlFormat, false), Encoding.Unicode) ??
            ReadUrl(data.GetData(UriList, false), Encoding.UTF8);
        if (url is not null)
            text = url;
        return text is null ? null : new([], text, data.GetData(Formats.Html, false) as string);
    }

    private static string? ReadUrl(object? data, Encoding encoding)
    {
        string? text = data switch
        {
            string value => value,
            MemoryStream { Length: > 0 and <= 65_536 } value => encoding.GetString(value.ToArray()),
            byte[] { Length: > 0 and <= 65_536 } value => encoding.GetString(value),
            _ => null
        };
        return text?.TrimEnd('\0').Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(ShelfFilters.IsLink);
    }
}
