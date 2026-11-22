using System.Runtime.InteropServices;
using System.Text;

namespace PixelThumbs;

public static class QuickPreviewKeys
{
    public const int Space = 0x20;
    public const int Escape = 0x1B;
    private const int MouseOffset = 0x100;
    public const int LeftMouse = MouseOffset + 1;
    public const int RightMouse = MouseOffset + 2;
    public const int MiddleMouse = MouseOffset + 4;
    public const int MouseBack = MouseOffset + 5;
    public const int MouseForward = MouseOffset + 6;
    public const int DefaultSize = 512;
    public static IReadOnlyList<int> Sizes { get; } = [256, 384, DefaultSize, 768, 1024];

    public static bool IsMouse(int value) => value is LeftMouse or RightMouse or MiddleMouse or MouseBack or MouseForward;

    internal static int Normalize(int value) => value is > 0 and < 0xFF and not Escape || IsMouse(value) ? value : Space;

    internal static int NormalizeSize(int size) => Sizes.Contains(size) ? size : DefaultSize;

    public static string DisplayName(int value) => value switch
    {
        Space => "Пробел",
        LeftMouse => "Левая кнопка мыши",
        RightMouse => "Правая кнопка мыши",
        MiddleMouse => "Средняя кнопка мыши",
        MouseBack => "Боковая кнопка 1",
        MouseForward => "Боковая кнопка 2",
        _ => KeyboardName(Normalize(value))
    };

    private static string KeyboardName(int value)
    {
        uint scanCode = MapVirtualKey((uint)value, 4);
        var name = new StringBuilder(96);
        int keyData = (int)((scanCode & 0xFF) << 16);
        if (scanCode >> 8 is 0xE0 or 0xE1)
            keyData |= 1 << 24;
        return GetKeyNameText(keyData, name, name.Capacity) > 0 ? name.ToString() : $"Клавиша 0x{value:X2}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapping);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetKeyNameTextW")]
    private static extern int GetKeyNameText(int keyData, StringBuilder name, int size);
}
