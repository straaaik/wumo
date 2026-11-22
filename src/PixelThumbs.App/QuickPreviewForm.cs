using System.Drawing.Drawing2D;
using static PixelThumbs.ConsoleTheme;

namespace PixelThumbs;

internal sealed class QuickPreviewForm : Form
{
    private Bitmap? picture;
    private readonly int requestedSize;
    private readonly Label details = new()
    {
        BackColor = Canvas, ForeColor = Sand, Font = Mono(9), UseMnemonic = false,
        TextAlign = ContentAlignment.TopLeft
    };

    internal QuickPreviewForm(int size, Point pointer, string fileName)
    {
        requestedSize = size;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Canvas;
        Text = fileName;
        details.Text = "Готовим миниатюру…";
        Controls.Add(details);
        PlaceOnMonitor(pointer);
    }

    internal void ShowDetails(string properties, string reason) =>
        details.Text = string.IsNullOrEmpty(properties) ? reason : $"{reason}\r\n\r\n{properties}";

    internal void ShowImage(Bitmap image)
    {
        picture?.Dispose();
        picture = image;
        details.Hide();
        Invalidate();
    }

    internal void PlaceOnMonitor(Point pointer)
    {
        Rectangle area = Screen.FromPoint(pointer).WorkingArea;
        int size = Math.Min(requestedSize, Math.Min(area.Height, area.Width) * 9 / 10);
        var centered = new Rectangle(area.Left + (area.Width - size) / 2, area.Top + (area.Height - size) / 2, size, size);
        if (!centered.Contains(pointer))
        {
            Bounds = centered;
            return;
        }

        // Keep the hovered shell item exposed to hit testing, including when the pointer moves.
        int x = pointer.X < centered.Left + size / 2 ? area.Right - size : area.Left;
        int y = pointer.Y < centered.Top + size / 2 ? area.Bottom - size : area.Top;
        Bounds = new Rectangle(x, y, size, size);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            const int noActivate = 0x08000000, toolWindow = 0x80, layered = 0x80000, transparent = 0x20;
            parameters.ExStyle |= noActivate | toolWindow | layered | transparent;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // An opaque layered window with WS_EX_TRANSPARENT passes mouse input to the windows beneath it.
        SetLayeredWindowAttributes(Handle, 0, 255, 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Frame(e.Graphics, ClientRectangle, ConsoleTheme.Panel, Line, Pixels(4, DeviceDpi));
        int inset = Pixels(10, DeviceDpi);
        int heading = Pixels(24, DeviceDpi);
        int caption = Pixels(27, DeviceDpi);
        var stage = StageBounds;
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, "PIXELTHUMBS // БЫСТРЫЙ ПРОСМОТР", Mono(8, FontStyle.Bold),
            new Rectangle(inset, 1, stage.Width, heading), Muted, flags);
        TextRenderer.DrawText(e.Graphics, Text, Mono(9),
            new Rectangle(inset, stage.Bottom, stage.Width, caption), Sand, flags);
        if (stage.Width <= 2 || stage.Height <= 2)
            return;
        Frame(e.Graphics, stage, Canvas, Hairline, Pixels(4, DeviceDpi));
        if (picture is null)
            return;
        DotGrid(e.Graphics, Rectangle.Inflate(stage, -2, -2), DeviceDpi);
        float scale = Math.Min(stage.Width / (float)picture.Width, stage.Height / (float)picture.Height);
        var width = (int)(picture.Width * scale);
        var height = (int)(picture.Height * scale);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(picture, new Rectangle(stage.Left + (stage.Width - width) / 2,
            stage.Top + (stage.Height - height) / 2, width, height));
    }

    private Rectangle StageBounds => Rectangle.FromLTRB(Pixels(10, DeviceDpi), Pixels(24, DeviceDpi) + 3,
        Width - Pixels(10, DeviceDpi), Height - Pixels(27, DeviceDpi));

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int padding = Pixels(9, DeviceDpi);
        var bounds = Rectangle.Inflate(StageBounds, -padding, -padding);
        if (details is not null && bounds.Width > 0 && bounds.Height > 0)
            details.Bounds = bounds;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            picture?.Dispose();
        base.Dispose(disposing);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}
