using System.ComponentModel;
using System.Runtime.InteropServices;
using static PixelThumbs.ClipboardHistory.ClipboardNative;

namespace PixelThumbs.ClipboardHistory;

internal sealed class HistoryJournal(string filename)
{
    private const uint Magic = 0x31435357; // WSC1
    private const int FileLimit = HistoryStore.MaxBytes + 2 * 1024 * 1024;

    internal HistoryEntry[] Load()
    {
        FileStream file;
        try { file = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return []; }
        using (file)
        using (var reader = new BinaryReader(file))
        {
            if (file.Length > FileLimit || reader.ReadUInt32() != Magic)
                throw new InvalidDataException();
            int count = Count(reader, 30);
            var items = new HistoryEntry[count];
            for (int i = 0; i < count; i++)
            {
                byte[] cipher = reader.ReadBytes(Count(reader, FileLimit));
                using var plain = new MemoryStream(Protect(cipher, cipher.Length, decrypt: true), writable: false);
                using var item = new BinaryReader(plain);
                items[i] = ReadItem(item);
            }
            if (file.Position != file.Length)
                throw new InvalidDataException();
            return items;
        }
    }

    internal void Save(HistoryEntry[] snapshot)
    {
        if (snapshot.Length == 0)
        {
            Delete();
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
        string stage = filename + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(Magic);
                writer.Write(snapshot.Length);
                foreach (var entry in snapshot)
                {
                    using var plain = new MemoryStream(checked((int)entry.Item.Size + 1024));
                    using var item = new BinaryWriter(plain);
                    WriteItem(item, entry);
                    byte[] cipher = Protect(plain.GetBuffer(), (int)plain.Length, decrypt: false);
                    writer.Write(cipher.Length);
                    writer.Write(cipher);
                }
                if (file.Length > FileLimit)
                    throw new InvalidDataException("История превышает лимит размера.");
                file.Flush(flushToDisk: true);
            }
            File.Move(stage, filename, overwrite: true);
        }
        finally { File.Delete(stage); }
    }

    internal void Delete() => File.Delete(filename);

    private static void WriteItem(BinaryWriter writer, HistoryEntry entry)
    {
        var item = entry.Item;
        writer.Write((int)item.Kind);
        writer.Write(entry.Time.ToBinary());
        writer.Write(entry.Copies);
        writer.Write(item.Key);
        writer.Write(item.Label);
        writer.Write(item.TextPreview ?? "");
        writer.Write(item.Incomplete);
        writer.Write(item.Paths.Length);
        foreach (string path in item.Paths)
            writer.Write(path);
        writer.Write(item.Payloads.Length);
        writer.Write(Array.IndexOf(item.Payloads, item.Image));
        foreach (var part in item.Payloads)
        {
            bool registered = part.Format >= 0xc000;
            writer.Write(registered);
            if (registered)
                writer.Write(FormatName(part.Format));
            else
                writer.Write(part.Format);
            writer.Write(part.Index);
            writer.Write(part.Stream);
            writer.Write(part.Bytes.Length);
            writer.Write(part.Bytes);
        }
    }

    private static HistoryEntry ReadItem(BinaryReader reader)
    {
        var kind = (ClipboardKind)reader.ReadInt32();
        if (kind is not (ClipboardKind.Text or ClipboardKind.Image or ClipboardKind.Files or ClipboardKind.Other))
            throw new InvalidDataException();
        var time = DateTime.FromBinary(reader.ReadInt64()).ToUniversalTime();
        int copies = Math.Max(1, reader.ReadInt32());
        string key = reader.ReadString(), label = reader.ReadString(), preview = reader.ReadString();
        bool incomplete = reader.ReadBoolean();
        var paths = new string[Count(reader, 100_000)];
        for (int i = 0; i < paths.Length; i++)
            paths[i] = reader.ReadString();
        var payloads = new ClipboardPayload[Count(reader, 1200)];
        int imageIndex = reader.ReadInt32();
        for (int i = 0; i < payloads.Length; i++)
        {
            uint format = reader.ReadBoolean() ? RegisterClipboardFormat(reader.ReadString()) : reader.ReadUInt32();
            int index = reader.ReadInt32();
            bool stream = reader.ReadBoolean();
            int length = Count(reader, HistoryStore.MaxBytes);
            byte[] bytes = reader.ReadBytes(length);
            if (format == 0 || bytes.Length != length)
                throw new InvalidDataException();
            payloads[i] = new(format, index, bytes, stream);
        }
        var image = imageIndex >= 0 && imageIndex < payloads.Length ? payloads[imageIndex] : null;
        return new(new ClipboardItem(kind, key, label, payloads, paths, image is null ? null : ClipboardImages.Thumbnail(image),
            image, incomplete, preview.Length > 0 ? preview : null), time, copies);
    }

    private static int Count(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        return count >= 0 && count <= maximum ? count : throw new InvalidDataException();
    }

    private static byte[] Protect(byte[] bytes, int length, bool decrypt)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Length = length, Bytes = handle.AddrOfPinnedObject() };
            bool success = decrypt
                ? CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out var result)
                : CryptProtectData(ref input, null, 0, 0, 0, 1, out result);
            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (result.Length < 0 || result.Length > FileLimit)
                    throw new InvalidDataException();
                var output = new byte[result.Length];
                Marshal.Copy(result.Bytes, output, 0, output.Length);
                return output;
            }
            finally { LocalFree(result.Bytes); }
        }
        finally { handle.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { internal int Length; internal nint Bytes; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint address);
}
