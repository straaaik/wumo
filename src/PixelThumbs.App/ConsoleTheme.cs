using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PixelThumbs;

internal static class ConsoleTheme
{
    private static readonly bool HighContrast = SystemInformation.HighContrast;
    internal static readonly Color Canvas = HighContrast ? SystemColors.Window : Color.FromArgb(0x0A, 0x0A, 0x0A);
    internal static readonly Color Panel = HighContrast ? SystemColors.Window : Color.FromArgb(0x10, 0x10, 0x10);
    internal static readonly Color Raised = HighContrast ? SystemColors.Control : Color.FromArgb(0x17, 0x17, 0x17);
    internal static readonly Color Line = HighContrast ? SystemColors.WindowText : Color.FromArgb(0x2D, 0x2C, 0x29);
    internal static readonly Color Hairline = HighContrast ? SystemColors.WindowText : Color.FromArgb(0x22, 0x21, 0x1F);
    internal static readonly Color Sand = HighContrast ? SystemColors.WindowText : Color.FromArgb(0xD2, 0xCC, 0xBB);
    internal static readonly Color Muted = HighContrast ? SystemColors.WindowText : Color.FromArgb(0xA4, 0x9F, 0x91);
    internal static readonly Color Disabled = HighContrast ? SystemColors.GrayText : Color.FromArgb(0x76, 0x72, 0x68);
    internal static readonly Color Selected = HighContrast ? SystemColors.Control : Color.FromArgb(0x2C, 0x2B, 0x28);

    private static readonly PrivateFontCollection Fonts = new();
    private static readonly Dictionary<(float Size, FontStyle Style), Font> FontCache = [];
    private static readonly FontFamily MonoFamily = LoadFamily("JetBrainsMono-Variable.ttf", "Consolas");

    internal static void ConfigureNativeControls()
    {
        if (HighContrast)
            return;
#pragma warning disable WFO5001 // WinForms exposes native dark mode as an experimental API.
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
    }

    internal static Font Mono(float size, FontStyle style = FontStyle.Regular)
    {
        var key = (size, style);
        if (!FontCache.TryGetValue(key, out var font))
        {
            font = new Font(MonoFamily, size, style);
            FontCache.Add(key, font);
        }
        return font;
    }

    private static FontFamily LoadFamily(string file, string fallback)
    {
        using var stream = typeof(ConsoleTheme).Assembly.GetManifestResourceStream($"PixelThumbs.Fonts.{file}");
        if (stream is null)
            return new FontFamily(fallback);

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        nint memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var previous = Fonts.Families.Select(family => family.Name).ToHashSet(StringComparer.Ordinal);
            Fonts.AddMemoryFont(memory, bytes.Length);
            // TextRenderer and native child controls use GDI. GDI+ retains this buffer for the app lifetime.
            AddFontMemResourceEx(memory, (uint)bytes.Length, 0, out _);
            memory = 0;
            return Fonts.Families.FirstOrDefault(candidate => !previous.Contains(candidate.Name)) ?? new FontFamily(fallback);
        }
        catch (ArgumentException)
        {
            // A font unsupported by the local graphics stack falls back to a Windows font.
        }
        finally
        {
            if (memory != 0)
                Marshal.FreeHGlobal(memory);
        }
        return new FontFamily(fallback);
    }

    internal static int Pixels(int pixels, int dpi) => (int)Math.Round(pixels * dpi / 96d);

    internal static void Frame(Graphics graphics, Rectangle bounds, Color fill, Color line, int radius)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;

        bounds.Width--;
        bounds.Height--;
        using var path = RoundedRectangle(bounds, radius);
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(line);
        graphics.FillPath(brush, path);
        graphics.DrawPath(pen, path);
        graphics.SmoothingMode = smoothing;
    }

    internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
        }
        else
        {
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
        }

        return path;
    }

    internal static void DotGrid(Graphics graphics, Rectangle clip, int dpi)
    {
        if (HighContrast)
            return;
        int step = Pixels(12, dpi);
        int dot = Pixels(1, dpi);
        using var brush = new SolidBrush(Line);
        for (int y = clip.Top / step * step + step / 2; y < clip.Bottom; y += step)
        for (int x = clip.Left / step * step + step / 2; x < clip.Right; x += step)
            graphics.FillRectangle(brush, x, y, dot, dot);
    }

    [DllImport("gdi32.dll")]
    private static extern nint AddFontMemResourceEx(nint font, uint size, nint reserved, out uint count);
}
