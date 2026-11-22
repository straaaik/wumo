using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace PixelThumbs;

public static class RenderPipeline
{
    public const int MaximumInput = 192 * 1024 * 1024;
    public static int MaximumInputFor(string extension) => extension.Equals(".stl", StringComparison.OrdinalIgnoreCase)
        ? 512 * 1024 * 1024 : MaximumInput;

    public static Bitmap? Render(Stream source, string extension, int resolution, ThumbnailStyle style,
        Action<TimeSpan, TimeSpan>? timings = null)
    {
        if (!FormatCatalog.IsSupported(extension) || !source.CanSeek || source.Length > MaximumInputFor(extension))
            return null;

        int size = Math.Clamp(resolution, 16, 512);
        style = style.Normalize();
        if (style.PreferEmbedded && RenderEmbedded(source, extension, size, style, timings) is { } embedded)
            return embedded;

        source.Position = 0;
        if (MeshRenderer.Render(source, extension, size, style, timings: timings) is { } mesh)
            return mesh;

        return !style.PreferEmbedded ? RenderEmbedded(source, extension, size, style, timings) : null;
    }

    internal static bool UsesEmbeddedImage(string extension) => extension is ".gcode" or ".f3d" or ".f3z" or
        ".fcstd" or ".sldprt" or ".sldasm";

    internal static Bitmap? RenderEmbedded(Stream source, string extension, int size, ThumbnailStyle style,
        Action<TimeSpan, TimeSpan>? timings = null)
    {
        source.Position = 0;
        long start = Stopwatch.GetTimestamp();
        var image = EmbeddedPreview.Read(source, extension, size);
        TimeSpan read = Stopwatch.GetElapsedTime(start);
        start = Stopwatch.GetTimestamp();
        var result = image is null ? null : Compose(image, size, style, applyMargin: true);
        timings?.Invoke(read, Stopwatch.GetElapsedTime(start));
        return result;
    }

    private static Bitmap Compose(Bitmap image, int size, ThumbnailStyle style, bool applyMargin)
    {
        using (image)
        {
            var result = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(style.Transparent ? Color.Transparent : StyleColor(style.BackgroundColor));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float inset = applyMargin ? size * style.Margin : 0;
            float scale = (size - 2 * inset) / Math.Max(image.Width, image.Height);
            float width = image.Width * scale;
            float height = image.Height * scale;
            graphics.DrawImage(image, (size - width) / 2, (size - height) / 2, width, height);
            return result;
        }
    }

    internal static Bitmap? RenderExternal(string input, int size, ThumbnailStyle style, bool desktopBackend)
    {
        string? executable = BundledF3d.FindExecutable();
        if (executable is null)
            return null;

        size = Math.Clamp(size, 16, 512);
        style = style.Normalize();
        string output = Path.Combine(Path.GetDirectoryName(input)!, "thumbnail.png");
        var command = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string variable in command.Environment.Keys.Where(name => name.StartsWith("F3D_", StringComparison.OrdinalIgnoreCase)).ToArray())
            command.Environment.Remove(variable);
        command.Environment["PATH"] = Environment.SystemDirectory;
        command.Environment["LP_NUM_THREADS"] = "2";
        command.ArgumentList.Add(input);
        command.ArgumentList.Add("--output=" + output);
        command.ArgumentList.Add("--no-config");
        command.ArgumentList.Add("--no-cache");
        command.ArgumentList.Add("--no-background");
        // F3D selects hardware rendering with software fallback in a desktop session.
        command.ArgumentList.Add(desktopBackend ? "--rendering-backend=auto" : "--rendering-backend=osmesa");
        command.ArgumentList.Add("--load-plugins=" + (input.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ? "assimp" : "occt"));
        command.ArgumentList.Add($"--resolution={size},{size}");
        command.ArgumentList.Add(FormattableString.Invariant($"--camera-azimuth-angle={style.Yaw}"));
        command.ArgumentList.Add(FormattableString.Invariant($"--camera-elevation-angle={style.Pitch}"));
        command.ArgumentList.Add(FormattableString.Invariant($"--camera-zoom-factor={1 - 2 * style.Margin}"));
        command.ArgumentList.Add(FormattableString.Invariant($"--light-intensity={0.4 + style.Lighting}"));
        if (!style.UseSourceColors)
        {
            Color color = StyleColor(style.ModelColor);
            command.ArgumentList.Add(string.Format(CultureInfo.InvariantCulture, "--color={0:F4},{1:F4},{2:F4}",
                color.R / 255.0, color.G / 255.0, color.B / 255.0));
        }

        using var process = Process.Start(command);
        if (process is null)
            return null;
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        process.WaitForExit();
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0 || !File.Exists(output))
            return null;
        using var image = Image.FromFile(output);
        if (image.Width != size || image.Height != size)
            return null;
        return Compose(new Bitmap(image), size, style, applyMargin: false);
    }

    private static Color StyleColor(int rgb) => Color.FromArgb(unchecked((int)0xff000000) | rgb);
}
