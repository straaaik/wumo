using System.ComponentModel;
using System.Drawing.Drawing2D;
using static PixelThumbs.ConsoleTheme;

namespace PixelThumbs;

internal sealed class TrayMenu : ContextMenuStrip
{
    private readonly ToolStripMenuItem openItem = new("Открыть Wumo");
    private readonly ToolStripMenuItem clipboardItem = new("История буфера обмена") { Visible = false };
    private readonly ToolStripSeparator divider = new();
    private readonly ToolStripMenuItem exitItem = new("Выйти из трея");
    private readonly bool useSystemTheme = SystemInformation.HighContrast;
    private MenuRenderer? menuRenderer;

    internal TrayMenu(Action open, Action exit, ClipboardHistory.HistoryWidget clipboard)
    {
        openItem.Click += (_, _) => open();
        exitItem.Click += (_, _) => exit();
        Items.AddRange([openItem, clipboardItem, divider, exitItem]);
        clipboard.TrayChanged += () => clipboardItem.Visible = clipboard.Active;
        clipboardItem.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripItem item in clipboardItem.DropDownItems.Cast<ToolStripItem>().ToArray())
            {
                clipboardItem.DropDownItems.Remove(item);
                item.Dispose();
            }
            clipboard.PopulateTray(clipboardItem.DropDownItems);
        };
        clipboardItem.DropDownItems.Add(new ToolStripMenuItem());
        ShowImageMargin = false;
        ShowCheckMargin = false;

        if (useSystemTheme)
        {
            RenderMode = ToolStripRenderMode.System;
            return;
        }

        BackColor = Raised;
        ForeColor = Sand;
        Font = Mono(9);
        menuRenderer = new MenuRenderer(this);
        Renderer = menuRenderer;
        AutoSize = false;
        SetMetrics();
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        if (!useSystemTheme)
            SetMetrics();
        base.OnOpening(e);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x02E0 && !useSystemTheme) // WM_DPICHANGED
        {
            SetMetrics();
            SetShape();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SetShape();
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        menuRenderer?.ResetHighlight();
        base.OnClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            menuRenderer?.Dispose();
            menuRenderer = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        SetShape();
    }

    private void SetShape()
    {
        if (useSystemTheme || Width <= 1 || Height <= 1)
            return;

        using var shape = RoundedRectangle(ClientRectangle, Pixels(4, DeviceDpi));
        Region = new Region(shape);
    }

    private void SetMetrics()
    {
        int dpi = DeviceDpi;
        SuspendLayout();
        try
        {
            var rowSize = new Size(Pixels(clipboardItem.Available ? 250 : 206, dpi), Pixels(32, dpi));
            openItem.AutoSize = exitItem.AutoSize = clipboardItem.AutoSize = divider.AutoSize = false;
            openItem.Margin = exitItem.Margin = clipboardItem.Margin = divider.Margin = Padding.Empty;
            openItem.Size = exitItem.Size = clipboardItem.Size = rowSize;
            divider.Size = new Size(rowSize.Width, Pixels(7, dpi));
            Size = new Size(rowSize.Width, rowSize.Height * (clipboardItem.Available ? 3 : 2) + divider.Height + Pixels(5, dpi));
        }
        finally { ResumeLayout(performLayout: true); }
    }

    private sealed class MenuRenderer : ToolStripRenderer, IDisposable
    {
        private const int HighlightDuration = 120;
        private static readonly Color OpenIcon = Color.FromArgb(0x00, 0xD3, 0xF3);
        private static readonly Color ExitIcon = Color.FromArgb(0xFF, 0x63, 0x7E);
        private readonly TrayMenu menu;
        private readonly Dictionary<ToolStripItem, Highlight> highlights = [];
        private readonly System.Windows.Forms.Timer highlightTimer = new() { Interval = 15 };

        internal MenuRenderer(TrayMenu menu)
        {
            this.menu = menu;
            highlightTimer.Tick += (_, _) =>
            {
                long now = Environment.TickCount64;
                foreach (ToolStripItem item in highlights.Keys)
                    item.Invalidate();
                if (!AnimateHighlight || !highlights.Values.Any(state => now - state.Started < HighlightDuration))
                    highlightTimer.Stop();
            };
        }

        private static bool AnimateHighlight => UiMotion.IsEnabled && System.Windows.SystemParameters.MenuAnimation;

        internal void ResetHighlight()
        {
            highlightTimer.Stop();
            highlights.Clear();
        }

        public void Dispose() => highlightTimer.Dispose();

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var background = new SolidBrush(Raised);
            e.Graphics.FillRectangle(background, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var outline = RoundedRectangle(bounds, Pixels(4, menu.DeviceDpi));
            using var pen = new Pen(Line);
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, outline);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            int dpi = menu.DeviceDpi;
            bool selected = e.Item.Selected || e.Item.Pressed;
            double highlight = ReferenceEquals(e.ToolStrip, menu) && AnimateHighlight
                ? HighlightOpacity(e.Item, selected)
                : selected ? 1 : 0;
            if (e.Item.Pressed || highlight > 0)
            {
                var bounds = Rectangle.Inflate(new Rectangle(Point.Empty, e.Item.Size), -Pixels(5, dpi), -Pixels(1, dpi));
                Color fill = e.Item.Pressed ? Selected : Color.FromArgb(
                    (int)Math.Round(Raised.R + (Selected.R - Raised.R) * highlight),
                    (int)Math.Round(Raised.G + (Selected.G - Raised.G) * highlight),
                    (int)Math.Round(Raised.B + (Selected.B - Raised.B) * highlight));
                Frame(e.Graphics, bounds, fill, fill, Pixels(4, dpi));
            }

            if (ReferenceEquals(e.Item, menu.openItem))
                DrawIcon(e.Graphics, dpi, e.Item.Height, OpenIcon, opening: true);
            else if (ReferenceEquals(e.Item, menu.exitItem))
                DrawIcon(e.Graphics, dpi, e.Item.Height, ExitIcon, opening: false);
        }

        private double HighlightOpacity(ToolStripItem item, bool selected)
        {
            long now = Environment.TickCount64;
            highlights.TryGetValue(item, out var state);
            if (state.Selected != selected)
            {
                double opacity = state.Opacity(now);
                state = new Highlight(selected, selected ? Math.Max(.35, opacity) : opacity, now);
                highlights[item] = state;
                highlightTimer.Start();
            }
            return state.Opacity(now);
        }

        private readonly record struct Highlight(bool Selected, double From, long Started)
        {
            internal double Opacity(long now)
            {
                double target = Selected ? 1 : 0;
                double remaining = Math.Max(0, 1 - (double)(now - Started) / HighlightDuration);
                return target + (From - target) * remaining * remaining;
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            int dpi = menu.DeviceDpi;
            int left = Pixels(32, dpi);
            int width = Math.Max(0, e.Item.Width - left - Pixels(8, dpi));
            var bounds = new Rectangle(left, 0, width, e.Item.Height);
            Color ink = !e.Item.Enabled ? Disabled : e.Item.Selected || ReferenceEquals(e.Item, menu.openItem) ? Sand : Muted;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, ink,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = Pixels(8, menu.DeviceDpi);
            int y = e.Item.Height / 2;
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
        }

        private static void DrawIcon(Graphics graphics, int dpi, int rowHeight, Color color, bool opening)
        {
            float scale = dpi / 96f;
            float x = Pixels(9, dpi);
            float y = (rowHeight - 14 * scale) / 2;
            PointF At(float partX, float partY) => new(x + partX * scale, y + partY * scale);

            var previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 1.15f * scale)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
            };
            if (opening)
            {
                using var outline = RoundedRectangle(new Rectangle(Pixels(9, dpi), (int)y, Pixels(14, dpi), Pixels(12, dpi)), Pixels(2, dpi));
                graphics.DrawPath(pen, outline);
                graphics.DrawLine(pen, At(1, 4), At(13, 4));
                graphics.DrawLines(pen, [At(5, 7), At(6.5f, 8.5f), At(5, 10)]);
                graphics.DrawLine(pen, At(8.5f, 10), At(11, 10));
            }
            else
            {
                graphics.DrawLines(pen, [At(5, 1), At(1.5f, 1), At(1.5f, 13), At(5, 13)]);
                graphics.DrawLine(pen, At(5, 7), At(13, 7));
                graphics.DrawLines(pen, [At(10, 4), At(13, 7), At(10, 10)]);
            }
            graphics.SmoothingMode = previous;
        }
    }
}
