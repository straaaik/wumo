using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

namespace PixelThumbs;

internal static class QuickPreviewTarget
{
    private static readonly Guid ShellWindowsClass = new("9ba05972-f6a8-11cf-a442-00a0c90a8f39");
    private static readonly Guid TopLevelBrowserService = new("4c96be40-915c-11cf-99d3-00aa004ae837");
    private static readonly Guid BrowserInterface = typeof(IShellBrowser).GUID;
    private static readonly Guid PersistFolderInterface = typeof(IPersistFolder2).GUID;
    private static readonly Guid AutomationClass = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private const uint RootWindow = 2;
    private const uint MenuModes = 0x4 | 0x8 | 0x10;
    private const int DesktopWindow = 8;
    private const int NeedDispatch = 1;
    private const int AutomationControlType = 30003;
    private const int AutomationId = 30011;
    private const int AutomationBounds = 30001;
    private const int AutomationEdit = 50004;
    private const int AutomationListItem = 50007;
    private const int AutomationDataItem = 50029;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    internal readonly record struct ViewContext(nint Window, nint View, nint Focus, nint HitWindow, bool Desktop);
    internal readonly record struct FocusedView(nint Window, nint View, bool Desktop);
    internal readonly record struct Hit(string Path, Rectangle Bounds);

