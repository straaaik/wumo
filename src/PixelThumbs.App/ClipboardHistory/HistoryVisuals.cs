using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;

namespace PixelThumbs.ClipboardHistory;

internal static class HistoryVisuals
{
    private static readonly Brush Ink = MakeBrush(0xfff4f0e8);
    private static readonly Brush Muted = MakeBrush(0xffbdbac2);

    internal static StackPanel CreatePreview(ClipboardItem item)
    {
        var content = new StackPanel();
        if (item.Thumbnail is { } thumbnail)
            content.Children.Add(new Image { Source = thumbnail, MaxHeight = 90, Stretch = Stretch.Uniform, HorizontalAlignment = System.Windows.HorizontalAlignment.Left });
        else
        {
            var lines = new TextBlock
            {
                Text = item.Label, Foreground = Ink, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 50
            };
            if (item.Kind == ClipboardKind.Files)
            {
                var file = new DockPanel();
                var icon = new TextBlock
                {
                    Text = item.Paths.Length > 1 ? "\ue8c8" : "\ue7c3",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Foreground = Muted, Margin = new Thickness(0, 2, 6, 0)
                };
                DockPanel.SetDock(icon, Dock.Left);
                file.Children.Add(icon);
                file.Children.Add(lines);
                content.Children.Add(file);
            }
            else
                content.Children.Add(lines);
        }
        if (item.Paths.Length > 1)
            content.Children.Add(Detail(Path.GetFileName(item.Paths[0].TrimEnd('\\', '/'))));
        if (item.Incomplete)
            content.Children.Add(Detail(item.Payloads.Length > 0 ? "Некоторые форматы недоступны" : "Повторная передача недоступна"));
        return content;
    }

    private static TextBlock Detail(string text) =>
        new() { Text = text, Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };

    internal static DoubleAnimation Motion(double from, double to, int milliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

    internal static SolidColorBrush MakeBrush(uint color)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
        brush.Freeze();
        return brush;
    }
}

