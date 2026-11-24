using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AliasButton = System.Windows.Controls.Button;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace PixelThumbs;

public sealed partial class ColorPicker : System.Windows.Controls.UserControl
{
    internal readonly record struct Selection(int Rgb, bool Transparent);

    private static readonly int[] PaletteColors =
    [
        0xFFFFFF, 0xD2CCBB, 0xA4A9B3, 0x505B69, 0x19232D, 0x0A0A0A, 0x362B20, 0x382427,
        0x75F2BF, 0x86D6F4, 0x5285F2, 0xC8A8F6, 0xF4877F, 0xF4A879, 0xF6C963, 0xB0F9C1,
    ];

    private readonly int defaultColor;
    private double saturation, value;
    private bool updating = true;

    internal ColorPicker(Selection initial, int defaultColor, bool allowTransparent)
    {
        this.defaultColor = defaultColor;
        InitializeComponent();
        DefaultSwatch.Background = SettingsForm.UiColor(defaultColor);
        TransparentButton.Visibility = allowTransparent ? Visibility.Visible : Visibility.Collapsed;
        foreach (int color in PaletteColors)
        {
            string hex = $"#{color:X6}";
            var button = new AliasButton { Style = (Style)FindResource("PaletteSwatch"), Background = SettingsForm.UiColor(color), ToolTip = hex };
            AutomationProperties.SetName(button, hex);
            button.Click += (_, _) => SetRgb(color);
            Palette.Children.Add(button);
        }
        SetRgb(initial.Rgb, transparent: initial.Transparent);
    }

    internal event EventHandler? SelectionChanged;
    internal Selection Selected { get; private set; }
    internal bool IsValid { get; private set; }

    private void ColorArea_Down(object sender, MouseButtonEventArgs e)
    {
        ColorArea.Focus();
        ColorArea.CaptureMouse();
        PickPoint(e.GetPosition(ColorArea));
        e.Handled = true;
    }

    private void ColorArea_Move(object sender, MouseEventArgs e)
    {
        if (ColorArea.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            PickPoint(e.GetPosition(ColorArea));
    }

    private void ColorArea_Up(object sender, MouseButtonEventArgs e) => ColorArea.ReleaseMouseCapture();

    private void PickPoint(Point point)
    {
        if (ColorArea.ActualWidth <= 0 || ColorArea.ActualHeight <= 0)
            return;
        saturation = Math.Clamp(point.X / ColorArea.ActualWidth, 0, 1);
        value = 1 - Math.Clamp(point.Y / ColorArea.ActualHeight, 0, 1);
        UpdateFromCoordinates();
    }

    private void ColorArea_KeyDown(object sender, KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : .01;
        switch (e.Key)
        {
            case Key.Left: saturation = Math.Max(0, saturation - step); break;
            case Key.Right: saturation = Math.Min(1, saturation + step); break;
            case Key.Up: value = Math.Min(1, value + step); break;
            case Key.Down: value = Math.Max(0, value - step); break;
            default: return;
        }
        UpdateFromCoordinates();
        e.Handled = true;
    }

    private void ColorArea_SizeChanged(object sender, SizeChangedEventArgs e) => PositionMarker();

    private void Hue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updating)
            UpdateFromCoordinates();
    }

    private void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating)
            return;
        string hex = Hex.Text.Trim().TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            SetRgb(rgb, keepHex: true);
        else
        {
            IsValid = false;
            if (HexError.Visibility != Visibility.Visible)
            {
                HexError.Visibility = Visibility.Visible;
                UiMotion.Reveal(HexError, offset: 0, initialOpacity: .4);
            }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Default_Click(object sender, RoutedEventArgs e) => SetRgb(defaultColor);
    private void Transparent_Click(object sender, RoutedEventArgs e) => SetRgb(Selected.Rgb, transparent: true);

    private void SetRgb(int rgb, bool transparent = false, bool keepHex = false)
    {
        updating = true;
        double r = (rgb >> 16 & 255) / 255.0, g = (rgb >> 8 & 255) / 255.0, b = (rgb & 255) / 255.0;
        value = Math.Max(r, Math.Max(g, b));
        double spread = value - Math.Min(r, Math.Min(g, b));
        saturation = value == 0 ? 0 : spread / value;
        if (spread > 0)
        {
            double sector = value == r ? (g - b) / spread : value == g ? 2 + (b - r) / spread : 4 + (r - g) / spread;
            Hue.Value = (sector * 60 + 360) % 360;
        }
        DisplaySelection(new(rgb, transparent), keepHex);
        updating = false;
    }

    private void UpdateFromCoordinates()
    {
        updating = true;
        DisplaySelection(new(RgbFromHsv(Hue.Value, saturation, value), false), keepHex: false);
        updating = false;
    }

    private void DisplaySelection(Selection selection, bool keepHex)
    {
        Selected = selection;
        IsValid = true;
        if (!keepHex)
            Hex.Text = $"#{selection.Rgb:X6}";
        HexError.Visibility = Visibility.Collapsed;
        TransparentButton.Tag = selection.Transparent ? "Active" : null;
        SelectedSwatch.Background = selection.Transparent ? null : SettingsForm.UiColor(selection.Rgb);
        ColorArea.Background = SettingsForm.UiColor(RgbFromHsv(Hue.Value, 1, 1));
        PositionMarker();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PositionMarker()
    {
        Canvas.SetLeft(ColorMarker, saturation * Math.Max(0, ColorArea.ActualWidth - ColorMarker.Width));
        Canvas.SetTop(ColorMarker, (1 - value) * Math.Max(0, ColorArea.ActualHeight - ColorMarker.Height));
    }

    private static int RgbFromHsv(double hue, double saturation, double value)
    {
        int Channel(int offset)
        {
            double angle = (offset + hue / 60) % 6;
            double component = value * (1 - saturation * Math.Max(0, Math.Min(1, Math.Min(angle, 4 - angle))));
            return (int)Math.Round(component * 255);
        }

        return Channel(5) << 16 | Channel(3) << 8 | Channel(1);
    }
}
