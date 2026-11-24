using System.ComponentModel;
using System.Runtime.InteropServices;
using static PixelThumbs.ClipboardHistory.ClipboardNative;

namespace PixelThumbs.ClipboardHistory;

internal sealed class HistoryJournal(string filename)
{
    private const uint LegacyMagic = 0x31435357; // WSC1
    private const uint HistoryMagic = 0x32435357; // WSC2
    private const uint FavoritesMagic = 0x31465357; // WSF1
    private const int RecordLimit = HistoryStore.MaxBytes + 2 * 1024 * 1024;

    internal HistoryEntry[] Load()
    {
        return LoadFile<HistoryEntry[]>(reader =>
        {
            if (reader.BaseStream.Length > RecordLimit)
                throw new InvalidDataException();
            uint magic = reader.ReadUInt32();
            if (magic is not (LegacyMagic or HistoryMagic))
                throw new InvalidDataException();
            return ReadItems(reader, Count(reader, 30), named: magic == HistoryMagic);
        }) ?? [];
    }

    internal void Save(HistoryEntry[] snapshot)
    {
        if (snapshot.Length == 0)
        {
            Delete();
            return;
        }
        bool named = snapshot.Any(entry => entry.Name is not null);
        SaveFile(named ? HistoryMagic : LegacyMagic, writer =>
        {
            WriteItems(writer, snapshot, named);
            if (writer.BaseStream.Length > RecordLimit)
                throw new InvalidDataException("История превышает лимит размера.");
        });
    }

    internal void Delete() => File.Delete(filename);

    internal HistoryFavorites LoadFavorites() => LoadFile(reader =>
    {
        if (reader.ReadUInt32() != FavoritesMagic)
            throw new InvalidDataException();
        var entries = ReadItems(reader, Count(reader, int.MaxValue), named: true);
        var folders = new List<HistoryFolder>();
        int count = Count(reader, int.MaxValue);
        for (int i = 0; i < count; i++)
        {
            var (id, name, time, entriesCount) = ReadRecord(reader, folder => (new Guid(folder.ReadBytes(16)),
                folder.ReadString(), DateTime.FromBinary(folder.ReadInt64()).ToUniversalTime(), Count(folder, int.MaxValue)));
            folders.Add(new(id, name, time, ReadItems(reader, entriesCount, named: true)));
        }
        return new HistoryFavorites(entries, [.. folders]);
    }) ?? new([], []);

    internal void SaveFavorites(HistoryFavorites snapshot) => SaveFile(FavoritesMagic, writer =>
    {
        WriteItems(writer, snapshot.Entries, named: true);
        writer.Write(snapshot.Folders.Length);
        foreach (var folder in snapshot.Folders)
        {
            WriteRecord(writer, 1024, metadata =>
            {
                metadata.Write(folder.Id.ToByteArray());
                metadata.Write(folder.Name);
                metadata.Write(folder.Time.ToBinary());
                metadata.Write(folder.Entries.Length);
            });
            foreach (var entry in folder.Entries)
                WriteEntry(writer, entry, named: true);
        }
    });

    private T? LoadFile<T>(Func<BinaryReader, T> read) where T : class
    {
        FileStream file;
        try { file = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        using (file)
        using (var reader = new BinaryReader(file))
        {
            var result = read(reader);
            if (file.Position != file.Length)
                throw new InvalidDataException();
            return result;
        }
    }

    private void SaveFile(uint magic, Action<BinaryWriter> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
        string stage = filename + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(magic);
                write(writer);
                file.Flush(flushToDisk: true);
            }
            File.Move(stage, filename, overwrite: true);
        }
        finally { File.Delete(stage); }
    }

    private static void WriteItems(BinaryWriter writer, HistoryEntry[] entries, bool named)
    {
        writer.Write(entries.Length);
        foreach (var entry in entries)
            WriteEntry(writer, entry, named);
    }

    private static void WriteEntry(BinaryWriter writer, HistoryEntry entry, bool named) =>
        WriteRecord(writer, checked((int)entry.Item.Size + 1024), item => WriteItem(item, entry, named));

    private static HistoryEntry[] ReadItems(BinaryReader reader, int count, bool named)
    {
        var entries = new List<HistoryEntry>();
        for (int i = 0; i < count; i++)
            entries.Add(ReadRecord(reader, item => ReadItem(item, named)));
        return [.. entries];
    }

    private static void WriteRecord(BinaryWriter writer, int capacity, Action<BinaryWriter> write)
    {
        using var plain = new MemoryStream(capacity);
        using var record = new BinaryWriter(plain);
        write(record);
        byte[] cipher = Protect(plain.GetBuffer(), (int)plain.Length, decrypt: false);
        writer.Write(cipher.Length);
        writer.Write(cipher);
    }

    private static T ReadRecord<T>(BinaryReader reader, Func<BinaryReader, T> read)
    {
        int length = Count(reader, RecordLimit);
        byte[] cipher = reader.ReadBytes(length);
        if (length == 0 || cipher.Length != length)
            throw new InvalidDataException();
        using var plain = new MemoryStream(Protect(cipher, length, decrypt: true), writable: false);
        using var record = new BinaryReader(plain);
        var result = read(record);
        if (plain.Position != plain.Length)
            throw new InvalidDataException();
        return result;
    }

    private static void WriteItem(BinaryWriter writer, HistoryEntry entry, bool named)
    {
        var item = entry.Item;
        writer.Write((int)item.Kind);
        writer.Write(entry.Time.ToBinary());
        writer.Write(entry.Copies);
        writer.Write(item.Key);
        writer.Write(item.Label);
        writer.Write(item.TextPreview ?? "");
        if (named)
            writer.Write(entry.Name ?? "");
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

    private static HistoryEntry ReadItem(BinaryReader reader, bool named)
    {
        var kind = (ClipboardKind)reader.ReadInt32();
        if (kind is not (ClipboardKind.Text or ClipboardKind.Image or ClipboardKind.Files or ClipboardKind.Other))
            throw new InvalidDataException();
        var time = DateTime.FromBinary(reader.ReadInt64()).ToUniversalTime();
        int copies = Math.Max(1, reader.ReadInt32());
        string key = reader.ReadString(), label = reader.ReadString(), preview = reader.ReadString();
        string name = named ? reader.ReadString() : "";
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
            image, incomplete, preview.Length > 0 ? preview : null), time, copies, name.Length > 0 ? name : null);
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
                if (result.Length < 0 || result.Length > RecordLimit)
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
