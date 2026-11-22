using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using static PixelThumbs.ClipboardHistory.ClipboardNative;

namespace PixelThumbs.ClipboardHistory;

internal static partial class ClipboardText
{
    // Most applications publish CF_UNICODETEXT themselves. Bound the optional rich-text decode.
    private const int RichDecodeLimit = 1024 * 1024;

    internal static byte[]? PlainFromRich(List<ClipboardPayload> parts)
    {
        var rich = parts.Find(part => part.Format == Rtf && part.Bytes.Length <= RichDecodeLimit);
        string? text = rich is null ? null : FromRtf(rich.Bytes);
        if (text is null && parts.Find(part => part.Format == Html && part.Bytes.Length <= RichDecodeLimit) is { } html)
            text = FromHtml(html.Bytes);
        return text is null ? null : Encoding.Unicode.GetBytes(text + '\0');
    }

    private static string? FromRtf(byte[] bytes)
    {
        try
        {
            var document = new FlowDocument();
            var selection = new TextRange(document.ContentStart, document.ContentEnd);
            using var input = new MemoryStream(bytes, writable: false);
            selection.Load(input, System.Windows.DataFormats.Rtf);
            return selection.Text.TrimEnd('\r', '\n');
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string FromHtml(byte[] bytes)
    {
        string content = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        const string marker = "<!--StartFragment-->";
        int start = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        int end = content.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end >= start + marker.Length)
            content = content[(start + marker.Length)..end];
        else if (content.IndexOf('<') is int tag && tag >= 0)
            content = content[tag..];
        return WebUtility.HtmlDecode(Tags().Replace(LineBreaks().Replace(content, Environment.NewLine), ""));
    }

    [GeneratedRegex(@"</?(?:p|div|li)\b[^>]*>|<br\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.NonBacktracking)]
    private static partial Regex Tags();
}
