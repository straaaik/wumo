using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;

namespace PixelThumbs;

public static class UiMotion
{
    private const int FeedbackDuration = 125;
    private const int RevealDuration = 180;
    private static readonly QuadraticEase EaseOut = new() { EasingMode = EasingMode.EaseOut };

    static UiMotion() => EaseOut.Freeze();

    internal static bool IsEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public static readonly DependencyProperty BackgroundProperty = BrushTarget("Background", Border.BackgroundProperty);
    public static readonly DependencyProperty BorderBrushProperty = BrushTarget("BorderBrush", Border.BorderBrushProperty);
    public static readonly DependencyProperty OpacityProperty = ValueTarget("Opacity", 1, (element, value) =>
        AnimateFeedback(element, UIElement.OpacityProperty, value));
    public static readonly DependencyProperty ScaleProperty = ValueTarget("Scale", 1, (element, value) =>
    {
        var scale = TransformFor<ScaleTransform>(element);
        Transition(element, scale, ScaleTransform.ScaleXProperty, value);
        Transition(element, scale, ScaleTransform.ScaleYProperty, value);
    });
    public static readonly DependencyProperty RotationProperty = ValueTarget("Rotation", 0, (element, value) =>
        Transition(element, TransformFor<RotateTransform>(element), RotateTransform.AngleProperty, value));

    public static Brush? GetBackground(DependencyObject element) => (Brush?)element.GetValue(BackgroundProperty);
    public static void SetBackground(DependencyObject element, Brush? value) => element.SetValue(BackgroundProperty, value);
    public static Brush? GetBorderBrush(DependencyObject element) => (Brush?)element.GetValue(BorderBrushProperty);
    public static void SetBorderBrush(DependencyObject element, Brush? value) => element.SetValue(BorderBrushProperty, value);
    public static double GetOpacity(DependencyObject element) => (double)element.GetValue(OpacityProperty);
    public static void SetOpacity(DependencyObject element, double value) => element.SetValue(OpacityProperty, value);
    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject element, double value) => element.SetValue(ScaleProperty, value);
    public static double GetRotation(DependencyObject element) => (double)element.GetValue(RotationProperty);
    public static void SetRotation(DependencyObject element, double value) => element.SetValue(RotationProperty, value);

    internal static void AnimateFeedback(FrameworkElement element, DependencyProperty property, double value) =>
        Transition(element, element, property, value);

    internal static void Reveal(FrameworkElement element, double offset = 6, double initialOpacity = .78)
    {
        if (!element.IsLoaded || !IsEnabled)
            return;

        double opacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        double from = element.Opacity != opacity ? element.Opacity : opacity * initialOpacity;
        element.BeginAnimation(UIElement.OpacityProperty, Timeline(from, opacity, RevealDuration));
        if (offset != 0)
        {
            var translate = TransformFor<TranslateTransform>(element);
            double fromY = translate.Y != 0 ? translate.Y : offset;
            translate.BeginAnimation(TranslateTransform.YProperty, Timeline(fromY, 0, RevealDuration));
        }
    }

    private static DependencyProperty BrushTarget(string name, DependencyProperty property) =>
        DependencyProperty.RegisterAttached(name, typeof(Brush), typeof(UiMotion),
            new PropertyMetadata(null, (element, change) => ChangeBrush((Border)element, property, (Brush?)change.NewValue)));

    private static DependencyProperty ValueTarget(string name, double standard, Action<FrameworkElement, double> update) =>
        DependencyProperty.RegisterAttached(name, typeof(double), typeof(UiMotion),
            new PropertyMetadata(standard, (element, change) => update((FrameworkElement)element, (double)change.NewValue)));

    private static void ChangeBrush(Border element, DependencyProperty property, Brush? target)
    {
        if (CanAnimate(element) && target is SolidColorBrush next && element.GetValue(property) is SolidColorBrush current)
        {
            var from = current.Color;
            var to = next.Color;
            if (from != to)
            {
                if (from.A == 0)
                    from = System.Windows.Media.Color.FromArgb(0, to.R, to.G, to.B);
                if (to.A == 0)
                    to = System.Windows.Media.Color.FromArgb(0, from.R, from.G, from.B);

                var brush = new SolidColorBrush(next.Color);
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, Duration(FeedbackDuration))
                {
                    EasingFunction = EaseOut,
                    FillBehavior = FillBehavior.Stop,
                });
                element.SetCurrentValue(property, brush);
                return;
            }
        }

        element.SetCurrentValue(property, target);
    }

    private static void Transition(FrameworkElement element, IAnimatable target, DependencyProperty property, double value)
    {
        var dependencyObject = (DependencyObject)target;
        double current = (double)dependencyObject.GetValue(property);
        dependencyObject.SetValue(property, value);
        var timeline = CanAnimate(element) && current != value ? Timeline(current, value, FeedbackDuration) : null;
        target.BeginAnimation(property, timeline, HandoffBehavior.SnapshotAndReplace);
    }

    private static T TransformFor<T>(FrameworkElement element) where T : Transform, new()
    {
        if (element.RenderTransform is T transform && !transform.IsFrozen)
            return transform;

        var newTransform = new T();
        element.RenderTransform = newTransform;
        return newTransform;
    }

    private static bool CanAnimate(FrameworkElement element) => element.IsLoaded && element.IsVisible && IsEnabled;

    private static Duration Duration(int milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    private static DoubleAnimation Timeline(double from, double to, int milliseconds) => new(from, to, Duration(milliseconds))
    {
        EasingFunction = EaseOut,
        FillBehavior = FillBehavior.Stop,
    };
}
