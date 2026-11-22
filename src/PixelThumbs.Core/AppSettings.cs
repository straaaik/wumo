using System.Text.Json;

namespace PixelThumbs;

public sealed record ThumbnailStyle
{
    public int ModelColor { get; init; } = 0x75F2BF;
    public int BackgroundColor { get; init; } = 0x19232D;
    public bool Transparent { get; init; } = true;
    public bool UseSourceColors { get; init; }
    public bool PreferEmbedded { get; init; } = true;
    public float Yaw { get; init; } = 35;
    public float Pitch { get; init; } = 25;
    public float Lighting { get; init; } = 0.65f;
    public float Margin { get; init; } = 0.12f;

    internal ThumbnailStyle Normalize() => this with
    {
        ModelColor = ModelColor & 0xffffff,
        BackgroundColor = BackgroundColor & 0xffffff,
        Yaw = float.IsFinite(Yaw) ? Math.Clamp(Yaw, -180, 360) : 35,
        Pitch = float.IsFinite(Pitch) ? Math.Clamp(Pitch, -90, 90) : 25,
        Lighting = float.IsFinite(Lighting) ? Math.Clamp(Lighting, 0, 1) : 0.65f,
        Margin = float.IsFinite(Margin) ? Math.Clamp(Margin, 0, 0.35f) : 0.12f
    };
}

public sealed record FormatSettings
{
    public bool Enabled { get; init; } = true;
    public ThumbnailStyle? Style { get; init; }
}

public sealed record AppSettings
{
    public bool AutoStart { get; init; }
    public bool ThumbnailsWidgetEnabled { get; init; } = true;
    public ClipboardHistoryOptions ClipboardHistory { get; init; } = new();
    public bool QuickPreview { get; init; } = true;
    public int QuickPreviewKey { get; init; } = QuickPreviewKeys.Space;
    public int QuickPreviewSize { get; init; } = QuickPreviewKeys.DefaultSize;
    public ThumbnailStyle DefaultStyle { get; init; } = new();
    public Dictionary<string, FormatSettings> Formats { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("PIXELTHUMBS_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelThumbs");

    public static AppSettings Load()
    {
        try
        {
            using var file = File.Open(Path.Combine(DataDirectory, "settings.json"), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (file.Length <= 1_048_576)
                return Normalize(JsonSerializer.Deserialize<AppSettings>(file) ?? new AppSettings());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return Normalize(new AppSettings());
    }

    public static void Save(AppSettings settings)
    {
        string folder = DataDirectory;
        Directory.CreateDirectory(folder);
        string staging = Path.Combine(folder, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, Normalize(settings), Json);
                file.Flush(flushToDisk: true);
            }
            File.Move(staging, Path.Combine(folder, "settings.json"), overwrite: true);
        }
        catch
        {
            try { File.Delete(staging); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var known = new Dictionary<string, FormatSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, extension, _, _, _) in FormatCatalog.Entries)
            known.Add(extension, new FormatSettings());

        if (settings.Formats is { } savedFormats)
        {
            foreach (var (extension, format) in savedFormats)
                if (format is not null && known.ContainsKey(extension))
                    known[extension] = format with { Style = format.Style?.Normalize() };
        }

        return settings with
        {
            DefaultStyle = settings.DefaultStyle?.Normalize() ?? new ThumbnailStyle(),
            QuickPreviewKey = QuickPreviewKeys.Normalize(settings.QuickPreviewKey),
            QuickPreviewSize = QuickPreviewKeys.NormalizeSize(settings.QuickPreviewSize),
            ClipboardHistory = (settings.ClipboardHistory ?? new()).Normalize(),
            Formats = known
        };
    }
}
