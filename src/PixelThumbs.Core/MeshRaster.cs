using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PixelThumbs;

public static partial class MeshRenderer
{
    private const int PixelTestBudget = 48_000_000;
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.45f, 0.77f, 0.6f));

    private static Bitmap? Draw(IEnumerable<Triangle> triangles, int size, ThumbnailStyle style,
        long reading, Action<TimeSpan, TimeSpan>? timings)
    {
        size = Math.Clamp(size, 16, 512);
        const float radians = MathF.PI / 180;
        var rotation = Matrix4x4.CreateRotationY(FiniteOr(style.Yaw, 35) * radians)
            * Matrix4x4.CreateRotationX(FiniteOr(style.Pitch, 25) * radians);
        RasterBounds bounds = MeasureRaster(triangles, rotation);
        long drawing = Stopwatch.GetTimestamp();
        if (bounds.Count == 0)
        {
            timings?.Invoke(Stopwatch.GetElapsedTime(reading, drawing), TimeSpan.Zero);
            return null;
        }

        float margin = Math.Clamp(FiniteOr(style.Margin, 0.12f), 0, 0.35f);
        float coverage = 1 - margin * 2;
        int preferredSize = size <= 256 && bounds.Count <= 75_000 ? size * 2 : size;
        int rasterSize = RasterResolution(bounds, preferredSize, coverage);
        int background = style.Transparent ? 0 : unchecked((int)0xff000000) | (style.BackgroundColor & 0xffffff);
        var pixels = new int[rasterSize * rasterSize];
        var depth = new float[pixels.Length];
        Array.Fill(pixels, background);
        Array.Fill(depth, float.NegativeInfinity);

        if (bounds.Span > 1e-20f)
        {
            float pixelSpan = (rasterSize - 2) * coverage;
            var projection = new Projection(rotation, bounds.Origin, (bounds.Min + bounds.Max) * 0.5f,
                bounds.Span, Math.Max(bounds.Span, bounds.Max.Z - bounds.Min.Z), pixelSpan, rasterSize * 0.5f);
            DrawTriangles(triangles, style, projection, rasterSize, pixels, depth);
        }

        Bitmap result = ScaleRaster(pixels, rasterSize, size);
        timings?.Invoke(Stopwatch.GetElapsedTime(reading, drawing), Stopwatch.GetElapsedTime(drawing));
        return result;
    }

    private static Bitmap ScaleRaster(int[] pixels, int rasterSize, int size)
    {
        if (rasterSize == size)
            return CreateBitmap(pixels, size);
        if (rasterSize == size * 2)
            return CreateBitmap(Downsample(pixels, size), size);

        using var source = CreateBitmap(pixels, rasterSize);
        var output = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(output);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, rasterSize, rasterSize, GraphicsUnit.Pixel, attributes);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static Bitmap CreateBitmap(int[] pixels, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            for (int row = 0; row < size; row++)
                Marshal.Copy(pixels, row * size, data.Scan0 + row * data.Stride, size);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static RasterBounds MeasureRaster(IEnumerable<Triangle> triangles, Matrix4x4 rotation)
    {
        Vector3 origin = default;
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        double area = 0, edges = 0;
        int count = 0;
        foreach (Triangle triangle in triangles)
        {
            if (count++ == 0)
                origin = triangle.A;
            Vector3 a = Vector3.Transform(triangle.A - origin, rotation);
            Vector3 b = Vector3.Transform(triangle.B - origin, rotation);
            Vector3 c = Vector3.Transform(triangle.C - origin, rotation);
            Vector3 low = Vector3.Min(a, Vector3.Min(b, c));
            Vector3 high = Vector3.Max(a, Vector3.Max(b, c));
            min = Vector3.Min(min, low);
            max = Vector3.Max(max, high);
            double width = (double)high.X - low.X;
            double height = (double)high.Y - low.Y;
            area += width * height;
            edges += width + height;
        }
        return new RasterBounds(origin, min, max, Math.Max(max.X - min.X, max.Y - min.Y), area, edges, count);
    }

    private static int RasterResolution(RasterBounds bounds, int preferred, float coverage)
    {
        if (bounds.Span <= 1e-20f)
            return preferred;
        // Measure the work before drawing so late facets remain visible too.
        int low = 8, high = preferred;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            double scale = (middle - 2) * coverage / (double)bounds.Span;
            double tests = bounds.Area * scale * scale + bounds.Edges * scale + bounds.Count;
            if (tests <= PixelTestBudget)
                low = middle;
            else
                high = middle - 1;
        }
        return low;
    }

    private static void DrawTriangles(IEnumerable<Triangle> triangles, ThumbnailStyle style, Projection projection,
        int size, int[] pixels, float[] depth)
    {
        float lighting = Math.Clamp(FiniteOr(style.Lighting, 0.65f), 0, 1);
        float normalScale = (projection.Span / projection.Extent) / projection.PixelSpan;
        foreach (Triangle triangle in triangles)
        {
            Vector3 a = projection.Project(triangle.A);
            Vector3 b = projection.Project(triangle.B);
            Vector3 c = projection.Project(triangle.C);
            double area = Edge(a, b, c.X, c.Y);
            if (area == 0)
                continue;

            bool reverse = area < 0;
            Vector3 normal = Vector3.Cross(b - a, c - a);
            // Undo the screen's vertical reflection and light the face toward the camera.
            normal.Y = -normal.Y;
            normal.Z *= normalScale;
            float length = normal.Length();
            normal = length > 1e-20f ? (reverse ? normal : -normal) / length : Vector3.UnitZ;
            float brightness = 1 - lighting + lighting * (0.42f + 0.65f * Math.Max(0, Vector3.Dot(normal, Light)));
            int material = style.UseSourceColors && triangle.Color != DefaultColor ? triangle.Color : style.ModelColor;
            int color = Shade(material, brightness);

            if (reverse)
                (b, c) = (c, b);
            Rasterize(a, b, c, Math.Abs(area), color, size, pixels, depth);
        }
    }

    private static void Rasterize(Vector3 a, Vector3 b, Vector3 c, double area, int color, int size, int[] pixels, float[] depth)
    {
        int left = Math.Max(0, (int)MathF.Ceiling(Math.Min(a.X, Math.Min(b.X, c.X)) - 0.5f));
        int right = Math.Min(size - 1, (int)MathF.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) - 0.5f));
        int top = Math.Max(0, (int)MathF.Ceiling(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - 0.5f));
        int bottom = Math.Min(size - 1, (int)MathF.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - 0.5f));
        if (right < left || bottom < top)
            return;

        double dA = ((double)c.Y - b.Y) / area;
        double dB = ((double)a.Y - c.Y) / area;
        double dC = ((double)b.Y - a.Y) / area;
        const double tolerance = -1e-8;

        for (int y = top; y <= bottom; y++)
        {
            double wa = Edge(b, c, left + 0.5, y + 0.5) / area;
            double wb = Edge(c, a, left + 0.5, y + 0.5) / area;
            double wc = Edge(a, b, left + 0.5, y + 0.5) / area;
            int offset = y * size + left;
            for (int x = left; x <= right; x++, offset++, wa += dA, wb += dB, wc += dC)
            {
                if (wa < tolerance || wb < tolerance || wc < tolerance)
                    continue;

                float z = (float)(wa * a.Z + wb * b.Z + wc * c.Z);
                if (z > depth[offset])
                {
                    depth[offset] = z;
                    pixels[offset] = color;
                }
            }
        }
    }

    private static double Edge(Vector3 a, Vector3 b, double x, double y) =>
        (x - a.X) * ((double)b.Y - a.Y) - (y - a.Y) * ((double)b.X - a.X);

    private readonly record struct RasterBounds(Vector3 Origin, Vector3 Min, Vector3 Max, float Span,
        double Area, double Edges, int Count);

    private readonly record struct Projection(Matrix4x4 Rotation, Vector3 Origin, Vector3 Center, float Span,
        float Extent, float PixelSpan, float HalfSize)
    {
        internal Vector3 Project(Vector3 point)
        {
            Vector3 rotated = Vector3.Transform(point - Origin, Rotation);
            return new((rotated.X - Center.X) / Span * PixelSpan + HalfSize,
                (Center.Y - rotated.Y) / Span * PixelSpan + HalfSize, (rotated.Z - Center.Z) / Extent);
        }
    }
}
