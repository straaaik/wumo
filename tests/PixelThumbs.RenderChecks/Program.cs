using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using PixelThumbs;

var style = new ThumbnailStyle { Yaw = 0, Pitch = 0, Transparent = true, Lighting = 0 };
var stlStyle = style with { Pitch = 90 };
bool worker = args.Contains("--worker");
bool metrics = args.Contains("--metrics");
var failures = 0;
int modelArgument = Array.IndexOf(args, "--model");
if (modelArgument >= 0 && modelArgument + 1 < args.Length)
{
    string path = args[modelArgument + 1];
    Check(Path.GetFileName(path), () =>
    {
        using var input = File.OpenRead(path);
        using var image = Render(input, Path.GetExtension(path), new ThumbnailStyle());
        Require(image.Width == 512 && image.Height == 512, "Unexpected image dimensions.");
        Require(Enumerable.Range(0, 32).Any(y => Enumerable.Range(0, 32).Any(x => image.GetPixel(x * 16, y * 16).A > 0)),
            "The thumbnail contains no visible surface.");
    });
    return failures == 0 ? 0 : 1;
}

Check("OBJ concave face", () => CheckPolygon(".obj", Obj));
Check("PLY concave face", () => CheckPolygon(".ply", Ply));
Check("G-code prefers the larger tail preview", () => CheckGCode(corruptTail: false));
Check("G-code skips a corrupt tail preview", () => CheckGCode(corruptTail: true));
Check("G-code ignores markers in partial lines", CheckGCodeCutoffs);
Check("G-code without a preview", () =>
{
    using var input = Text("G28\nG1 X10 Y10\n");
    using var image = EmbeddedPreview.Read(input, ".gcode", 128);
    Require(image is null, "An image was returned without a thumbnail block.");
});
Check("STL with 165904 facets", () => CheckRing(288, 289, 20, 14));
Check("Large binary STL", () => CheckRing(840, 850, 120, 140));
Check("STL collapses only geometry-equivalent facets", CheckSimplification);
Check("STL retains visible facets under heavy overdraw", CheckOverdraw);
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        action();
        Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalMilliseconds:F0} ms)");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name} ({timer.Elapsed.TotalMilliseconds:F0} ms): {error.Message}");
    }
}

void CheckPolygon(string extension, Func<bool, string> create)
{
    using var concave = Text(create(false));
    using var triangles = Text(create(true));
    using var actual = Render(concave, extension, style);
    using var expected = Render(triangles, extension, style);
    int different = 0;
    for (int y = 0; y < actual.Height; y++)
    for (int x = 0; x < actual.Width; x++)
        if (actual.GetPixel(x, y).A != expected.GetPixel(x, y).A)
            different++;
    Require(different < 8, $"The face differs from the manual triangulation at {different} pixels.");
    Require(actual.GetPixel(256, 150).A == 0, "The concave cutout was filled.");
}

static string Obj(bool triangulated) => """
    v 3 3 0
    v 2 3 0
    v 2 1 0
    v 1 1 0
    v 1 3 0
    v 0 3 0
    v 0 0 0
    v 3 0 0

    """ + (triangulated ? "f 1 2 3\nf 1 3 8\nf 3 4 7\nf 3 7 8\nf 4 5 6\nf 4 6 7\n" : "f 1 2 3 4 5 6 7 8\n");

static string Ply(bool triangulated) => $"""
    ply
    format ascii 1.0
    element vertex 8
    property float x
    property float y
    property float z
    element face {(triangulated ? 6 : 1)}
    property list uchar int vertex_indices
    end_header
    3 3 0
    2 3 0
    2 1 0
    1 1 0
    1 3 0
    0 3 0
    0 0 0
    3 0 0

    """ + (triangulated ? "3 0 1 2\n3 0 2 7\n3 2 3 6\n3 2 6 7\n3 3 4 5\n3 3 5 6\n" : "8 0 1 2 3 4 5 6 7\n");

void CheckGCode(bool corruptTail)
{
    string head = Thumbnail(32, Color.Red);
    string tail = corruptTail ? "; thumbnail begin 512x512 4\n; ????\n; thumbnail end\n" : Thumbnail(128, Color.Blue);
    using var input = new ReadCountingStream(Encoding.ASCII.GetBytes(head + new string('\n', 3 * 1024 * 1024) + tail));
    input.Position = 3;
    using var image = worker ? RenderThroughWorker(input, ".gcode", style, 128) : EmbeddedPreview.Read(input, ".gcode", 128);
    Require(image is not null, "No valid G-code preview was found.");
    Require(worker || input.Position == 3, "The caller's stream position changed.");
    Require(input.BytesRead is > 0 and <= 2 * 1024 * 1024, $"Read {input.BytesRead} bytes.");
    Color center = image!.GetPixel(64, 64);
    Require(corruptTail ? center.R > center.B : center.B > center.R, $"Incorrect preview selected ({center}).");
}

void CheckGCodeCutoffs()
{
    const int scan = 1024 * 1024;
    string preview = Thumbnail(128, Color.Blue);
    string head = preview.TrimEnd('\n');
    using var cutHead = Text(new string('\n', scan - 1 - head.Length) + head + " invalid\n" + new string('\n', 2 * scan));
    using var cutTail = Text(new string('\n', 2 * scan) + "G1" + preview + new string('\n', scan - preview.Length));
    using var headImage = worker ? RenderThroughWorker(cutHead, ".gcode", style, 128) : EmbeddedPreview.Read(cutHead, ".gcode", 128);
    using var tailImage = worker ? RenderThroughWorker(cutTail, ".gcode", style, 128) : EmbeddedPreview.Read(cutTail, ".gcode", 128);
    Require(headImage is null && tailImage is null, "A partial G-code line was parsed as a thumbnail marker.");
}

