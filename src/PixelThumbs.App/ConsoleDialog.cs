using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using ActionResult = System.Windows.Forms.DialogResult;

namespace PixelThumbs;

internal static class ConsoleDialog
{
    internal readonly record struct Action(string Text, ActionResult Result, bool Primary = false, bool Dangerous = false);

    internal static ActionResult Show(Window? owner, string title, string message, ActionResult defaultResult, params Action[] actions)
    {
        var dialog = new CockpitDialog(owner, title);
        dialog.Body.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = dialog.Ink("Secondary"),
        });
        foreach (var action in actions)
            dialog.AddAction(action, defaultResult);
        return dialog.Run();
    }

    internal static ColorPicker.Selection? PickColor(Window owner, string title, ColorPicker.Selection current,
        int defaultColor, bool allowTransparent)
    {
        var dialog = new CockpitDialog(owner, title, width: 422);
        var picker = new ColorPicker(current, defaultColor, allowTransparent);
        dialog.Body.Children.Add(picker);
        dialog.AddAction(new Action("Отмена", ActionResult.Cancel), ActionResult.OK);
        var apply = dialog.AddAction(new Action("Выбрать цвет", ActionResult.OK, Primary: true), ActionResult.OK);
        picker.SelectionChanged += (_, _) => apply.IsEnabled = picker.IsValid;
        return dialog.Run() == ActionResult.OK ? picker.Selected : null;
    }

    private sealed class CockpitDialog : Window
    {
        private readonly WrapPanel actions = new() { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        private ActionResult result = ActionResult.Cancel;

        public CockpitDialog(Window? owner, string title, double width = 560)
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PixelThumbs;component/Ui/Cockpit.xaml", UriKind.Relative) });
            Owner = owner;
            Title = title;
            SizeToContent = SizeToContent.Height;
            Width = owner is { ActualWidth: > 0 } ? Math.Min(width, owner.ActualWidth - 32) : width;
            MaxHeight = owner is { ActualHeight: > 0 } ? Math.Min(600, owner.ActualHeight - 32) : 600;
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = owner is null;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = (FontFamily)FindResource("MonoFace");
            Foreground = Ink("Sand");
            Background = Ink("Canvas");
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 35, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new DockPanel { Margin = new Thickness(12, 4, 16, 4) };
            var close = new Button { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(54, 54, 60)),
                Style = (Style)FindResource("WindowDot"), ToolTip = "Закрыть окно" };
            AutomationProperties.SetName(close, "Закрыть окно");
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            close.Click += (_, _) => Close();
            DockPanel.SetDock(close, Dock.Left);
            heading.Children.Add(close);
            heading.Children.Add(new TextBlock
            {
                Text = title, Foreground = Ink("Muted"), FontSize = 12, LineHeight = 17,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            layout.Children.Add(new Border
            {
                Background = Ink("Bar"), BorderBrush = Ink("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading
            });

            Body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            var scroll = new ScrollViewer { Content = Body, Background = Ink("DialogBody"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);

            var footer = new Border
            {
                Background = Ink("Bar"), BorderBrush = Ink("Line"), BorderThickness = new Thickness(0, 1, 0, 0),
                Child = actions, Padding = new Thickness(14, 9, 18, 10),
            };
            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);
            Content = new Border { Background = Ink("Canvas"), BorderBrush = Ink("Frame"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = layout };
        }

        public StackPanel Body { get; }

        public Brush Ink(string key) => (Brush)FindResource(key);

        public Button AddAction(Action action, ActionResult defaultResult)
        {
            var button = new Button
            {
                Content = action.Text,
                Tag = action.Primary ? "Paper" : null,
                Style = (Style)FindResource("Command"),
                MinHeight = 29,
                IsDefault = action.Result == defaultResult,
                IsCancel = action.Result == ActionResult.Cancel,
                Margin = new Thickness(5, 0, 0, 0),
            };
            if (action.Dangerous)
                button.Foreground = Ink("Rose");
            button.Click += (_, _) => { result = action.Result; Close(); };
            actions.Children.Add(button);
            return button;
        }

        public ActionResult Run()
        {
            var parent = Owner as SettingsForm;
            parent?.SetDialogShade(true);
            try
            {
                ShowDialog();
                return result;
            }
            finally
            {
                parent?.SetDialogShade(false);
            }
        }
    }
}