    internal static string? ResolveSelection(FocusedView context)
    {
        IShellWindows? windows = null;
        IShellView? view = null;
        IShellItemArray? selection = null;
        IShellItem? item = null;
        nint path = 0;
        try
        {
            windows = (IShellWindows?)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass)!);
            if (windows is null)
                return null;
            view = FindActiveView(windows, context.View, context.Desktop);
            const uint selectionOnly = 1, folder = 0x20000000, fileSystemPath = 0x80058000;
            if (view is not IFolderView folderView ||
                folderView.Items(selectionOnly, typeof(IShellItemArray).GUID, out selection) < 0 ||
                selection is null || selection.GetCount(out uint count) < 0 || count != 1 ||
                selection.GetItemAt(0, out item) < 0 || item is null ||
                item.GetAttributes(folder, out uint attributes) < 0 || (attributes & folder) != 0 ||
                item.GetDisplayName(fileSystemPath, out path) < 0)
                return null;
            string? file = Marshal.PtrToStringUni(path);
            return file is not null && Path.IsPathFullyQualified(file) && FormatCatalog.IsSupported(Path.GetExtension(file))
                ? file : null;
        }
        catch (COMException) { return null; }
        finally
        {
            Marshal.FreeCoTaskMem(path);
            Release(item);
            Release(selection);
            Release(view);
            Release(windows);
        }
    }

    internal static Hit? Resolve(ViewContext context, Point mouse)
    {
        if (GetView(mouse) != context || ReadHoveredItem(context, mouse) is not { } hit)
            return null;

        IShellWindows? windows = null;
        IShellView? view = null;
        try
        {
            windows = (IShellWindows?)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass)!);
            if (windows is null)
                return null;
            view = FindActiveView(windows, context.View, context.Desktop);
            if (view is not IFolderView folderView)
                return null;

            string? path = ReadItemPath(folderView, hit.Index);
            return path is not null && GetView(mouse) == context ? new Hit(path, hit.Bounds) : null;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            Release(view);
            Release(windows);
        }
    }

    // The low-level hooks use Win32 only. Explorer COM and UI Automation run on a separate STA.
    internal static ViewContext? GetView(Point mouse)
    {
        var name = new StringBuilder(64);
        if (!TryGetShellFocus(name, out nint foreground, out nint focus, out bool desktop))
            return null;
        nint hit = WindowFromPoint(mouse);
        nint view = GetAncestor(hit, RootWindow) == foreground ? FindDefView(hit, foreground, name) : 0;
        if (view == 0 || (focus != foreground && FindDefView(focus, foreground, name) != view))
            return null;
        return new ViewContext(foreground, view, focus, hit, desktop);
    }

    internal static FocusedView? GetFocusedView()
    {
        var name = new StringBuilder(64);
        if (!TryGetShellFocus(name, out nint foreground, out nint focus, out bool desktop))
            return null;
        nint view = FindDefView(focus, foreground, name);
        return view == 0 ? null : new FocusedView(foreground, view, desktop);
    }

    private static bool TryGetShellFocus(StringBuilder name, out nint foreground, out nint focus, out bool desktop)
    {
        foreground = GetForegroundWindow();
        focus = 0;
        desktop = false;
        if (foreground == 0)
            return false;

        string frame = WindowClass(foreground, name);
        desktop = IsDesktop(frame);
        if (frame is not ("CabinetWClass" or "ExploreWClass") && !desktop)
            return false;

        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        uint thread = GetWindowThreadProcessId(foreground, 0);
        if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Focus == 0 ||
            info.Caret != 0 || (info.Flags & MenuModes) != 0 ||
            GetAncestor(info.Focus, RootWindow) != foreground)
            return false;
        focus = info.Focus;
        return true;
    }
    private static nint FindDefView(nint child, nint frame, StringBuilder name)
    {
        for (nint window = child; window != 0 && window != frame; window = GetParent(window))
        {
            string type = WindowClass(window, name);
            if (type == "SHELLDLL_DefView")
                return window;
            if (type == "Edit" || type.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
                return 0;
        }
        return 0;
    }

    private static bool IsDesktop(string type) => type is "Progman" or "WorkerW";

    private static string WindowClass(nint window, StringBuilder buffer)
    {
        buffer.Clear();
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private static IShellView? FindActiveView(IShellWindows windows, nint focusedView, bool desktop)
    {
        if (desktop)
        {
            object desktopFolder = 0;
            object root = null!;
            if (windows.FindWindow(ref desktopFolder, ref root, DesktopWindow, out _, NeedDispatch, out var window) != 0)
                return null;
            try { return GetMatchingView(window, focusedView, desktop: true); }
            finally { Release(window); }
        }

        if (windows.GetCount(out int count) < 0)
            return null;
        for (int i = 0; i < count; i++)
        {
            if (windows.Item(i, out var window) != 0)
                continue;
            try
            {
                if (GetMatchingView(window, focusedView, desktop: false) is { } view)
                    return view;
            }
            catch (COMException) { } // The window or tab may close while it is being enumerated.
            finally { Release(window); }
        }
        return null;
    }

    private static IShellView? GetMatchingView(object? window, nint focusedView, bool desktop)
    {
        if (window is not IServiceProvider provider ||
            provider.QueryService(TopLevelBrowserService, BrowserInterface, out var browser) < 0 || browser is null)
            return null;

        try
        {
            if (!desktop && (browser.GetWindow(out nint browserWindow) < 0 ||
                GetAncestor(browserWindow, RootWindow) != GetAncestor(focusedView, RootWindow)))
                return null;
            if (browser.QueryActiveShellView(out var view) < 0 || view is null)
                return null;

            if (view.GetWindow(out nint windowHandle) >= 0 && windowHandle == focusedView)
                return view;
            Release(view);
            return null;
        }
        finally { Release(browser); }
    }

    private static nint GetFolderId(IFolderView view)
    {
        if (view.GetFolder(PersistFolderInterface, out var folder) < 0 || folder is null)
            return 0;
        try { return folder.GetCurFolder(out nint id) >= 0 ? id : 0; }
        finally { Release(folder); }
    }

    private static string? ReadItemPath(IFolderView view, int index)
    {
        nint parent = GetFolderId(view);
        nint child = 0, item = 0;
        try
        {
            if (parent == 0 || view.Item(index, out child) < 0 || child == 0)
                return null;
            item = ILCombine(parent, child);
            return PreviewablePath(PathFromItemId(item));
        }
        finally
        {
            Marshal.FreeCoTaskMem(parent);
            Marshal.FreeCoTaskMem(child);
            Marshal.FreeCoTaskMem(item);
        }
    }

    private static string? PathFromItemId(nint item)
    {
        if (item == 0)
            return null;
        var buffer = new StringBuilder(32768);
        return SHGetPathFromIDListEx(item, buffer, (uint)buffer.Capacity, 0) ? buffer.ToString() : null;
    }

    private static (int Index, Rectangle Bounds)? ReadHoveredItem(ViewContext context, Point mouse)
    {
        IAutomation? automation = null;
        IAutomationElement? focus = null, item = null;
        IAutomationTreeWalker? walker = null;
        try
        {
            automation = (IAutomation?)Activator.CreateInstance(Type.GetTypeFromCLSID(AutomationClass)!);
            if (automation is null)
                return null;
            bool editing = automation.GetFocusedElement(out focus) >= 0 &&
                focus?.GetCurrentPropertyValue(AutomationControlType) is AutomationEdit;
            if (editing)
                return null;
            if (WindowClass(context.HitWindow, new StringBuilder(64)) == "SysListView32")
                return ReadListViewItem(context.HitWindow, mouse);
            if (automation.ElementFromPoint(mouse, out item) < 0)
                return null;

            for (int depth = 0; item is not null && depth < 6; depth++)
            {
                object? type = item.GetCurrentPropertyValue(AutomationControlType);
                if (type is AutomationEdit)
                    return null;
                if (type is AutomationListItem or AutomationDataItem)
                {
                    // The Explorer file pane publishes the native item index as its UIA AutomationId.
                    // Resolve the corresponding child PIDL, including when display names are identical.
                    if (!int.TryParse(item.GetCurrentPropertyValue(AutomationId) as string, NumberStyles.None,
                        CultureInfo.InvariantCulture, out int index))
                        return null;
                    var bounds = item.GetCurrentPropertyValue(AutomationBounds) is double[] { Length: 4 } rect
                        ? Rectangle.Ceiling(new RectangleF((float)rect[0], (float)rect[1], (float)rect[2], (float)rect[3]))
                        : new Rectangle(mouse, new Size(1, 1));
                    return bounds.Contains(mouse) ? (index, bounds) : null;
                }

                if (walker is null && automation.GetControlViewWalker(out walker) < 0)
                    break;
                if (walker is null || walker.GetParentElement(item, out var parent) < 0)
                    break;
                Release(item);
                item = parent;
            }
            return null;
        }
        catch (COMException) { return null; }
        finally
        {
            Release(walker);
            Release(item);
            Release(focus);
            Release(automation);
        }
    }

    private static (int Index, Rectangle Bounds)? ReadListViewItem(nint window, Point mouse)
    {
        if (AccessibleObjectFromPoint(mouse, out var accessible, out object child) < 0 || accessible is null)
            return null;
        try
        {
            if (child is not int id || id <= 0 || WindowFromAccessibleObject(accessible, out nint owner) < 0 || owner != window)
                return null;
            return (id - 1, new Rectangle(mouse, new Size(1, 1)));
        }
        finally { Release(accessible); }
    }
    private static string? PreviewablePath(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path) || !FormatCatalog.IsSupported(Path.GetExtension(path)))
            return null;
        try
        {
            const FileAttributes unavailable = FileAttributes.Directory | FileAttributes.Offline | RecallOnDataAccess;
            return (File.GetAttributes(path) & unavailable) == 0 ? path : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, nint process);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(Point point, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible,
        [MarshalAs(UnmanagedType.Struct)] out object child);
    [DllImport("oleacc.dll")]
    private static extern int WindowFromAccessibleObject(IAccessible accessible, out nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("shell32.dll")] private static extern nint ILCombine(nint parent, nint child);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool SHGetPathFromIDListEx(nint item, StringBuilder path, uint size, uint flags);

    // Only the leading ABI slots needed here are declared for each Shell and UI Automation interface.
    [ComImport, Guid("85cb6900-4d95-11cf-960c-0080c7f4ee85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index,
            [MarshalAs(UnmanagedType.IDispatch)] out object? window);
        void NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();
        [PreserveSig] int FindWindow([In, MarshalAs(UnmanagedType.Struct)] ref object location,
            [In, MarshalAs(UnmanagedType.Struct)] ref object root, int windowClass, out int hwnd, int flags,
            [MarshalAs(UnmanagedType.IDispatch)] out object? window);
    }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(in Guid service, in Guid iid, out IShellBrowser? browser);
    }

    [ComImport, Guid("000214e2-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint window);
        void ContextSensitiveHelp();
        void InsertMenus();
        void SetMenu();
        void RemoveMenus();
        void SetStatusText();
        void EnableModeless();
        void TranslateAccelerator();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMessage();
        [PreserveSig] int QueryActiveShellView(out IShellView? view);
    }

    [ComImport, Guid("000214e3-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out nint window);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        [PreserveSig] int GetFolder(in Guid iid, out IPersistFolder2? folder);
        [PreserveSig] int Item(int index, out nint id);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, in Guid iid, out IShellItemArray? selection);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler();
        void GetPropertyStore();
        void GetPropertyDescriptionList();
        void GetAttributes();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem? item);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        [PreserveSig] int GetDisplayName(uint nameType, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    }

    [ComImport, Guid("1ac3d9f0-175c-11d1-95be-00609797ea4f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder2
    {
        void GetClassId();
        void Initialize();
        [PreserveSig] int GetCurFolder(out nint id);
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        [PreserveSig] int ElementFromPoint(Point point, out IAutomationElement? element);
        [PreserveSig] int GetFocusedElement(out IAutomationElement? element);
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        [PreserveSig] int GetControlViewWalker(out IAutomationTreeWalker? walker);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int property);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomationTreeWalker
    {
        [PreserveSig] int GetParentElement(IAutomationElement element, out IAutomationElement? parent);
    }
}
