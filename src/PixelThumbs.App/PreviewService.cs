using System.Runtime.InteropServices;

namespace PixelThumbs;



public static class PreviewService
{
    public static Bitmap RenderSample(int size, ThumbnailStyle style) => MeshRenderer.RenderSample(size, style);

    public static Bitmap? RenderFile(string path, int size, ThumbnailStyle style,
        CancellationToken cancellation = default, Action<RenderDiagnostics>? diagnostics = null)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return WorkerClient.RenderForDesktop(stream, Path.GetExtension(path).ToLowerInvariant(), size, style, cancellation, diagnostics);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics?.Invoke(new RenderDiagnostics(RenderFailure.ReadError));
            return null;
        }
    }

    public static Task<int> RefreshAsync(IEnumerable<string> paths)
    {
        if (!SettingsStore.Load().ThumbnailsWidgetEnabled)
            return Task.FromResult(0);
        var items = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.Run(() => items.Count(path => ShellViews.Refresh(path)));
    }

    internal static string FailureMessage(RenderFailure failure) => failure switch
    {
        RenderFailure.ReadError => "Файл не удалось прочитать или в нём нет корректной геометрии.",
        RenderFailure.NoEmbeddedImage => "В файле нет корректного встроенного эскиза.",
        RenderFailure.ExternalRenderer => "Комплектный F3D не смог построить миниатюру.",
        RenderFailure.MemoryLimit => "Достигнут лимит памяти рендера.",
        RenderFailure.InputLimit => "Файл превышает допустимый размер для рендера.",
        RenderFailure.Timeout => "Истекло время рендера.",
        RenderFailure.Isolation => "Не удалось установить ограничение памяти процесса рендера.",
        RenderFailure.WorkerError => "Процесс рендера завершился с ошибкой.",
        _ => "Миниатюра недоступна."
    };
}

internal static class ShellViews
{
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid CacheClassId = new("50EF4544-AC9F-4A8E-B21B-8A26180DB13F");
    private static readonly uint[] CommonThumbnailSizes = [32, 96, 256];
    private const uint ForceExtraction = 4;
    private const int AssociationsChanged = 0x08000000;
    private const int ItemUpdated = 0x00002000;
    private const uint UnicodePath = 5;

    internal static void NotifyAssociations() => SHChangeNotify(AssociationsChanged, 0, null, 0);

    internal static bool Refresh(string path, CancellationToken cancellation = default)
    {
        nint shellItem = 0;
        nint bitmap = 0;
        IThumbnailCache? cache = null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (SHCreateItemFromParsingName(fullPath, 0, ShellItemId, out shellItem) < 0)
                return false;
            cache = (IThumbnailCache)Activator.CreateInstance(Type.GetTypeFromCLSID(CacheClassId)!)!;
            bool updated = false;
            foreach (uint size in CommonThumbnailSizes)
            {
                if (cancellation.IsCancellationRequested)
                    break;
                updated |= cache.GetThumbnail(shellItem, size, ForceExtraction, out bitmap, 0, 0) >= 0 && bitmap != 0;
                if (bitmap != 0)
                {
                    Marshal.Release(bitmap);
                    bitmap = 0;
                }
            }
            if (updated)
                SHChangeNotify(ItemUpdated, UnicodePath, fullPath, 0);
            return updated;
        }
        catch (COMException) { return false; }
        finally
        {
            if (bitmap != 0) Marshal.Release(bitmap);
            if (shellItem != 0) Marshal.Release(shellItem);
            if (cache is not null) Marshal.ReleaseComObject(cache);
        }
    }

    [ComImport, Guid("F676C15D-596A-4CE2-8234-33996F445DB1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailCache
    {
        [PreserveSig] int GetThumbnail(nint item, uint size, uint flags, out nint bitmap, nint cacheFlags, nint id);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindingContext, in Guid iid, out nint item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int notification, uint flags, string? path, nint unused);
}
