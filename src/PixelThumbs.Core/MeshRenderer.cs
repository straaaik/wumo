using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Xml;

namespace PixelThumbs;

public static partial class MeshRenderer
{
    private const int MaxInputBytes = 128 * 1024 * 1024;
    private const int MaxTriangles = 1_000_000;
    private const int MaxVertices = 2_000_000;
    private const int DefaultColor = -1;

    public static Bitmap? Render(Stream source, string extension, int size, ThumbnailStyle style,
        Action<TimeSpan, TimeSpan>? timings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(style);

        string format = extension.TrimStart('.').ToLowerInvariant();
        if (!SupportsMesh(format))
            return null;

        try
        {
            if (source.CanSeek && source.Position != 0)
                source.Seek(0, SeekOrigin.Begin);

            long reading = Stopwatch.GetTimestamp();
            using MemoryStream? copy = source.CanSeek ? null : ReadBounded(source);
            Stream input = copy ?? source;
            return ReadTriangles(input, format) is { } triangles ? Draw(triangles, size, style, reading, timings) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
            or XmlException or System.Text.Json.JsonException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    internal static bool SupportsMesh(string format) => format is "stl" or "obj" or "3mf" or "ply" or "glb" or "gltf";

    internal static Func<int, ThumbnailStyle, Bitmap?>? PreparePreview(Stream input, string extension)
    {
        input.Position = 0;
        if (ReadTriangles(input, extension.TrimStart('.')) is not { } triangles)
            return null;

        // Small binary STLs benefit from a single parse. Retain streaming for large files.
        if (triangles is not List<Triangle> && input.Length <= 12 * 1024 * 1024)
            triangles = triangles.ToArray();
        return (size, style) => Draw(triangles, size, style, Stopwatch.GetTimestamp(), null);
    }

    private static IEnumerable<Triangle>? ReadTriangles(Stream input, string format)
    {
        if (input.Length <= 0)
            return null;
        if (format == "stl" && input.Length <= RenderPipeline.MaximumInputFor(".stl") &&
            ReadBinaryStlHeader(input, out BinaryStl header))
            return ReadBinaryStl(input, header);
        if (input.Length > MaxInputBytes)
            return null;

        var mesh = format switch
        {
            "stl" => ReadStl(input),
            "obj" => ReadObj(input),
            "3mf" => Read3mf(input),
            "ply" => ReadPly(input),
            _ => ReadGltf(input, format == "glb")
        };
        return mesh.Triangles;
    }

    public static Bitmap RenderSample(int size, ThumbnailStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var mesh = new Mesh();
        // A simple stepped part, with open slots that make color and lighting changes easy to see.
        AddBox(mesh, new(-1.25f, 0, -0.85f), new(1.25f, 0.25f, 0.85f));
        AddBox(mesh, new(-1.05f, 0.25f, -0.68f), new(-0.66f, 0.82f, 0.65f));
        AddBox(mesh, new(0.65f, 0.25f, -0.68f), new(1.05f, 0.82f, 0.65f));
        AddBox(mesh, new(-0.65f, 0.25f, -0.68f), new(0.65f, 1.06f, -0.22f));
        AddBox(mesh, new(-0.43f, 0.43f, -0.55f), new(0.43f, 0.65f, 0.51f));
        AddBox(mesh, new(-0.81f, 0.82f, -0.66f), new(-0.66f, 1.08f, 0.18f));
        AddBox(mesh, new(0.65f, 0.82f, -0.66f), new(0.81f, 1.08f, 0.18f));
        AddBox(mesh, new(-0.42f, 1.06f, -0.61f), new(0.42f, 1.33f, -0.31f));
        return Draw(mesh.Triangles, size, style, Stopwatch.GetTimestamp(), null)!;
    }

    private static int Shade(int rgb, float brightness)
    {
        int r = Math.Clamp((int)(((rgb >> 16) & 255) * brightness + 0.5f), 0, 255);
        int g = Math.Clamp((int)(((rgb >> 8) & 255) * brightness + 0.5f), 0, 255);
        int b = Math.Clamp((int)((rgb & 255) * brightness + 0.5f), 0, 255);
        return unchecked((int)0xff000000) | (r << 16) | (g << 8) | b;
    }

    private static int[] Downsample(int[] pixels, int size)
    {
        var result = new int[size * size];
        int stride = size * 2;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int offset = (y * stride + x) * 2;
                int a = pixels[offset];
                int b = pixels[offset + 1];
                int c = pixels[offset + stride];
                int d = pixels[offset + stride + 1];
                int rb = ((a & 0xff00ff) + (b & 0xff00ff) + (c & 0xff00ff) + (d & 0xff00ff) + 0x020002) >> 2;
                int ag = (((a >>> 8) & 0xff00ff) + ((b >>> 8) & 0xff00ff) + ((c >>> 8) & 0xff00ff)
                    + ((d >>> 8) & 0xff00ff) + 0x020002) >> 2;
                result[y * size + x] = (rb & 0xff00ff) | ((ag & 0xff00ff) << 8);
            }
        }

        return result;
    }

