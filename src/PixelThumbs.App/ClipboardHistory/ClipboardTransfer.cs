using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using static PixelThumbs.ClipboardHistory.ClipboardNative;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace PixelThumbs.ClipboardHistory;

internal sealed class ClipboardTransfer : ComDataObject
{
    internal const string ShelfEntryFormat = "Wumo.ClipboardShelf.Entry";
    private static readonly uint ShelfEntry = RegisterClipboardFormat(ShelfEntryFormat);
    private const int FormatError = unchecked((int)0x80040064);
    private const int MediumError = unchecked((int)0x80040069);
    private const int NotImplemented = unchecked((int)0x80004001);
    private readonly List<Offer> offers = [];

    internal ClipboardTransfer(ClipboardItem item, bool dragging, bool fromShelf = false, bool internalOnly = false)
    {
        if (fromShelf)
            offers.Add(new(ShelfEntry, -1, TYMED.TYMED_HGLOBAL, () => [1]));
        if (internalOnly)
            return;
        foreach (var part in item.Payloads)
        {
            TYMED medium = part.Format == EnhancedMetafile ? TYMED.TYMED_ENHMF : TYMED.TYMED_HGLOBAL;
            if (part.Format == Contents)
                medium |= TYMED.TYMED_ISTREAM;
            else if (part.Stream)
                medium = TYMED.TYMED_ISTREAM;
            offers.Add(new(part.Format, part.Index, medium, () => part.Bytes));
        }
        if (item.Kind == ClipboardKind.Files)
            offers.Add(new(DropEffect, -1, TYMED.TYMED_HGLOBAL, () => BitConverter.GetBytes(CopyEffect)));

        if (!dragging)
            return;

        if (item.Kind == ClipboardKind.Text && ClipboardCapture.ReadText(item.Payloads, 1) is not null)
            AddVirtualFile("Текст.txt", () => Encoding.UTF8.GetBytes(ClipboardCapture.ReadText(item.Payloads, int.MaxValue)!));
        if (item.Image is { } image)
        {
            var png = new Lazy<byte[]>(() => ClipboardImages.EncodePng(image));
            if (!offers.Exists(offer => offer.Id == Png))
                offers.Add(new(Png, -1, TYMED.TYMED_HGLOBAL, () => png.Value));
            AddVirtualFile("Изображение.png", () => png.Value);
        }
    }

