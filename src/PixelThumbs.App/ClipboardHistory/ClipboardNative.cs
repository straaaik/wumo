using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace PixelThumbs.ClipboardHistory;

internal static class ClipboardNative
{
    internal const uint Text = 1, Bitmap = 2, OemText = 7, Dib = 8, Unicode = 13, EnhancedMetafile = 14, DropFiles = 15, Locale = 16, DibV5 = 17;
    internal const int UpdateMessage = 0x031D;
    internal const int CopyEffect = 1;
    internal const int DragDrop = 0x00040100, DragCancel = 0x00040101, DefaultCursors = 0x00040102;
    internal static readonly uint Png = RegisterClipboardFormat("PNG");
    internal static readonly uint Html = RegisterClipboardFormat("HTML Format");
    internal static readonly uint Rtf = RegisterClipboardFormat("Rich Text Format");
    internal static readonly uint Url = RegisterClipboardFormat("UniformResourceLocatorW");
    internal static readonly uint Descriptor = RegisterClipboardFormat("FileGroupDescriptorW");
    internal static readonly uint AnsiDescriptor = RegisterClipboardFormat("FileGroupDescriptor");
    internal static readonly uint Contents = RegisterClipboardFormat("FileContents");
    internal static readonly uint DropEffect = RegisterClipboardFormat("Preferred DropEffect");

    internal static string FormatName(uint format)
    {
        if (format == EnhancedMetafile)
            return "Enhanced Metafile";
        if (format < 0xc000)
            return $"Windows #{format}";
        var buffer = new StringBuilder(256);
        return GetClipboardFormatName(format, buffer, buffer.Capacity) > 0 ? buffer.ToString() : $"Windows #{format}";
    }

    internal static byte[]? ReadGlobal(nint handle, int limit)
    {
        nuint size = handle == 0 ? 0 : GlobalSize(handle);
        if (size == 0 || size > (nuint)limit)
            return null;
        nint address = GlobalLock(handle);
        if (address == 0)
            return null;
        try
        {
            var bytes = GC.AllocateUninitializedArray<byte>((int)size);
            Marshal.Copy(address, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    internal static nint WriteGlobal(byte[] bytes)
    {
        nint handle = GlobalAlloc(0x0042, (nuint)Math.Max(bytes.Length, 1));
        if (handle == 0)
            throw new OutOfMemoryException();
        nint address = GlobalLock(handle);
        if (address == 0)
        {
            GlobalFree(handle);
            throw new OutOfMemoryException();
        }
        try { Marshal.Copy(bytes, 0, address, bytes.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }

    internal static FORMATETC Format(uint id, int index, TYMED medium) =>
        new() { cfFormat = unchecked((short)id), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };

    internal static byte[]? ReadMetafile(nint handle, int limit)
    {
        uint length = GetEnhMetaFileBits(handle, 0, null);
        if (length == 0 || length > limit)
            return null;
        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        return GetEnhMetaFileBits(handle, length, bytes) == length ? bytes : null;
    }

    [DllImport("user32.dll", SetLastError = true)] internal static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] internal static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] internal static extern nint GlobalFree(nint memory);
    [DllImport("gdi32.dll")] private static extern uint GetEnhMetaFileBits(nint handle, uint size, byte[]? data);
    [DllImport("gdi32.dll")] internal static extern nint SetEnhMetaFileBits(uint size, byte[] data);
    [DllImport("gdi32.dll")] internal static extern bool DeleteEnhMetaFile(nint handle);
    [DllImport("ole32.dll")] internal static extern int OleGetClipboard(out ComDataObject? data);
    [DllImport("ole32.dll")] internal static extern int OleSetClipboard(ComDataObject data);
    [DllImport("ole32.dll")] internal static extern int OleIsCurrentClipboard(ComDataObject data);
    [DllImport("ole32.dll")] internal static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] internal static extern void OleUninitialize();
    [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll")] internal static extern int DoDragDrop(ComDataObject data, IDropSource source, int allowed, out int effect);
    [DllImport("shlwapi.dll")] internal static extern nint SHCreateMemStream(byte[] data, uint size);

    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
        [PreserveSig] int GiveFeedback(uint effect);
    }
}
