using System.Windows;

namespace PixelThumbs.ClipboardHistory;

internal static class ShelfMotion
{
    internal static bool Horizontal(ClipboardEdge edge) => edge is ClipboardEdge.Top or ClipboardEdge.Bottom;

    internal static readonly ShelfLayout Layout = new(412, 575, 945, 320, 27, 16, 234, 142);
    internal const int CopyDuration = 650;

    internal static int Duration(bool opening) => opening ? 365 : 230;

    internal static double CopyAmplitude(double milliseconds, double from) =>
        Lerp(from, 1, Smooth(Math.Clamp(milliseconds / 140, 0, 1))) *
        Smooth(Math.Clamp((CopyDuration - milliseconds) / 225, 0, 1));

    internal static ShelfFrame Frame(double width, double height, ClipboardEdge edge, double progress, double copy = 0)
    {
        progress = Math.Clamp(progress, 0, 1);
        bool horizontal = Horizontal(edge);
        double depth = horizontal ? height : width;
        double span = horizontal ? width : height;
        double tabDepth = Math.Min(12 + 9 * copy, depth);
        double tabSpan = Math.Min(78 + 16 * copy, span);
        double depthProgress = 1 - Math.Pow(1 - progress, 3);
        double spanProgress = Smooth(Math.Clamp((progress - .08) / .92, 0, 1));
        double bulge = 38 * Math.Sin(Math.PI * progress);

        double shapeDepth = Lerp(tabDepth, depth, depthProgress);
        double shapeSpan = Lerp(tabSpan, span, spanProgress);
        double start = (span - shapeSpan) / 2;
        Rect bounds = edge switch
        {
            ClipboardEdge.Top => new(start, 0, shapeSpan, shapeDepth),
            ClipboardEdge.Bottom => new(start, height - shapeDepth, shapeSpan, shapeDepth),
            ClipboardEdge.Left => new(0, start, shapeDepth, shapeSpan),
            _ => new(width - shapeDepth, start, shapeDepth, shapeSpan)
        };
        double radius = Math.Min(Math.Min(shapeDepth, shapeSpan) / 2, Lerp(5 + 4 * copy, Layout.Radius, progress) + bulge);
        double opacity = Smooth(Math.Clamp((progress - .52) / .41, 0, 1));
        return new(bounds, radius, opacity);
    }

    internal static double Lerp(double start, double end, double progress) => start + (end - start) * progress;
    private static double Smooth(double progress) => progress * progress * (3 - 2 * progress);
}

internal readonly record struct ShelfLayout(double SideWidth, double SideHeight, double HorizontalWidth,
    double HorizontalHeight, double Radius, double Padding, double CardWidth, double PreviewHeight);
internal readonly record struct ShelfFrame(Rect Bounds, double Radius, double Opacity);
