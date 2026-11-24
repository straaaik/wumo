using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PixelPoint = System.Drawing.Point;
using Brushes = System.Windows.Media.Brushes;

namespace PixelThumbs.ClipboardHistory;

internal sealed class DragPreview : ClipboardNative.IDropSource
{
    private readonly Window? ghost;
    private readonly Border? card;

    internal DragPreview(ClipboardItem item, Border origin)
    {
        if (!UiMotion.IsEnabled)
            return;
        card = origin;
        ghost = new Window
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Topmost = true,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            SizeToContent = SizeToContent.Height, Width = Math.Clamp(origin.ActualWidth, 120, 260),
            Content = new Border
            {
                Child = HistoryVisuals.CreatePreview(item), Background = HistoryVisuals.MakeBrush(0xf1252d38),
                BorderBrush = HistoryVisuals.MakeBrush(0xa0869db4), BorderThickness = new Thickness(1),
                Padding = new Thickness(9), CornerRadius = new CornerRadius(8), IsHitTestVisible = false
            }
        };
        ghost.SourceInitialized += (_, _) => PanelPlacement.Configure(ghost, transparentToInput: true);
        new WindowInteropHelper(ghost).EnsureHandle();
        FollowCursor();
        ghost.Show();
        FollowCursor();
        card.Opacity = .58;
    }

    public int QueryContinueDrag(bool escape, uint keys) => escape ? ClipboardNative.DragCancel :
        (keys & 1) == 0 ? ClipboardNative.DragDrop : 0;

    public int GiveFeedback(uint effect)
    {
        FollowCursor();
        return ClipboardNative.DefaultCursors;
    }

    private void FollowCursor()
    {
        if (ghost is null)
            return;
        var cursor = PanelPlacement.Cursor();
        PanelPlacement.Move(ghost, new PixelPoint(cursor.X + 14, cursor.Y + 18));
    }

    internal void Finish(bool dropped)
    {
        if (ghost is null || card is null)
            return;
        if (!UiMotion.IsEnabled)
        {
            card.Opacity = 1;
            ghost.Close();
            return;
        }
        bool returnToCard = !dropped && PresentationSource.FromVisual(card) is not null;
        if (returnToCard)
        {
            var start = PanelPlacement.Location(ghost);
            var destination = card.PointToScreen(new System.Windows.Point());
            var clock = HistoryVisuals.Motion(1, .7, 150).CreateClock();
            clock.CurrentTimeInvalidated += (_, _) =>
            {
                double progress = clock.CurrentProgress ?? 1;
                var x = (int)Math.Round(start.X + (destination.X - start.X) * progress);
                var y = (int)Math.Round(start.Y + (destination.Y - start.Y) * progress);
                PanelPlacement.Move(ghost, new PixelPoint(x, y));
            };
            clock.Completed += (_, _) => { card.Opacity = 1; ghost.Close(); };
            ghost.ApplyAnimationClock(UIElement.OpacityProperty, clock);
        }
        else
        {
            var fade = HistoryVisuals.Motion(1, 0, 110);
            fade.Completed += (_, _) => { card.Opacity = 1; ghost.Close(); };
            ghost.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
