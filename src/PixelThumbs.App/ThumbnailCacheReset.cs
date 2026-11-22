using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace PixelThumbs;

internal static class ThumbnailCacheReset
{
    private const string CategoryName = "Thumbnail Cache";
    private const string Category = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\" + CategoryName;
    private const string Handler = "{889900C3-59F3-4C2F-AE21-A409EA01E605}";

    internal static void Clear() => Run(purge: true);

    internal static ulong Run(bool purge)
    {
        using var category = Registry.LocalMachine.OpenSubKey(Category);
        using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{Handler}\InprocServer32");
        string systemServer = Path.Combine(Environment.SystemDirectory, "thumbcache.dll");
        if (!string.Equals(category?.GetValue(null) as string, Handler, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(server?.GetValue(null) as string, systemServer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Системный обработчик категории «Эскизы» недоступен.");

        var type = Type.GetTypeFromCLSID(Guid.Parse(Handler), throwOnError: true)!;
        object instance = Activator.CreateInstance(type)!;
        var callback = new CleanupCallback();
        try
        {
            var cleanup = (IEmptyVolumeCache)instance;
            uint flags = 0;
            nint title = 0, description = 0, button = 0;
            int initialized;
            try
            {
                string volume = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                    ?? throw new InvalidOperationException("Папка данных текущего пользователя недоступна.");
                nint key = category!.Handle.DangerousGetHandle();
                initialized = instance is IEmptyVolumeCache2 extended
                    ? extended.InitializeEx(key, volume, CategoryName, out title, out description, out button, ref flags)
                    : cleanup.Initialize(key, volume, out title, out description, ref flags);
                if (initialized != 1)
                    Check(initialized);
            }
            finally
            {
                Marshal.FreeCoTaskMem(title);
                Marshal.FreeCoTaskMem(description);
                Marshal.FreeCoTaskMem(button);
            }

            try
            {
                if (initialized == 1) // S_FALSE: no files to delete.
                    return 0;
                Check(cleanup.GetSpaceUsed(out ulong bytes, callback));
                if (purge)
                    Check(cleanup.Purge(ulong.MaxValue, callback));
                return bytes;
            }
            finally { cleanup.Deactivate(out _); }
        }
        finally { Marshal.ReleaseComObject(instance); }
    }

    private static void Check(int result)
    {
        if (result != 0)
            throw new InvalidOperationException($"Очистка эскизов Windows вернула код 0x{result:X8}.");
    }

    [ComImport, Guid("8FCE5227-04DA-11D1-A004-00805F8ABE06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCache
    {
        [PreserveSig] int Initialize(nint key, [MarshalAs(UnmanagedType.LPWStr)] string volume,
            out nint displayName, out nint description, ref uint flags);
        [PreserveSig] int GetSpaceUsed(out ulong bytes, ICleanupCallback callback);
        [PreserveSig] int Purge(ulong bytes, ICleanupCallback callback);
        [PreserveSig] int ShowProperties(nint window);
        [PreserveSig] int Deactivate(out uint flags);
    }

    [ComImport, Guid("02B7E3BA-4DB3-11D2-B2D9-00C04F8EEC8C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCache2
    {
        [PreserveSig] int Initialize(nint key, nint volume, nint title, nint description, nint flags);
        [PreserveSig] int GetSpaceUsed(nint bytes, nint callback);
        [PreserveSig] int Purge(ulong bytes, nint callback);
        [PreserveSig] int ShowProperties(nint window);
        [PreserveSig] int Deactivate(nint flags);
        [PreserveSig] int InitializeEx(nint key, [MarshalAs(UnmanagedType.LPWStr)] string volume,
            [MarshalAs(UnmanagedType.LPWStr)] string keyName, out nint displayName, out nint description,
            out nint buttonName, ref uint flags);
    }

    [ComVisible(true), Guid("6E793361-73C6-11D0-8469-00AA00442901"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICleanupCallback
    {
        [PreserveSig] int ScanProgress(ulong bytes, uint flags, nint status);
        [PreserveSig] int PurgeProgress(ulong freed, ulong requested, uint flags, nint status);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CleanupCallback : ICleanupCallback
    {
        public int ScanProgress(ulong bytes, uint flags, nint status) => 0;
        public int PurgeProgress(ulong freed, ulong requested, uint flags, nint status) => 0;
    }
}