static string Thumbnail(int size, Color color)
{
    using var bitmap = new Bitmap(size, size);
    using (var graphics = Graphics.FromImage(bitmap))
        graphics.Clear(color);
    using var png = new MemoryStream();
    bitmap.Save(png, ImageFormat.Png);
    string payload = Convert.ToBase64String(png.ToArray());
    return $"; thumbnail begin {size}x{size} {payload.Length}\n; {payload}\n; thumbnail end\n";
}

void CheckRing(int width, int height, int holeWidth, int holeHeight)
{
    int left = (width - holeWidth) / 2;
    int top = (height - holeHeight) / 2;
    int facets = 2 * (width * height - holeWidth * holeHeight);
    using var mesh = new MemoryStream(84 + 50 * facets);
    using (var file = new BinaryWriter(mesh, Encoding.ASCII, leaveOpen: true))
    {
        WriteHeader(file, facets);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (x >= left && x < left + holeWidth && y >= top && y < top + holeHeight)
                continue;
            Facet(file, x, y, x + 1, y, x + 1, y + 1);
            Facet(file, x, y, x + 1, y + 1, x, y + 1);
        }
    }

    using var image = Render(mesh, ".stl", stlStyle);
    Require(image.GetPixel(256, 256).A == 0, "The actual opening disappeared.");
    int missing = 0;
    for (int y = 115; y < 395; y += 2)
    for (int x = 115; x < 210; x += 2)
        if (image.GetPixel(x, y).A != 255)
            missing++;
    Require(missing == 0, $"A continuous surface lost {missing} sampled pixels.");
}

void CheckOverdraw()
{
    const int layers = 90_000;
    using var mesh = new MemoryStream(84 + 50 * (layers + 1));
    using (var file = new BinaryWriter(mesh, Encoding.ASCII, leaveOpen: true))
    {
        WriteHeader(file, layers + 1);
        for (int i = 0; i < layers; i++)
            Facet(file, 0, 0, 50, 0, 0, 50, i / 10000f);
        Facet(file, 70, 0, 120, 0, 120, 50);
    }
    using var image = Render(mesh, ".stl", stlStyle);
    Require(image.GetPixel(406, 288).A == 255, "The final visible facet was omitted.");
    Require(image.GetPixel(256, 288).A == 0, "The gap between the facets was filled.");
}

void CheckSimplification()
{
    const int repetitions = 500_000;
    using var input = new MemoryStream(84 + 50 * (repetitions + 1));
    using (var file = new BinaryWriter(input, Encoding.ASCII, leaveOpen: true))
    {
        WriteHeader(file, repetitions + 1);
        for (int i = 0; i < repetitions; i++)
            Facet(file, 0, 0, 50, 0, 0, 50);
        Facet(file, 50, 0, 100, 0, 0, 50);
    }
    using var reference = new MemoryStream();
    using (var file = new BinaryWriter(reference, Encoding.ASCII, leaveOpen: true))
    {
        WriteHeader(file, 1);
        Facet(file, 0, 0, 100, 0, 0, 50);
    }

    using var actual = Render(input, ".stl", stlStyle);
    using var expected = Render(reference, ".stl", stlStyle);
    for (int y = 0; y < 512; y += 4)
    for (int x = 0; x < 512; x += 4)
        Require(actual.GetPixel(x, y).A == expected.GetPixel(x, y).A, $"Mesh simplification changed the silhouette at ({x}, {y}).");
}

Bitmap Render(Stream input, string extension, ThumbnailStyle thumbStyle) =>
    (worker ? RenderThroughWorker(input, extension, thumbStyle) : MeshRenderer.Render(input, extension, 512, thumbStyle))
    ?? throw new InvalidOperationException("Rendering returned no image.");

Bitmap? RenderThroughWorker(Stream input, string extension, ThumbnailStyle style, int size = 512) =>
#if LEGACY_WORKER
    WorkerClient.Render(input, extension, size, style, null);
#else
    WorkerClient.Render(input, extension, size, style, diagnostics: metrics ? report => Console.WriteLine(
        $"{extension} {report.Failure}; startup {report.Startup.TotalMilliseconds:F1} ms, read {report.Read.TotalMilliseconds:F1} ms, render {report.Render.TotalMilliseconds:F1} ms, peak {report.PeakMemory / 1048576d:F1} MiB") : null);
#endif

static MemoryStream Text(string value) => new(Encoding.UTF8.GetBytes(value));

static void WriteHeader(BinaryWriter output, int facets)
{
    output.Write(new byte[80]);
    output.Write(facets);
}

static void Facet(BinaryWriter output, float ax, float ay, float bx, float by, float cx, float cy, float z = 0)
{
    output.Write(0L);
    output.Write(0f);
    output.Write(ax);
    output.Write(ay);
    output.Write(z);
    output.Write(bx);
    output.Write(by);
    output.Write(z);
    output.Write(cx);
    output.Write(cy);
    output.Write(z);
    output.Write((short)0);
}

static void Require(bool value, string message)
{
    if (!value)
        throw new InvalidOperationException(message);
}

sealed class ReadCountingStream(byte[] bytes) : MemoryStream(bytes)
{
    public int BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = base.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }
}
