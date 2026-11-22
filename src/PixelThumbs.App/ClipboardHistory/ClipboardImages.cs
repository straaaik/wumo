using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static PixelThumbs.ClipboardHistory.ClipboardNative;

namespace PixelThumbs.ClipboardHistory;

internal static class ClipboardImages
{
    internal static bool IsEncoded(uint format) =>
        format == 6 || (format >= 0xc000 && FormatName(format) is "JFIF" or "GIF" or "WebP" or "image/png" or "image/jpeg" or "image/webp");

    internal static BitmapSource? Thumbnail(ClipboardPayload image) =>
        (image.Format is Dib or DibV5 ? UncompressedThumbnail(image.Bytes, 320, 180) : null) ?? Decode(image, 320, 180);

    internal static ClipboardPayload? FromBitmap(BitmapSource bitmap, int limit)
    {
        const int header = 124;
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        if (width < 1 || height < 1 || (long)width * height > (limit - header) / 4)
            return null;
        int stride = width * 4;
        var bytes = new byte[header + stride * height];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, header);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), -height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 3); // BI_BITFIELDS
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), stride * height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 0x73524742); // LCS_sRGB
        if (bitmap.Format != PixelFormats.Bgra32)
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, width, height), bytes, stride, header);
        return new ClipboardPayload(DibV5, -1, bytes);
    }

    private static BitmapSource? UncompressedThumbnail(ReadOnlySpan<byte> dib, int maxWidth, int maxHeight)
    {
        if (dib.Length < 40)
            return null;
        int header = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        int colors = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        if (width < 1 || signedHeight is 0 or int.MinValue || colors != 0)
            return null;
        bool bgr = header == 40 && depth is 24 or 32 && compression == 0;
        // The DIB V5 emitted for dropped WPF bitmaps has straight alpha and no color profile.
        bool bgra = header == 124 && dib.Length >= header && depth == 32 && compression == 3 &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]) == 0x00ff0000 &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]) == 0x0000ff00 &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]) == 0x000000ff &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]) == 0xff000000 &&
            BinaryPrimitives.ReadUInt32LittleEndian(dib[56..]) == 0x73524742 &&
            BinaryPrimitives.ReadUInt64LittleEndian(dib[112..]) == 0;
        if (!bgr && !bgra)
            return null;
        int height = Math.Abs(signedHeight);
        if ((long)width * height > HistoryStore.MaxBytes / 4)
            return null;
        int bytesPerPixel = depth / 8;
        int stride = (width * bytesPerPixel + 3) & ~3;
        if (header + (long)stride * height > dib.Length)
            return null;

        double ratio = Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        int scaledWidth = Math.Max(1, (int)(width * ratio));
        int scaledHeight = Math.Max(1, (int)(height * ratio));
        int scaledStride = scaledWidth * 4;
        var scaled = new byte[scaledStride * scaledHeight];
        int channels = bgra ? 4 : 3;
        for (int y = 0; y < scaledHeight; y++)
        {
            int row = (int)((y + .5) * height / scaledHeight);
            int sourceY = signedHeight > 0 ? height - 1 - row : row;
            var source = dib.Slice(header + sourceY * stride, stride);
            var target = scaled.AsSpan(y * scaledStride, scaledStride);
            for (int x = 0; x < scaledWidth; x++)
            {
                int sourceX = (int)((x + .5) * width / scaledWidth);
                source.Slice(sourceX * bytesPerPixel, channels).CopyTo(target[(x * 4)..]);
            }
        }
        var bitmap = BitmapSource.Create(scaledWidth, scaledHeight, 96, 96,
            bgra ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, scaled, scaledStride);
        bitmap.Freeze();
        return bitmap;
    }

    internal static byte[] EncodePng(ClipboardPayload image)
    {
        if (image.Format == Png)
            return image.Bytes;
        var bitmap = Decode(image, 0, 0) ?? throw new InvalidDataException("Изображение недоступно.");
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapSource? Decode(ClipboardPayload image, int maxWidth, int maxHeight)
    {
        try
        {
            using var input = ImageStream(image);
            var metadata = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            int width = metadata.PixelWidth;
            int height = metadata.PixelHeight;
            if (width < 1 || height < 1 || (long)width * height > HistoryStore.MaxBytes / 4)
                return null;
            input.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input;
            if (maxWidth > 0 && (width > maxWidth || height > maxHeight))
            {
                if ((long)height * maxWidth >= (long)width * maxHeight)
                    bitmap.DecodePixelHeight = maxHeight;
                else
                    bitmap.DecodePixelWidth = maxWidth;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            if (maxWidth == 0)
                return bitmap;
            // BitmapImage retains its StreamSource. Store only the scaled pixels in history.
            var thumbnail = new WriteableBitmap(bitmap);
            thumbnail.Freeze();
            return thumbnail;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or
            FormatException or IOException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static Stream ImageStream(ClipboardPayload image)
    {
        if (image.Format is not (Dib or DibV5))
            return new MemoryStream(image.Bytes, writable: false);

        ReadOnlySpan<byte> dib = image.Bytes;
        if (dib.Length < 40)
            throw new InvalidDataException();
        int header = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (header < 40 || header > dib.Length)
            throw new InvalidDataException();
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        int colors = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        if (colors < 0)
            throw new InvalidDataException();
        if (colors == 0 && depth is > 0 and <= 8)
            colors = 1 << depth;
        int masks = header == 40 ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0;
        const int bmpHeader = 14;
        int offset = checked(bmpHeader + header + masks + colors * 4);
        if (offset > bmpHeader + dib.Length)
            throw new InvalidDataException();
        var prefix = new byte[bmpHeader];
        prefix[0] = (byte)'B';
        prefix[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(2), dib.Length + bmpHeader);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(10), offset);
        return new BitmapFileStream(prefix, image.Bytes);
    }

    // WIC expects a BMP file header. Expose it ahead of the original DIB without cloning the pixels.
    private sealed class BitmapFileStream(byte[] header, byte[] pixels) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => (long)header.Length + pixels.Length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Position >= Length || buffer.IsEmpty)
                return 0;
            int used = 0;
            if (Position < header.Length)
            {
                used = Math.Min(header.Length - (int)Position, buffer.Length);
                header.AsSpan((int)Position, used).CopyTo(buffer);
                Position += used;
            }
            int count = (int)Math.Min(Length - Position, buffer.Length - used);
            if (count > 0)
            {
                pixels.AsSpan((int)Position - header.Length, count).CopyTo(buffer[used..]);
                Position += count;
            }
            return used + count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = offset + (origin switch { SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => 0 });
            if (position < 0)
                throw new IOException();
            return Position = position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
