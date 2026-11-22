using System.Buffers;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

[assembly: ComVisible(false)]

namespace PixelThumbs;

[ComImport, ComVisible(true), Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.Interface)] IStream stream, uint mode);
}

[ComImport, ComVisible(true), Guid("E357FCCD-A995-4576-B01F-234630154E96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    [PreserveSig] int GetThumbnail(uint size, out nint bitmap, out uint alphaType);
}

public abstract class ThumbnailProvider(string extension) : IInitializeWithStream, IThumbnailProvider
{
    private const int Failure = unchecked((int)0x80004005);
    private IStream? input;

    public int Initialize(IStream stream, uint mode)
    {
        if (input is not null)
            return unchecked((int)0x800704DF);
        input = stream;
        return stream is not null ? 0 : unchecked((int)0x80070057);
    }

    public int GetThumbnail(uint size, out nint bitmap, out uint alphaType)
    {
        bitmap = 0;
        alphaType = 0;
        if (input is null || size == 0)
            return Failure;

        try
        {
            var settings = SettingsStore.Load();
            var options = settings.Formats[extension];
            if (!settings.ThumbnailsWidgetEnabled || !options.Enabled)
                return Failure;

            using var stream = new ShellStream(input);
            using var image = WorkerClient.Render(stream, extension, (int)Math.Min(size, 512),
                options.Style ?? settings.DefaultStyle);
            if (image is null)
                return Failure;

            bitmap = ThumbnailBitmap.Create(image);
            if (bitmap == 0)
                return Failure;
            alphaType = 2; // WTSAT_ARGB
            return 0;
        }
        catch (Exception)
        {
            return Failure;
        }
    }
}

internal sealed class ShellStream(IStream stream) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length
    {
        get { stream.Stat(out var info, 1); return info.cbSize; }
    }

    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }

    public override unsafe int Read(byte[] buffer, int offset, int count)
    {
        int read = 0;
        if (offset == 0)
        {
            stream.Read(buffer, count, (nint)(&read));
            return read;
        }

        byte[] temporary = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            stream.Read(temporary, count, (nint)(&read));
            temporary.AsSpan(0, read).CopyTo(buffer.AsSpan(offset, read));
            return read;
        }
        finally { ArrayPool<byte>.Shared.Return(temporary); }
    }

    public override unsafe long Seek(long offset, SeekOrigin origin)
    {
        long position;
        stream.Seek(offset, (int)origin, (nint)(&position));
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class ThumbnailBitmap
{
    internal static unsafe nint Create(Bitmap source)
    {
        using var normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        var dimensions = new BitmapInfo
        {
            HeaderSize = 40, Width = normalized.Width, Height = -normalized.Height,
            Planes = 1, Bits = 32
        };
        var data = normalized.LockBits(new Rectangle(0, 0, normalized.Width, normalized.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        nint bitmap = 0;
        try
        {
            bitmap = CreateDIBSection(0, ref dimensions, 0, out nint pixels, 0, 0);
            if (bitmap == 0)
                return 0;
            int stride = normalized.Width * 4;
            for (int row = 0; row < normalized.Height; row++)
                Buffer.MemoryCopy((void*)(data.Scan0 + row * data.Stride), (void*)(pixels + row * stride), stride, stride);
            return bitmap;
        }
        catch
        {
            if (bitmap != 0)
                DeleteObject(bitmap);
            throw;
        }
        finally { normalized.UnlockBits(data); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage,
        out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
