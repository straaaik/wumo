namespace PixelThumbs.ClipboardHistory;

internal enum ShelfFilter { All, Text, Links, Images, Files, Other, Favorites }

internal static class ShelfFilters
{
    internal static ShelfFilter Category(ClipboardItem item) => item.Kind switch
    {
        ClipboardKind.Image => ShelfFilter.Images,
        ClipboardKind.Files => ShelfFilter.Files,
        ClipboardKind.Other => ShelfFilter.Other,
        _ => item.Payloads.Any(part => part.Format == ClipboardNative.Url) || IsLink(item.TextPreview)
            ? ShelfFilter.Links : ShelfFilter.Text
    };

    internal static bool IsLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var address) &&
        address.Scheme is "http" or "https" or "mailto";

    internal static bool Matches(ClipboardItem item, ShelfFilter filter, string query) =>
        (filter == ShelfFilter.All || Category(item) == filter) &&
        (query.Length == 0 || Contains(item.Label, query) || item.Paths.Any(path => Contains(path, query)) ||
            Contains(item.TextPreview, query) ||
            item.Kind == ClipboardKind.Text && ClipboardCapture.ContainsText(item.Payloads, query));

    internal static bool Contains(string? text, string query) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