    private static void AddBox(Mesh mesh, Vector3 min, Vector3 max)
    {
        Vector3[] points =
        [
            min, new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), max, new(min.X, max.Y, max.Z)
        ];
        ReadOnlySpan<int> faces = [0, 3, 2, 1, 4, 5, 6, 7, 0, 4, 7, 3, 1, 2, 6, 5, 0, 1, 5, 4, 3, 7, 6, 2];
        for (int i = 0; i < faces.Length; i += 4)
        {
            mesh.Add(points[faces[i]], points[faces[i + 1]], points[faces[i + 2]]);
            mesh.Add(points[faces[i]], points[faces[i + 2]], points[faces[i + 3]]);
        }
    }

    private static MemoryStream ReadBounded(Stream source)
    {
        var copy = new MemoryStream();
        try
        {
            byte[] buffer = new byte[65536];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (copy.Length + count > MaxInputBytes)
                    throw new InvalidDataException("Mesh input exceeds the thumbnail size limit.");
                copy.Write(buffer, 0, count);
            }

            copy.Position = 0;
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    private static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    private static bool TryFloat(ReadOnlySpan<char> token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static bool TryInt(ReadOnlySpan<char> token, out int value) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static ReadOnlySpan<char> TakeToken(ref ReadOnlySpan<char> line)
    {
        line = line.TrimStart();
        int end = 0;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
            end++;
        ReadOnlySpan<char> token = line[..end];
        line = line[end..];
        return token;
    }

    private static Vector3 ZUp(Vector3 point) => new(point.X, point.Z, -point.Y);

    private static bool IsValid(Vector3 point) =>
        MathF.Abs(point.X) <= 1e20f && MathF.Abs(point.Y) <= 1e20f && MathF.Abs(point.Z) <= 1e20f;

    private static bool IsValidTriangle(Vector3 a, Vector3 b, Vector3 c) =>
        IsValid(a) && IsValid(b) && IsValid(c) && a != b && b != c && c != a;

    private static int Rgb(float red, float green, float blue) =>
        (ToByte(red) << 16) | (ToByte(green) << 8) | ToByte(blue);

    private static int ToByte(float channel) => (int)(Math.Clamp(channel, 0, 255) + 0.5f);

    private static int AverageColors(int a, int b, int c)
    {
        if (a == DefaultColor || b == DefaultColor || c == DefaultColor)
            return a != DefaultColor ? a : b != DefaultColor ? b : c;

        int r = (((a >> 16) & 255) + ((b >> 16) & 255) + ((c >> 16) & 255)) / 3;
        int g = (((a >> 8) & 255) + ((b >> 8) & 255) + ((c >> 8) & 255)) / 3;
        int blue = ((a & 255) + (b & 255) + (c & 255)) / 3;
        return (r << 16) | (g << 8) | blue;
    }

    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Color);

    private readonly record struct Vertex(Vector3 Position, int Color = DefaultColor);

    private sealed class Mesh
    {
        internal List<Triangle> Triangles { get; } = [];
        internal void Add(Vector3 a, Vector3 b, Vector3 c, int color = DefaultColor)
        {
            if (!IsValidTriangle(a, b, c))
                return;
            if (Triangles.Count >= MaxTriangles)
                throw new InvalidDataException("Mesh exceeds the triangle count limit.");

            Triangles.Add(new Triangle(a, b, c, color));
        }

        internal void Add(Vertex a, Vertex b, Vertex c) => Add(a.Position, b.Position, c.Position,
            AverageColors(a.Color, b.Color, c.Color));
    }
}
