using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace PixelThumbs;

public static partial class EmbeddedPreview
{
    private static readonly Lazy<Zstandard?> Decoder = new(LoadZstandard);

    private static byte[]? ReadZstandardEntry(Stream source, ZipArchiveEntry entry, int limit)
    {
        if (Decoder.Value is not { } decoder || entry.CompressedLength > limit + 1_048_576 ||
            !TryGetBoundedDirectory(source, out ulong offset, out ulong length, out ulong count))
            return null;

        long directoryEnd = (long)(offset + length);
        source.Position = (long)offset;
        Span<byte> header = stackalloc byte[46];
        for (ulong index = 0; index < count && source.Position + header.Length <= directoryEnd; index++)
        {
            source.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50)
                return null;

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            long next = source.Position + nameLength + extraLength + commentLength;
            if (next > directoryEnd)
                return null;

            ushort compression = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            if (compression is 93 or 20 && (flags & 1) == 0 && nameLength is > 0 and <= 4096 &&
                (uncompressed == uint.MaxValue || uncompressed == entry.Length) &&
                (compressed == uint.MaxValue || compressed == entry.CompressedLength))
            {
                byte[] name = new byte[nameLength];
                source.ReadExactly(name);
                if (Encoding.UTF8.GetString(name).Equals(entry.FullName, StringComparison.Ordinal))
                {
                    ulong localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
                    if (localOffset == uint.MaxValue || uncompressed == uint.MaxValue || compressed == uint.MaxValue)
                    {
                        byte[] extra = new byte[extraLength];
                        source.ReadExactly(extra);
                        if (!ReadZip64Entry(extra, entry, uncompressed, compressed, ref localOffset))
                            return null;
                    }
                    return ReadZstandardPayload(source, entry, name, compression, decoder, localOffset, offset);
                }
            }
            source.Position = next;
        }
        return null;
    }

    private static bool ReadZip64Entry(ReadOnlySpan<byte> extra, ZipArchiveEntry entry, uint uncompressed,
        uint compressed, ref ulong localOffset)
    {
        while (extra.Length >= 4)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (size > extra.Length - 4)
                return false;
            if (type != 1)
            {
                extra = extra[(size + 4)..];
                continue;
            }

            ReadOnlySpan<byte> fields = extra.Slice(4, size);
            if (uncompressed == uint.MaxValue)
            {
                if (fields.Length < 8 || BinaryPrimitives.ReadUInt64LittleEndian(fields) != (ulong)entry.Length)
                    return false;
                fields = fields[8..];
            }
            if (compressed == uint.MaxValue)
            {
                if (fields.Length < 8 || BinaryPrimitives.ReadUInt64LittleEndian(fields) != (ulong)entry.CompressedLength)
                    return false;
                fields = fields[8..];
            }
            if (localOffset == uint.MaxValue)
            {
                if (fields.Length < 8)
                    return false;
                localOffset = BinaryPrimitives.ReadUInt64LittleEndian(fields);
            }
            return true;
        }
        return false;
    }

    private static byte[]? ReadZstandardPayload(Stream source, ZipArchiveEntry entry, byte[] expectedName,
        ushort compression, Zstandard decoder, ulong offset, ulong directory)
    {
        Span<byte> header = stackalloc byte[30];
        if (offset >= directory || (ulong)header.Length > directory - offset)
            return null;

        source.Position = (long)offset;
        source.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034B50 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) & 1) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != compression ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) != expectedName.Length)
            return null;

        ulong payload = offset + (ulong)header.Length + (ulong)expectedName.Length +
            BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        if (payload > directory || (ulong)entry.CompressedLength > directory - payload)
            return null;

        byte[] localName = new byte[expectedName.Length];
        source.ReadExactly(localName);
        if (!localName.AsSpan().SequenceEqual(expectedName))
            return null;

        source.Position = (long)payload;
        byte[] compressed = GC.AllocateUninitializedArray<byte>((int)entry.CompressedLength);
        source.ReadExactly(compressed);
        byte[] result = GC.AllocateUninitializedArray<byte>((int)entry.Length);
        nuint written = decoder.Decompress(result, (nuint)result.Length, compressed, (nuint)compressed.Length);
        return written == (nuint)result.Length ? result : null;
    }

    private static Zstandard? LoadZstandard()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "zstd.dll");
        if (!NativeLibrary.TryLoad(path, out IntPtr library))
            return null;
        if (NativeLibrary.TryGetExport(library, "ZSTD_decompress", out IntPtr decompress))
            return new Zstandard(Marshal.GetDelegateForFunctionPointer<ZstandardDecompress>(decompress));
        NativeLibrary.Free(library);
        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint ZstandardDecompress([Out] byte[] destination, nuint capacity, byte[] source, nuint size);

    private sealed record Zstandard(ZstandardDecompress Decompress);
}
