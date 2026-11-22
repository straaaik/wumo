using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace PixelThumbs;

public static partial class MeshRenderer
{
    private const int StlHeaderSize = 84;
    private const int StlFacetSize = 50;
    private const int StlSimplificationThreshold = 500_000;

    private static bool ReadBinaryStlHeader(Stream source, out BinaryStl header)
    {
        header = default;
        if (source.Length < StlHeaderSize)
            return false;

        Span<byte> bytes = stackalloc byte[StlHeaderSize];
        source.ReadExactly(bytes);
        source.Position = 0;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[80..]);
        long expectedLength = StlHeaderSize + (long)StlFacetSize * count;
        if (count == 0 || expectedLength > source.Length ||
            (expectedLength != source.Length && bytes[..5].SequenceEqual("solid"u8)))
            return false;

        int marker = bytes[..80].IndexOf("COLOR="u8);
        bool magics = marker >= 0 && marker + 10 <= 80;
        int baseColor = magics ? Rgb(bytes[marker + 6], bytes[marker + 7], bytes[marker + 8]) : DefaultColor;
        header = new BinaryStl(count, magics, baseColor);
        return true;
    }

    private static IEnumerable<Triangle> ReadBinaryStl(Stream source, BinaryStl header)
    {
        const int facetsPerBlock = 4096;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(facetsPerBlock * StlFacetSize);
        try
        {
            source.Position = StlHeaderSize;
            uint remaining = header.Count;
            bool simplify = remaining >= StlSimplificationThreshold;
            Triangle? pending = null;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, facetsPerBlock);
                source.ReadExactly(buffer.AsSpan(0, count * StlFacetSize));
                remaining -= (uint)count;
                for (int i = 0; i < count; i++)
                {
                    Triangle triangle = ReadStlFacet(buffer.AsSpan(i * StlFacetSize, StlFacetSize), header);
                    if (!IsValidTriangle(triangle.A, triangle.B, triangle.C))
                        continue;

                    if (pending is { } previous)
                    {
                        if (previous == triangle)
                            continue;
                        if (simplify && CollapseStlEdge(previous, triangle, out Triangle combined))
                        {
                            pending = combined;
                            continue;
                        }
                        yield return previous;
                    }
                    pending = triangle;
                }
            }
            if (pending is { } last)
                yield return last;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Triangle ReadStlFacet(ReadOnlySpan<byte> facet, BinaryStl header)
    {
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(facet[48..]);
        bool attributeBit = (flags & 0x8000) != 0;
        int color = header.BaseColor;
        if (attributeBit != header.Magics)
        {
            int low = (flags & 31) * 255 / 31;
            int mid = ((flags >> 5) & 31) * 255 / 31;
            int high = ((flags >> 10) & 31) * 255 / 31;
            color = header.Magics ? Rgb(low, mid, high) : Rgb(high, mid, low);
        }
        return new Triangle(ZUp(ReadVector(facet[12..])), ZUp(ReadVector(facet[24..])), ZUp(ReadVector(facet[36..])), color);
    }

    // Removing a collinear vertex between opposite corners preserves the exact area of both facets.
    // Matching a shared edge and color keeps creases, boundary loops and color transitions intact.
    private static bool CollapseStlEdge(Triangle first, Triangle second, out Triangle result)
    {
        result = default;
        if (first.Color != second.Color)
            return false;

        bool a = ContainsVertex(second, first.A);
        bool b = ContainsVertex(second, first.B);
        bool c = ContainsVertex(second, first.C);
        if ((a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) != 2)
            return false;

        Vector3 firstTip = !a ? first.A : !b ? first.B : first.C;
        Vector3 secondTip = !ContainsVertex(first, second.A) ? second.A :
            !ContainsVertex(first, second.B) ? second.B : second.C;
        Vector3 edgeA = a ? first.A : first.B;
        Vector3 edgeB = c ? first.C : first.B;
        if (OnOpenSegment(edgeA, firstTip, secondTip))
            result = new Triangle(firstTip, secondTip, edgeB, first.Color);
        else if (OnOpenSegment(edgeB, firstTip, secondTip))
            result = new Triangle(firstTip, edgeA, secondTip, first.Color);
        else
            return false;
        return true;
    }

    private static bool ContainsVertex(Triangle triangle, Vector3 point) =>
        triangle.A == point || triangle.B == point || triangle.C == point;

    private static bool OnOpenSegment(Vector3 middle, Vector3 start, Vector3 end)
    {
        double ax = (double)middle.X - start.X, ay = (double)middle.Y - start.Y, az = (double)middle.Z - start.Z;
        double bx = (double)end.X - start.X, by = (double)end.Y - start.Y, bz = (double)end.Z - start.Z;
        if (ay * bz != az * by || az * bx != ax * bz || ax * by != ay * bx)
            return false;

        double dot = ax * bx + ay * by + az * bz;
        return dot > 0 && dot < bx * bx + by * by + bz * bz;
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));

    private readonly record struct BinaryStl(uint Count, bool Magics, int BaseColor);
}
