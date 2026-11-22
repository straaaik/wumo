using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;

namespace PixelThumbs.ClipboardHistory;

internal static class HistoryCard
{
    internal static void Attach(Border card, Action restore, Action<Border> drag, Action remove,
        Action clearClipboard, Action clearHistory, Action<bool> menuState)
    {
        card.Cursor = System.Windows.Input.Cursors.Hand;
        card.Focusable = true;
        Point? start = null;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(card);
            card.CaptureMouse();
            e.Handled = true;
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed)
                return;
            Point current = e.GetPosition(card);
            if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            start = null;
            card.ReleaseMouseCapture();
            drag(card);
        };
        card.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (start is null)
                return;
            start = null;
            card.ReleaseMouseCapture();
            e.Handled = true;
            restore();
        };
        card.LostMouseCapture += (_, _) => start = null;
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                restore();
            }
        };

        var menu = new ContextMenu();
        AddMenu(menu, "Вернуть в буфер", restore);
        AddMenu(menu, "Удалить из истории", remove);
        AddMenu(menu, "Очистить системный буфер", clearClipboard);
        menu.Items.Add(new System.Windows.Controls.Separator());
        AddMenu(menu, "Очистить историю", clearHistory);
        menu.Opened += (_, _) => menuState(true);
        menu.Closed += (_, _) => menuState(false);
        card.ContextMenu = menu;
    }

    private static void AddMenu(ContextMenu menu, string name, Action invoke)
    {
        var item = new MenuItem { Header = name };
        item.Click += (_, _) => invoke();
        menu.Items.Add(item);
    }
}
