using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Rectangle = System.Drawing.Rectangle;
using Point = System.Drawing.Point;

namespace PixelThumbs.ClipboardHistory;

internal static class PanelPlacement
{
    internal static nint Handle(Window window) => new WindowInteropHelper(window).Handle;

    internal static void Configure(Window window, bool transparentToInput = false)
    {
        nint handle = Handle(window);
        long style = GetWindowLongPtr(handle, -20).ToInt64() | 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        if (transparentToInput)
            style |= 0x20;
        SetWindowLongPtr(handle, -20, (nint)style);
    }

    internal static void EnableActivation(Window window)
    {
        nint handle = Handle(window);
        SetWindowLongPtr(handle, -20, (nint)(GetWindowLongPtr(handle, -20).ToInt64() & ~0x08000000L));
    }

    internal static nint Foreground() => GetForegroundWindow();

    internal static void ReturnFocus(Window window, nint previous)
    {
        nint current = Handle(window);
        if (previous != 0 && previous != current && Foreground() == current)
            SetForegroundWindow(previous);
    }

    internal static Point Cursor()
    {
        GetCursorPos(out var point);
        return point;
    }

    internal static Point Location(Window window)
    {
        GetWindowRect(Handle(window), out var bounds);
        return new(bounds.Left, bounds.Top);
    }

    internal static double Scale(Screen screen)
    {
        var bounds = screen.Bounds;
        nint monitor = MonitorFromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 2);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96d : 1;
    }

    internal static void Move(Window window, Point point) =>
        SetWindowPos(Handle(window), 0, point.X, point.Y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE

    internal static Point Clamp(Point point, Rectangle work, double width, double height, double scale) =>
        new(Math.Clamp(point.X, work.Left, Math.Max(work.Left, work.Right - (int)Math.Ceiling(width * scale))),
            Math.Clamp(point.Y, work.Top, Math.Max(work.Top, work.Bottom - (int)Math.Ceiling(height * scale))));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter,
        int x, int y, int width, int height, uint flags);
}