    private void AddVirtualFile(string name, Func<byte[]> contents)
    {
        var descriptor = new byte[4 + 592];
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, 1);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(4), 4 | 0x4000); // FD_ATTRIBUTES | FD_PROGRESSUI
        BinaryPrimitives.WriteInt32LittleEndian(descriptor.AsSpan(4 + 36), 0x80); // FILE_ATTRIBUTE_NORMAL
        Encoding.Unicode.GetBytes(name.AsSpan(), descriptor.AsSpan(4 + 72));
        offers.Add(new(Descriptor, -1, TYMED.TYMED_HGLOBAL, () => descriptor));
        offers.Add(new(Contents, 0, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL, contents));
    }

    internal static bool Publish(nint owner, ClipboardItem item, ComDataObject transfer, out bool ole)
    {
        ole = item.Payloads.Any(part => part.Index >= 0 || part.Stream);
        if (ole)
            return OleSetClipboard(transfer) >= 0;

        var available = ((ClipboardTransfer)transfer).offers;
        var handles = new nint[available.Count];
        try
        {
            for (int i = 0; i < handles.Length; i++)
            {
                var offer = available[i];
                byte[] bytes = offer.Data();
                handles[i] = offer.Id == EnhancedMetafile ? SetEnhMetaFileBits((uint)bytes.Length, bytes) : WriteGlobal(bytes);
            }
            if (handles.All(handle => handle == 0) || !OpenClipboard(owner))
                return false;
            try
            {
                if (!EmptyClipboard())
                    return false;
                bool wroteContent = false;
                for (int i = 0; i < handles.Length; i++)
                    if (handles[i] != 0 && SetClipboardData(available[i].Id, handles[i]) != 0)
                    {
                        handles[i] = 0;
                        wroteContent |= available[i].Id != DropEffect && available[i].Id != Locale;
                    }
                return wroteContent;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            for (int i = 0; i < handles.Length; i++)
                if (handles[i] != 0)
                {
                    if (available[i].Id == EnhancedMetafile)
                        DeleteEnhMetaFile(handles[i]);
                    else
                        GlobalFree(handles[i]);
                }
        }
    }

    public int QueryGetData(ref FORMATETC format)
    {
        var offer = Find(format);
        return offer is null ? FormatError : (offer.Medium & format.tymed) == 0 ? MediumError : 0;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        var offer = Find(format);
        medium = default;
        if (offer is null || (offer.Medium & format.tymed) == 0)
            Marshal.ThrowExceptionForHR(offer is null ? FormatError : MediumError);
        try
        {
            byte[] bytes = offer!.Data();
            TYMED type = offer.Medium & format.tymed;
            if ((type & TYMED.TYMED_ISTREAM) != 0)
                medium = new() { tymed = TYMED.TYMED_ISTREAM, unionmember = SHCreateMemStream(bytes, (uint)bytes.Length) };
            else if (type == TYMED.TYMED_ENHMF)
                medium = new() { tymed = type, unionmember = SetEnhMetaFileBits((uint)bytes.Length, bytes) };
            else
                medium = new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = WriteGlobal(bytes) };
            if (medium.unionmember == 0)
                Marshal.ThrowExceptionForHR(unchecked((int)0x80004005));
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException)
        {
            throw new COMException(error.Message, unchecked((int)0x80004005));
        }
    }

    private Offer? Find(FORMATETC format)
    {
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT || format.ptd != 0)
            return null;
        uint id = (ushort)format.cfFormat;
        int index = format.lindex;
        if (id == Contents && index == -1 && !offers.Exists(offer => offer.Id == id && offer.Index > 0))
            index = 0;
        return offers.Find(offer => offer.Id == id && offer.Index == index);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => direction == DATADIR.DATADIR_GET
        ? new FormatEnumerator(offers.Select(offer => Format(offer.Id, offer.Index, offer.Medium)).ToArray())
        : throw new COMException(null, NotImplemented);
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Marshal.ThrowExceptionForHR(NotImplemented);
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = 0;
        return 0x00040130; // DATA_S_SAMEFORMATETC
    }
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
    {
        // Explorer reports the completed operation through this method. The source never deletes an item.
        if (release)
            ReleaseStgMedium(ref medium);
    }
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection)
    {
        connection = 0;
        return unchecked((int)0x80040003);
    }
    public void DUnadvise(int connection) => Marshal.ThrowExceptionForHR(NotImplemented);
    public int EnumDAdvise(out IEnumSTATDATA? enumerator)
    {
        enumerator = null;
        return unchecked((int)0x80040003);
    }

    private sealed record Offer(uint Id, int Index, TYMED Medium, Func<byte[]> Data);

    private sealed class FormatEnumerator(FORMATETC[] formats, int position = 0) : IEnumFORMATETC
    {
        public int Next(int count, FORMATETC[] destination, int[]? fetched)
        {
            int copied = Math.Min(count, formats.Length - position);
            Array.Copy(formats, position, destination, 0, copied);
            position += copied;
            if (fetched is { Length: > 0 })
                fetched[0] = copied;
            return copied == count ? 0 : 1;
        }
        public int Skip(int count)
        {
            int skipped = Math.Min(count, formats.Length - position);
            position += skipped;
            return skipped == count ? 0 : 1;
        }
        public int Reset() { position = 0; return 0; }
        public void Clone(out IEnumFORMATETC copy) => copy = new FormatEnumerator(formats, position);
    }
}
