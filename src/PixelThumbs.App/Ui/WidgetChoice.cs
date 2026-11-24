using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;

namespace PixelThumbs;

public sealed class WidgetChoice : RadioButton
{
    private const double DragThreshold = 8;
    private const double SwipeThreshold = 32;
    private const double GradientTravel = 2 * (SwipeThreshold - DragThreshold);
    private const double HorizontalBias = 1.25;

    private static readonly DependencyProperty GradientProgressProperty = DependencyProperty.Register(
        nameof(GradientProgress), typeof(double), typeof(WidgetChoice), new PropertyMetadata(0d,
            (sender, change) => ((WidgetChoice)sender).DrawGradient((double)change.NewValue)));

    public static readonly DependencyProperty WidgetEnabledProperty = DependencyProperty.Register(
        nameof(WidgetEnabled), typeof(bool), typeof(WidgetChoice), new PropertyMetadata(false, OnWidgetEnabledChanged));
    public static readonly RoutedEvent WidgetEnabledChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(WidgetEnabledChanged), RoutingStrategy.Direct, typeof(RoutedPropertyChangedEventHandler<bool>), typeof(WidgetChoice));

    private LinearGradientBrush? gradient;
    private Point? gestureStart;
    private double gradientAtDragStart;
    private bool dragged;
    private TouchDevice? activeTouch;

    public bool WidgetEnabled
    {
        get => (bool)GetValue(WidgetEnabledProperty);
        set => SetValue(WidgetEnabledProperty, value);
    }

    internal double GradientProgress => (double)GetValue(GradientProgressProperty);

    public event RoutedPropertyChangedEventHandler<bool> WidgetEnabledChanged
    {
        add => AddHandler(WidgetEnabledChangedEvent, value);
        remove => RemoveHandler(WidgetEnabledChangedEvent, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        gradient = null;
        if (GetTemplateChild("PART_Gradient") is Border { Background: LinearGradientBrush brush } surface)
        {
            gradient = brush.CloneCurrentValue();
            surface.Background = gradient;
            DrawGradient(GradientProgress);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WidgetChoicePeer(this);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsKeyboardFocused && Keyboard.Modifiers == ModifierKeys.None && (e.Key is Key.Left or Key.Right))
        {
            SetCurrentValue(WidgetEnabledProperty, e.Key == Key.Right);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (activeTouch is null && e.StylusDevice?.TabletDevice.Type != TabletDeviceType.Touch)
            BeginGesture(e.GetPosition(this));
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (activeTouch is null && IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            MoveGesture(e.GetPosition(this));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (activeTouch is null && EndGesture(e.GetPosition(this)))
        {
            e.Handled = true;
            ReleaseMouseCapture();
        }
        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (activeTouch is null)
            CancelGesture();
    }

    protected override void OnTouchDown(TouchEventArgs e)
    {
        if (gestureStart is null && e.TouchDevice.Capture(this))
        {
            activeTouch = e.TouchDevice;
            BeginGesture(e.GetTouchPoint(this).Position);
        }
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        if (e.TouchDevice == activeTouch)
            MoveGesture(e.GetTouchPoint(this).Position);
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        if (e.TouchDevice == activeTouch)
        {
            Point point = e.GetTouchPoint(this).Position;
            bool wasDrag = EndGesture(point);
            activeTouch = null;
            e.TouchDevice.Capture(null);
            if (!wasDrag && new Rect(RenderSize).Contains(point))
            {
                Focus();
                OnClick();
            }
        }
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        base.OnLostTouchCapture(e);
        if (e.TouchDevice == activeTouch)
        {
            activeTouch = null;
            CancelGesture();
        }
    }

    internal void BeginGesture(Point point)
    {
        gestureStart = point;
        dragged = false;
    }

    internal void MoveGesture(Point point)
    {
        if (gestureStart is not { } start)
            return;

        Vector delta = point - start;
        if (!dragged)
        {
            if (!IsDrag(delta))
                return;
            dragged = true;
            gradientAtDragStart = GradientProgress;
            BeginAnimation(GradientProgressProperty, null);
        }

        double deadZone = Math.Max(DragThreshold, HorizontalBias * Math.Abs(delta.Y));
        double travel = Math.Max(0, Math.Abs(delta.X) - deadZone) / GradientTravel;
        SetValue(GradientProgressProperty, Math.Clamp(gradientAtDragStart + Math.CopySign(travel, delta.X), 0, 1));
    }

    internal bool EndGesture(Point point)
    {
        if (gestureStart is not { } start)
            return false;

        Vector delta = point - start;
        bool wasDrag = dragged || IsDrag(delta);
        gestureStart = null;
        if (Math.Abs(delta.X) >= SwipeThreshold && IsHorizontal(delta) && WidgetEnabled != (delta.X > 0))
            SetCurrentValue(WidgetEnabledProperty, delta.X > 0);
        else if (wasDrag)
            SettleGradient();
        return wasDrag;
    }

    private void CancelGesture()
    {
        if (gestureStart is null)
            return;
        gestureStart = null;
        if (dragged)
            SettleGradient();
    }

    private static bool IsDrag(Vector delta) => Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)) >= DragThreshold;
    private static bool IsHorizontal(Vector delta) => Math.Abs(delta.X) >= HorizontalBias * Math.Abs(delta.Y);

    private void SettleGradient() => UiMotion.AnimateFeedback(this, GradientProgressProperty, WidgetEnabled ? 1 : 0);

    private void DrawGradient(double progress)
    {
        if (gradient is null)
            return;

        var stops = gradient.GradientStops;
        stops[0].Color = WithAlpha(stops[0].Color, 1 - progress);
        stops[1].Offset = .70 - .52 * progress;
        stops[2].Offset = .82 - .52 * progress;
        stops[3].Color = WithAlpha(stops[3].Color, progress);
    }

    private static Color WithAlpha(Color color, double amount) =>
        Color.FromArgb((byte)Math.Round(byte.MaxValue * amount), color.R, color.G, color.B);

    private static void OnWidgetEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var choice = (WidgetChoice)sender;
        var previous = (bool)e.OldValue;
        var enabled = (bool)e.NewValue;
        choice.SettleGradient();
        UIElementAutomationPeer.FromElement(choice)?.RaisePropertyChangedEvent(
            TogglePatternIdentifiers.ToggleStateProperty, ToToggleState(previous), ToToggleState(enabled));
        choice.RaiseEvent(new RoutedPropertyChangedEventArgs<bool>(previous, enabled, WidgetEnabledChangedEvent));
    }

    private static ToggleState ToToggleState(bool enabled) => enabled ? ToggleState.On : ToggleState.Off;

    private sealed class WidgetChoicePeer(WidgetChoice choice) : RadioButtonAutomationPeer(choice), IToggleProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);

        ToggleState IToggleProvider.ToggleState => ToToggleState(choice.WidgetEnabled);

        void IToggleProvider.Toggle()
        {
            if (!IsEnabled())
                throw new ElementNotEnabledException();
            choice.SetCurrentValue(WidgetEnabledProperty, !choice.WidgetEnabled);
        }
    }
}
