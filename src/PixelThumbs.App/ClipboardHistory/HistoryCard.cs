using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Point = System.Windows.Point;

namespace PixelThumbs.ClipboardHistory;

internal static class HistoryCard
{
    internal static void Attach(Border card, Action open, Action<Border> drag, ContextMenu menu)
    {
        card.Cursor = System.Windows.Input.Cursors.Hand;
        card.Focusable = true;
        var restingBrush = card.Background;
        var pressedBrush = card.TryFindResource("Frame") as Brush;
        Point? start = null;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsControl(e.OriginalSource))
                return;
            start = e.GetPosition(card);
            if (card.CaptureMouse() && pressedBrush is not null)
                UiMotion.SetBackground(card, pressedBrush);
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
            open();
        };
        card.LostMouseCapture += (_, e) =>
        {
            start = null;
            if (ReferenceEquals(e.OriginalSource, card))
                UiMotion.SetBackground(card, restingBrush);
        };
        card.KeyDown += (_, e) =>
        {
            if ((e.Key is Key.Enter or Key.Space) && !IsControl(e.OriginalSource))
            {
                e.Handled = true;
                open();
            }
        };

        card.ContextMenu = menu;

        bool IsControl(object source)
        {
            for (var element = source as Visual; element is not null && element != card;
                element = VisualTreeHelper.GetParent(element) as Visual)
                if (element is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase)
                    return true;
            return false;
        }
    }
}
