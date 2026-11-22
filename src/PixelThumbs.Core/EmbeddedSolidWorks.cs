using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PixelThumbs;

public static partial class EmbeddedPreview
{
    private static ReadOnlySpan<byte> CompoundSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static ReadOnlySpan<byte> SolidWorksMarker => [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];
    private static readonly uint[] SolidWorksCrcTable = BuildSolidWorksCrcTable();
    private const int SolidWorksRecordHeader = 26;
    private const int SolidWorksScanLimit = 16 * 1024 * 1024;

    private static Bitmap? ReadSolidWorks(Stream source, int size)
    {
        if (source.Length is < 64 or > MaxArchiveBytes)
            return null;

        Span<byte> prefix = stackalloc byte[64];
        source.ReadExactly(prefix);
        if (prefix.StartsWith(CompoundSignature))
            return ReadCompoundSolidWorks(source, size);
        if (prefix.IndexOf(SolidWorksMarker) < 0)
            return null;

        int rotate = prefix[7] & 7;
        PreviewRecord? dib = null;
        Bitmap? png = ReadSolidWorksRange(source, 0, Math.Min(source.Length, SolidWorksScanLimit), rotate, size, ref dib);
        if (png is null && source.Length > SolidWorksScanLimit)
        {
            long tail = Math.Max(SolidWorksScanLimit - SolidWorksMarker.Length + 1, source.Length - SolidWorksScanLimit);
            png = ReadSolidWorksRange(source, tail, source.Length, rotate, size, ref dib);
        }
        return png ?? (dib is { } record && InflateSolidWorksRecord(source, record) is { } payload
            ? DecodeSolidWorksDib(payload, size)
            : null);
    }

    // Modern record framing and the stream-name rotation were documented by openswx.
    // Only the two document preview stream names are accepted here.
    private static Bitmap? ReadSolidWorksRange(Stream source, long start, long end, int rotate, int size,
        ref PreviewRecord? dib)
    {
        const int blockSize = 64 * 1024;
        byte[] block = ArrayPool<byte>.Shared.Rent(blockSize);
        Span<byte> header = stackalloc byte[SolidWorksRecordHeader];
        Span<byte> name = stackalloc byte[10];
        int candidates = 0;
        int pngAttempts = 0;
        try
        {
            while (end - start >= SolidWorksMarker.Length)
            {
                source.Position = start;
                int length = (int)Math.Min(end - start, blockSize);
                source.ReadExactly(block.AsSpan(0, length));
                ReadOnlySpan<byte> window = block.AsSpan(0, length);
                int cursor = 0;
                while (window[cursor..].IndexOf(SolidWorksMarker) is int found && found >= 0)
                {
                    long offset = start + cursor + found;
                    cursor += found + SolidWorksMarker.Length;
                    if (++candidates > 32_768)
                        return null;
                    if (offset + header.Length > source.Length)
                        break;

                    source.Position = offset;
                    source.ReadExactly(header);
                    uint nameSize = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
                    if (nameSize is not (7 or 10) || offset + header.Length + nameSize > source.Length)
                        continue;

                    uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header[10..]);
                    uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
                    uint inflated = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
                    if (compressed is < 2 or > MaxImageBytes || inflated is < 24 or > MaxImageBytes ||
                        (crc == 2 * inflated && compressed == inflated / 2))
                        continue;

                    Span<byte> decodedName = name[..(int)nameSize];
                    source.ReadExactly(decodedName);
                    if (compressed > source.Length - source.Position)
                        continue;
                    for (int index = 0; index < decodedName.Length; index++)
                    {
                        int value = decodedName[index];
                        decodedName[index] = (byte)((value << rotate) | (value >> (8 - rotate)));
                    }

                    var record = new PreviewRecord(source.Position, (int)compressed, (int)inflated, crc);
                    if (pngAttempts < 6 && decodedName.SequenceEqual("PreviewPNG"u8))
                    {
                        pngAttempts++;
                        Bitmap? result = Decode(InflateSolidWorksRecord(source, record), size);
                        if (result is not null)
                            return result;
                    }
                    else if (decodedName.SequenceEqual("Preview"u8) && dib is null)
                    {
                        dib = record;
                    }
                }

                if (length < blockSize)
                    break;
                start += length - SolidWorksMarker.Length + 1;
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    private static byte[]? InflateSolidWorksRecord(Stream source, PreviewRecord record)
    {
        try
        {
            source.Position = record.Offset;
            byte[] compressed = GC.AllocateUninitializedArray<byte>(record.Compressed);
            source.ReadExactly(compressed);
            using var input = new MemoryStream(compressed, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            byte[] result = GC.AllocateUninitializedArray<byte>(record.Length);
            deflate.ReadExactly(result);
            if (deflate.ReadByte() != -1)
                return null;

            uint crc = uint.MaxValue;
            foreach (byte value in result)
                crc = SolidWorksCrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
            return ~crc == record.Crc ? result : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private static uint[] BuildSolidWorksCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ (0xEDB88320u * (value & 1));
            table[index] = value;
        }
        return table;
    }

    private static Bitmap? ReadCompoundSolidWorks(Stream source, int size)
    {
        var bytes = new CompoundLockBytes(source);
        const uint shareExclusiveRead = 0x10;
        int result = StgOpenStorageOnILockBytes(bytes, null, shareExclusiveRead, 0, 0, out IStructuredStorage? storage);
        if (result < 0 || storage is null)
            return null;
        try
        {
            Bitmap? image = Decode(ReadStorageStream(storage, "PreviewPNG"), size);
            return image ?? (ReadStorageStream(storage, "Preview") is { } dib ? DecodeSolidWorksDib(dib, size) : null);
        }
        finally
        {
            Marshal.ReleaseComObject(storage);
        }
    }

    private static byte[]? ReadStorageStream(IStructuredStorage storage, string name)
    {
        const uint shareExclusiveRead = 0x10;
        if (storage.OpenStream(name, 0, shareExclusiveRead, 0, out IStream? stream) < 0 || stream is null)
            return null;
        try
        {
            stream.Stat(out STATSTG stat, 1);
            if (stat.cbSize is < 24 or > MaxImageBytes)
                return null;
            byte[] data = GC.AllocateUninitializedArray<byte>((int)stat.cbSize);
            nint read = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                Marshal.WriteInt32(read, 0);
                stream.Read(data, data.Length, read);
                return Marshal.ReadInt32(read) == data.Length ? data : null;
            }
            finally { Marshal.FreeCoTaskMem(read); }
        }
        finally { Marshal.ReleaseComObject(stream); }
    }

    private static Bitmap? DecodeSolidWorksDib(ReadOnlySpan<byte> input, int size)
    {
        if (input.Length < 40)
            return null;
        // Some SolidWorks streams prefix the usual BITMAPINFO header with the DIB byte count.
        if (input.Length >= 44 && BinaryPrimitives.ReadUInt32LittleEndian(input) == input.Length - 4)
            input = input[4..];
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(input);
        if (headerSize is not (40 or 108 or 124) || headerSize > input.Length)
            return null;

        int width = BinaryPrimitives.ReadInt32LittleEndian(input[4..]);
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(input[8..]);
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(input[12..]);
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(input[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(input[16..]);
        uint colors = BinaryPrimitives.ReadUInt32LittleEndian(input[32..]);
        if (width is < 1 or > 4096 || signedHeight is 0 or < -4096 or > 4096 || planes != 1 ||
            bits is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3) ||
            (compression == 3 && bits is not (16 or 32)) || colors > 256)
            return null;

        int height = Math.Abs(signedHeight);
        if ((long)width * height > 8_000_000)
            return null;
        if (colors == 0 && bits <= 8)
            colors = 1u << bits;
        int pixelStart = (int)headerSize + (int)colors * 4 + (compression == 3 && headerSize == 40 ? 12 : 0);
        int rowBytes = ((width * bits + 31) / 32) * 4;
        if ((long)rowBytes * height + pixelStart > input.Length)
            return null;

        const int fileHeaderLength = 14;
        byte[] bmp = new byte[input.Length + fileHeaderLength];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), pixelStart + fileHeaderLength);
        input.CopyTo(bmp.AsSpan(fileHeaderLength));
        return DecodeRaster(bmp, size, width, height);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int StgOpenStorageOnILockBytes([MarshalAs(UnmanagedType.Interface)] ICompoundLockBytes bytes,
        [MarshalAs(UnmanagedType.Interface)] IStructuredStorage? priority, uint mode, nint exclusions, uint reserved,
        [MarshalAs(UnmanagedType.Interface)] out IStructuredStorage? storage);

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStructuredStorage
    {
        [PreserveSig]
        int CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2,
            [MarshalAs(UnmanagedType.Interface)] out IStream? stream);
        [PreserveSig]
        int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, nint reserved1, uint mode, uint reserved2,
            [MarshalAs(UnmanagedType.Interface)] out IStream? stream);
    }

