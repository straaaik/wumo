using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PixelThumbs;

internal sealed class QuickPreviewInputHook : IDisposable
{
    private const int KeyboardHook = 13;
    private const int MouseHook = 14;
    private readonly Func<int, bool, Point, bool> handle;
    private readonly Action<bool>? pointerChanged;
    private readonly int pointerKey;
    private readonly HookProcedure keyboardCallback;
    private readonly HookProcedure mouseCallback;
    private readonly nint keyboard;
    private readonly nint mouse;

    internal QuickPreviewInputHook(Func<int, bool, Point, bool> handle, Action<bool>? pointerChanged = null,
        bool trackMouse = true, int pointerKey = 0)
    {
        this.handle = handle;
        this.pointerChanged = pointerChanged;
        this.pointerKey = pointerKey;
        keyboardCallback = OnKeyboard;
        mouseCallback = OnMouse;
        nint module = GetModuleHandle(null);
        keyboard = SetWindowsHookEx(KeyboardHook, keyboardCallback, module, 0);
        if (keyboard == 0)
            throw new Win32Exception();
        if (!trackMouse)
            return;
        mouse = SetWindowsHookEx(MouseHook, mouseCallback, module, 0);
        if (mouse == 0)
        {
            var error = new Win32Exception();
            UnhookWindowsHookEx(keyboard);
            throw error;
        }
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code == 0 && message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            int key = Marshal.ReadInt32(data);
            bool down = message is 0x100 or 0x104;
            if (handle(key, down, key == pointerKey ? Cursor.Position : Point.Empty))
                return 1;
        }
        return CallNextHookEx(0, code, message, data);
    }

    private nint OnMouse(int code, nuint message, nint data)
    {
        if (code == 0)
        {
            if (message is 0x200 or 0x20A or 0x20E) // WM_MOUSEMOVE, WM_MOUSEWHEEL, WM_MOUSEHWHEEL
                pointerChanged?.Invoke(message != 0x200);
            int key = message switch
            {
                0x201 or 0x202 => QuickPreviewKeys.LeftMouse,
                0x204 or 0x205 => QuickPreviewKeys.RightMouse,
                0x207 or 0x208 => QuickPreviewKeys.MiddleMouse,
                0x20B or 0x20C => (Marshal.ReadInt32(data, 8) >> 16) == 1
                    ? QuickPreviewKeys.MouseBack : QuickPreviewKeys.MouseForward,
                _ => 0
            };
            bool down = message is 0x201 or 0x204 or 0x207 or 0x20B;
            if (key != 0 && handle(key, down, Marshal.PtrToStructure<Point>(data)))
                return 1;
        }
        return CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        if (mouse != 0)
            UnhookWindowsHookEx(mouse);
        UnhookWindowsHookEx(keyboard);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nuint message, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern nint SetWindowsHookEx(int id, HookProcedure procedure, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string? module);
}