    [ComImport, Guid("0000000A-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompoundLockBytes
    {
        [PreserveSig] int ReadAt(ulong offset, nint destination, uint count, nint read);
        [PreserveSig] int WriteAt(ulong offset, nint source, uint count, nint written);
        [PreserveSig] int Flush();
        [PreserveSig] int SetSize(ulong size);
        [PreserveSig] int LockRegion(ulong offset, ulong length, uint type);
        [PreserveSig] int UnlockRegion(ulong offset, ulong length, uint type);
        [PreserveSig] int Stat(out STATSTG status, uint flags);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CompoundLockBytes(Stream stream) : ICompoundLockBytes
    {
        private const int ReadFault = unchecked((int)0x8003001E);
        private const int NoWrite = unchecked((int)0x80030005);
        private const int InvalidFunction = unchecked((int)0x80030001);
        private long totalRead;

        public int ReadAt(ulong offset, nint destination, uint count, nint read)
        {
            if (read != 0)
                Marshal.WriteInt32(read, 0);
            if (count == 0)
                return 0;
            if (offset > (ulong)stream.Length || count > MaxImageBytes || count > SolidWorksScanLimit - totalRead)
                return ReadFault;
            int available = (int)Math.Min((long)count, stream.Length - (long)offset);
            if (available == 0)
                return ReadFault;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(available);
            try
            {
                stream.Position = (long)offset;
                int length = stream.ReadAtLeast(buffer.AsSpan(0, available), available, throwOnEndOfStream: false);
                totalRead += length;
                Marshal.Copy(buffer, 0, destination, length);
                if (read != 0)
                    Marshal.WriteInt32(read, length);
                return length == count ? 0 : ReadFault;
            }
            catch (IOException) { return ReadFault; }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        public int WriteAt(ulong offset, nint source, uint count, nint written) => NoWrite;
        public int Flush() => 0;
        public int SetSize(ulong size) => NoWrite;
        public int LockRegion(ulong offset, ulong length, uint type) => InvalidFunction;
        public int UnlockRegion(ulong offset, ulong length, uint type) => InvalidFunction;

        public int Stat(out STATSTG status, uint flags)
        {
            status = new STATSTG { cbSize = stream.Length, type = 3 };
            return 0;
        }
    }

    private readonly record struct PreviewRecord(long Offset, int Compressed, int Length, uint Crc);
}

/* SolidWorks modern stream framing reference: https://github.com/schwitters/openswx
MIT License

Copyright (c) 2026 openswx contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/
