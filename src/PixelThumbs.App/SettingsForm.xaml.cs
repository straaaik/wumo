using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bitmap = System.Drawing.Bitmap;
using Brush = System.Windows.Media.Brush;
using ActionResult = System.Windows.Forms.DialogResult;
using Point = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;

namespace PixelThumbs;

public sealed partial class SettingsForm : Window
{
    private const string ClipboardTitle = "История буфера обмена";
    private static readonly int[] ClipboardExpirationHours = [0, 1, 6, 24, 168];
    private static readonly ThumbnailStyle Defaults = new();
    private static readonly StylePreset[] Presets =
    [
        new("Стандарт", Defaults.ModelColor, Defaults.BackgroundColor, Defaults.Transparent, Defaults.PreferEmbedded),
        new("Мята", 0xB0F9C1, 0x212531, true),
        new("Глина", 0xF4A879, 0x30242D),
        new("Моно", 0xEBE8E0, 0x292832),
        new("Лёд", 0x86D6F4, 0x192D42),
        new("Лаванда", 0xC8A8F6, 0x28213C),
        new("Янтарь", 0xF6C963, 0x362B20),
        new("Графит", 0x464E5B, 0xDFDCD3),
        new("Коралл", 0xF4877F, 0x382427),
    ];

    private readonly FormatRow[] formats;
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly DispatcherTimer previewIdleTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private AppSettings settings;
    private AppSettings savedSettings;
    private ThumbnailStyle editing;
    private QuickPreviewInputHook? capture;
    private int capturedInput;
    private string? previewFile;
    private bool loading = true;
    private bool clipboardLoading;
    private bool dirty;
    private bool recoveredDraft;
    private PreviewCancellation? previewRender;
    private WorkerClient.DesktopMeshPreview? meshPreview;
    private bool previewPending;
    private bool previewQueued;
    private bool refreshing;
    private bool resettingCache;
    private bool closed;
    private bool shuttingDown;
    private int previewVersion;
    private int resolution = 256;

    public SettingsForm()
    {
        var draft = LaunchState.ReadDraft();
        savedSettings = SettingsStore.Load();
        settings = draft is null ? CopySettings(savedSettings) : draft with { ClipboardHistory = savedSettings.ClipboardHistory };
        recoveredDraft = draft is not null;
        dirty = HasSettingsChanges();
        if (!dirty)
            ClearRecoveredDraft();
        editing = settings.DefaultStyle;
        formats = FormatCatalog.All.Select(format => new FormatRow(format, GetFormat(format.Extension).Enabled, SetFormatEnabled)).ToArray();

        InitializeComponent();
        PixelThumbsChoice.Checked += (_, _) => ShowWumoSettings(false);
        ClipboardChoice.Checked += (_, _) => ShowWumoSettings(false);
        ShowWumoSettings(false);
        DisplayClipboardOptions(savedSettings.ClipboardHistory);
        QuickPreviewSize.ItemsSource = QuickPreviewKeys.Sizes.Select(size => new SizeOption(size, $"{size} × {size}")).ToArray();
        QuickPreviewSize.DisplayMemberPath = nameof(SizeOption.Label);
        QuickPreviewSize.SelectedValuePath = nameof(SizeOption.Pixels);
        QuickPreviewSize.SelectedValue = settings.QuickPreviewSize;
        PreviewSize.ItemsSource = new[] { 48, 96, 256 }.Select(size => new SizeOption(size, $"{size} px")).ToArray();
        PreviewSize.DisplayMemberPath = nameof(SizeOption.Label);
        PreviewSize.SelectedValuePath = nameof(SizeOption.Pixels);
        PreviewSize.SelectedValue = resolution;
        StylePresets.ItemsSource = Presets;
        AutoStart.IsChecked = settings.AutoStart;
        ThumbnailsWidgetSwitch.IsChecked = settings.ThumbnailsWidgetEnabled;
        UpdateWidgetVisibility();
        QuickPreviewEnabled.IsChecked = settings.QuickPreview;
        UpdateBindingControls();
        TotalCount.Text = formats.Length.ToString(CultureInfo.CurrentCulture);
        RebuildFormats();
        UpdateMasterState();

        statusTimer.Tick += (_, _) => ShowEditStatus();
        previewIdleTimer.Tick += (_, _) => ReleaseMeshPreview();
        Activated += (_, _) => SchedulePreview();
        Deactivated += (_, _) =>
        {
            StopCapture();
            PausePreview();
        };
        PreviewCard.IsVisibleChanged += (_, _) =>
        {
            if (PreviewCard.IsVisible)
                SchedulePreview();
            else
                PausePreview();
        };
        Loaded += (_, _) => ArrangePanels();
        WorkspaceScroll.SizeChanged += (_, _) => ArrangePanels();
        SizeChanged += (_, _) => ArrangePanels();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                PausePreview();
            else
                SchedulePreview();
            bool expanded = WindowState == WindowState.Maximized;
            string title = expanded ? "Восстановить размер" : "Развернуть";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeWindowButton, title);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                SaveSettings();
            }
        };
        loading = false;
        LoadEditor();
        if (recoveredDraft)
            SetStatus("Несохранённые настройки восстановлены после обновления", "Amber");
        else
            ShowEditStatus();
    }

    public event EventHandler? Saved;
    internal event Action<ClipboardHistoryOptions>? ClipboardOptionsSaved;
    internal event Action? ClipboardPreviewRequested;
    internal event Func<Task<string?>>? RestartRequested;

    private FormatRow? Selected => FormatList.SelectedItem as FormatRow;

    private Brush Ink(string key) => (Brush)FindResource(key);

    private void ArrangePanels()
    {
        if (!IsLoaded)
            return;
        double width = WorkspaceArea.ActualWidth;
        bool wide = width >= 900;
        bool single = width < 550;

        Workbench.ColumnDefinitions[0].Width = single ? new(1, GridUnitType.Star) : new(wide ? 266 : 220);
        Workbench.ColumnDefinitions[1].Width = single ? new(0) : new(1, GridUnitType.Star);
        Workbench.ColumnDefinitions[2].Width = wide ? new(1, GridUnitType.Star) : new(0);
        Workbench.RowDefinitions[0].Height = wide ? new(1, GridUnitType.Star) : GridLength.Auto;
        Workbench.RowDefinitions[1].Height = wide ? new(0) : GridLength.Auto;
        Workbench.RowDefinitions[2].Height = single ? GridLength.Auto : new(0);
        Grid.SetColumn(StyleCard, single ? 0 : 1);
        Grid.SetRow(StyleCard, single ? 1 : 0);
        Grid.SetColumn(PreviewCard, wide ? 2 : 0);
        Grid.SetRow(PreviewCard, wide ? 0 : single ? 2 : 1);
        Grid.SetColumnSpan(PreviewCard, wide || single ? 1 : 2);
        FormatCard.Margin = new Thickness(0, 0, single ? 0 : 10, wide ? 0 : 10);
        StyleCard.Margin = new Thickness(0, 0, wide ? 10 : 0, wide ? 0 : 10);
        Workbench.Height = wide ? Math.Max(478, WorkspaceScroll.ActualHeight - Summary.ActualHeight - Summary.Margin.Bottom) : double.NaN;
        FormatCard.Height = wide ? double.NaN : 450;
        FormatCard.VerticalAlignment = wide ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        PreviewCard.Height = wide ? double.NaN : 410;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(WindowMessage);
        Dispatcher.BeginInvoke(FitToMonitor, DispatcherPriority.Loaded);
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x232) // WM_EXITSIZEMOVE
            Dispatcher.BeginInvoke(FitToMonitor, DispatcherPriority.Loaded);
        return 0;
    }

    private void FitToMonitor()
    {
        if (closed || WindowState != WindowState.Normal)
            return;
        nint handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var bounds))
            return;
        var area = Screen.FromHandle(handle).WorkingArea;
        int width = Math.Min(bounds.Right - bounds.Left, area.Width);
        int height = Math.Min(bounds.Bottom - bounds.Top, area.Height);
        int x = Math.Clamp(bounds.Left, area.Left, area.Right - width);
        int y = Math.Clamp(bounds.Top, area.Top, area.Bottom - height);
        if (x != bounds.Left || y != bounds.Top || x + width != bounds.Right || y + height != bounds.Bottom)
            SetWindowPos(handle, 0, x, y, width, height, 0x14); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void WumoSettings_Click(object sender, RoutedEventArgs e)
    {
        if (WumoSettingsPage.Visibility == Visibility.Visible)
        {
            PixelThumbsChoice.IsChecked = true;
            return;
        }

        PixelThumbsChoice.IsChecked = false;
        ClipboardChoice.IsChecked = false;
        ShowWumoSettings(true);
    }

    private void ShowWumoSettings(bool visible)
    {
        if (visible || ClipboardChoice.IsChecked == true)
            StopCapture();
        WumoSettingsPage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SaveSettingsButton.Visibility = ClipboardChoice.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        WumoSettingsButton.Tag = visible ? "Active" : null;
        SelectedApplicationName.Text = visible ? "Настройки" : ClipboardChoice.IsChecked == true ? ClipboardTitle :
            System.Windows.Automation.AutomationProperties.GetName(PixelThumbsChoice);
        if (!loading)
            ShowEditStatus();
    }

    private void DisplayClipboardOptions(ClipboardHistoryOptions value)
    {
        clipboardLoading = true;
        ClipboardSwitch.IsChecked = value.Enabled;
        ClipboardText.IsChecked = (value.Types & ClipboardKind.Text) != 0;
        ClipboardImages.IsChecked = (value.Types & ClipboardKind.Image) != 0;
        ClipboardFiles.IsChecked = (value.Types & ClipboardKind.Files) != 0;
        ClipboardOther.IsChecked = (value.Types & ClipboardKind.Other) != 0;
        ClipboardDockEdge.SelectedIndex = (int)value.Edge;
        var monitors = Screen.AllScreens.Select((screen, index) =>
            new ClipboardMonitorChoice(screen.DeviceName, $"Монитор {index + 1} · {screen.Bounds.Width} × {screen.Bounds.Height}")).ToList();
        monitors.Insert(0, new(null, "Основной монитор"));
        if (value.Monitor is { } monitor && !monitors.Any(choice => choice.Device == monitor))
            monitors.Add(new(monitor, "Отключённый монитор"));
        ClipboardMonitor.ItemsSource = monitors;
        ClipboardMonitor.SelectedItem = monitors.First(choice => choice.Device == value.Monitor);
        ClipboardPersist.IsChecked = value.KeepBetweenSessions;
        ClipboardExpire.SelectedIndex = Math.Max(0, Array.IndexOf(ClipboardExpirationHours, value.ExpireHours));
        clipboardLoading = false;
    }

    private void ClipboardOptions_Click(object sender, RoutedEventArgs e) => SaveClipboardOptions();
    private void ClipboardMode_Changed(object sender, SelectionChangedEventArgs e) => SaveClipboardOptions();
    private void ClipboardPreview_Click(object sender, RoutedEventArgs e) => ClipboardPreviewRequested?.Invoke();
    private sealed record ClipboardMonitorChoice(string? Device, string Label);

    private void SaveClipboardOptions()
    {
        if (loading || clipboardLoading)
            return;
        var previous = savedSettings.ClipboardHistory;
        var next = new ClipboardHistoryOptions
        {
            Enabled = ClipboardSwitch.IsChecked == true,
            Types = (ClipboardText.IsChecked == true ? ClipboardKind.Text : 0) |
                (ClipboardImages.IsChecked == true ? ClipboardKind.Image : 0) |
                (ClipboardFiles.IsChecked == true ? ClipboardKind.Files : 0) |
                (ClipboardOther.IsChecked == true ? ClipboardKind.Other : 0),
            Edge = (ClipboardEdge)Math.Max(0, ClipboardDockEdge.SelectedIndex),
            Monitor = (ClipboardMonitor.SelectedItem as ClipboardMonitorChoice)?.Device,
            KeepBetweenSessions = ClipboardPersist.IsChecked == true,
            ExpireHours = ClipboardExpirationHours[Math.Max(0, ClipboardExpire.SelectedIndex)]
        }.Normalize();
        if (next != previous)
        {
            try { SettingsStore.Save(SettingsStore.Load() with { ClipboardHistory = next }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                DisplayClipboardOptions(previous);
                SetStatus($"Не удалось сохранить настройки истории. {error.Message}", "Rose");
                return;
            }
            settings = settings with { ClipboardHistory = next };
            savedSettings = savedSettings with { ClipboardHistory = next };
            ClipboardOptionsSaved?.Invoke(next);
        }
        DisplayClipboardOptions(next);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SwitchWidgetPage(true);
    private void Thumbnails_Click(object sender, RoutedEventArgs e) => SwitchWidgetPage(false);

    private void ThumbnailsWidget_Changed(object sender, RoutedEventArgs e)
    {
        if (loading)
            return;
        settings = settings with { ThumbnailsWidgetEnabled = ThumbnailsWidgetSwitch.IsChecked == true };
        UpdateWidgetVisibility();
        UpdateDirtyState();
        RequestPreview();
    }

    private void UpdateWidgetVisibility()
    {
        bool enabled = settings.ThumbnailsWidgetEnabled;
        ModuleNavigation.IsEnabled = enabled;
        WorkspaceArea.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        WidgetOffNotice.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (enabled)
            return;

        StopCapture();
        previewPending = false;
        previewRender?.Cancel();
        ReleaseMeshPreview();
    }

    private void SwitchWidgetPage(bool widgetSettings)
    {
        StopCapture();
        WorkspaceScroll.Visibility = widgetSettings ? Visibility.Collapsed : Visibility.Visible;
        WidgetSettingsScroll.Visibility = widgetSettings ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailsTab.Tag = widgetSettings ? null : "Active";
        SettingsTab.Tag = widgetSettings ? "Active" : null;
        ArrangePanels();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!loading)
            RebuildFormats();
    }

    private void RebuildFormats(string? extension = null)
    {
        string? selected = extension ?? Selected?.Extension;
        string query = FormatSearch.Text.Trim();
        var results = formats.Where(format => format.Extension.Contains(query, StringComparison.OrdinalIgnoreCase)
            || format.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        bool wasLoading = loading;
        loading = true;
        FormatList.ItemsSource = results;
        FormatList.SelectedItem = results.FirstOrDefault(row => row.Extension == selected);
        if (Selected is { } row)
            FormatList.ScrollIntoView(row);
        EmptyFormats.Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        loading = wasLoading;
        if (!loading)
            LoadEditor();
    }

    private void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading)
            LoadEditor();
    }

    private void GeneralStyle_Click(object sender, RoutedEventArgs e) => FormatList.SelectedItem = null;

    private void AllFormats_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = formats.Any(row => !row.Enabled);
        loading = true;
        foreach (var row in formats)
        {
            var current = GetFormat(row.Extension);
            if (current.Enabled != enabled)
                settings.Formats[row.Extension] = current with { Enabled = enabled };
            row.Enabled = enabled;
        }
        if (Selected is { } selected)
            FormatEnabled.IsChecked = selected.Enabled;
        loading = false;
        UpdateMasterState();
        UpdateDirtyState();
    }

    private void FormatEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            row.Enabled = FormatEnabled.IsChecked == true;
    }

    private void SetFormatEnabled(string extension, bool enabled)
    {
        if (loading)
            return;
        var current = GetFormat(extension);
        if (current.Enabled == enabled)
            return;
        settings.Formats[extension] = current with { Enabled = enabled };
        if (Selected?.Extension == extension)
            FormatEnabled.IsChecked = enabled;
        UpdateMasterState();
        UpdateDirtyState();
    }

    private void UpdateMasterState()
    {
        int count = formats.Count(row => row.Enabled);
        AllFormats.IsChecked = count == 0 ? false : count == formats.Length ? true : null;
        EnabledCount.Text = count.ToString(CultureInfo.CurrentCulture);
    }

    private void LoadEditor()
    {
        var target = Selected;
        var format = target is null ? null : GetFormat(target.Extension);
        loading = true;
        GeneralStyleButton.Tag = target is null ? "Active" : null;
        EditorTitle.Text = target?.Extension ?? "Общий стиль";
        EditorDetail.Text = target is null ? "Используется форматами без собственного стиля." : $"{target.Label} · {target.Detail}";
        FormatOptions.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        FormatEnabled.IsChecked = format?.Enabled == true;
        CustomStyle.IsChecked = format?.Style is not null;
        StyleFields.IsEnabled = target is null || format?.Style is not null;
        bool geometry = target?.Extension is not (".f3d" or ".f3z" or ".fcstd" or ".gcode" or ".sldprt" or ".sldasm");
        ModelColor.IsEnabled = SourceColors.IsEnabled = PreferEmbedded.IsEnabled = geometry;
        Yaw.IsEnabled = Pitch.IsEnabled = Lighting.IsEnabled = geometry;
        ResetYaw.IsEnabled = ResetPitch.IsEnabled = ResetLighting.IsEnabled = geometry;
        editing = format?.Style ?? settings.DefaultStyle;
        DisplayStyle();
        loading = false;
        RequestPreview();
    }

    private void CustomStyle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } target)
            return;
        settings.Formats[target.Extension] = GetFormat(target.Extension) with { Style = CustomStyle.IsChecked == true ? editing : null };
        UpdateDirtyState();
        LoadEditor();
    }

    private void DisplayStyle()
    {
        TransparentBackground.IsChecked = editing.Transparent;
        SourceColors.IsChecked = editing.UseSourceColors;
        PreferEmbedded.IsChecked = editing.PreferEmbedded;
        Yaw.Value = MathF.Round(editing.Yaw);
        Pitch.Value = MathF.Round(editing.Pitch);
        Lighting.Value = MathF.Round(editing.Lighting * 100);
        MarginSlider.Value = MathF.Round(editing.Margin * 100);
        YawValue.Text = $"{Yaw.Value:0}°";
        PitchValue.Text = $"{Pitch.Value:0}°";
        LightingValue.Text = $"{Lighting.Value:0}%";
        MarginValue.Text = $"{MarginSlider.Value:0}%";
        ModelSwatch.Background = UiColor(editing.ModelColor);
        ModelHex.Text = $"#{editing.ModelColor:X6}";
        BackgroundSwatch.Background = editing.Transparent ? Ink("Checker") : UiColor(editing.BackgroundColor);
        BackgroundHex.Text = editing.Transparent ? "Прозрачный" : $"#{editing.BackgroundColor:X6}";
        ResetView.IsEnabled = editing.Yaw != Defaults.Yaw || editing.Pitch != Defaults.Pitch
            || editing.Lighting != Defaults.Lighting || editing.Margin != Defaults.Margin;
    }

    private void StyleOptions_Click(object sender, RoutedEventArgs e) => WriteStyle(editing with
    {
        Transparent = TransparentBackground.IsChecked == true,
        UseSourceColors = SourceColors.IsChecked == true,
        PreferEmbedded = PreferEmbedded.IsChecked == true
    });

    private void StyleSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading)
            return;
        float value = (float)e.NewValue;
        WriteStyle(sender switch
        {
            var slider when slider == Yaw => editing with { Yaw = value },
            var slider when slider == Pitch => editing with { Pitch = value },
            var slider when slider == Lighting => editing with { Lighting = value / 100 },
            var slider when slider == MarginSlider => editing with { Margin = value / 100 },
            _ => editing,
        });
    }

    private void ResetParameter_Click(object sender, RoutedEventArgs e) => WriteStyle(sender switch
    {
        var label when label == ResetYaw => editing with { Yaw = Defaults.Yaw },
        var label when label == ResetPitch => editing with { Pitch = Defaults.Pitch },
        var label when label == ResetLighting => editing with { Lighting = Defaults.Lighting },
        var label when label == ResetMargin => editing with { Margin = Defaults.Margin },
        _ => editing,
    });

    private void ResetView_Click(object sender, RoutedEventArgs e) => WriteStyle(editing with
    {
        Yaw = Defaults.Yaw, Pitch = Defaults.Pitch, Lighting = Defaults.Lighting, Margin = Defaults.Margin,
    });

    private void ModelColor_Click(object sender, RoutedEventArgs e) => PickColor(model: true);
    private void BackgroundColor_Click(object sender, RoutedEventArgs e) => PickColor(model: false);

    private void PickColor(bool model)
    {
        var initial = model ? new ColorPicker.Selection(editing.ModelColor, false)
            : new ColorPicker.Selection(editing.BackgroundColor, editing.Transparent);
        int defaultColor = model ? Defaults.ModelColor : Defaults.BackgroundColor;
        var selection = ConsoleDialog.PickColor(this, model ? "Цвет модели" : "Цвет фона", initial, defaultColor, !model);
        if (selection is not { } result || result == initial)
            return;
        WriteStyle(model ? editing with { ModelColor = result.Rgb, UseSourceColors = false, PreferEmbedded = false }
            : editing with { BackgroundColor = result.Rgb, Transparent = result.Transparent });
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StylePreset preset })
        {
            var next = editing with
            {
                ModelColor = preset.Model, BackgroundColor = preset.Background, Transparent = preset.Transparent,
                UseSourceColors = false, PreferEmbedded = preset.PreferEmbedded,
            };
            WriteStyle(ModelColor.IsEnabled ? next : editing with
            {
                BackgroundColor = next.BackgroundColor, Transparent = next.Transparent,
            });
        }
    }

    private void WriteStyle(ThumbnailStyle next)
    {
        if (loading || next == editing)
            return;
        if (Selected is { } target)
        {
            if (CustomStyle.IsChecked != true)
                return;
            settings.Formats[target.Extension] = GetFormat(target.Extension) with { Style = next };
        }
        else
        {
            settings = settings with { DefaultStyle = next };
        }
        editing = next;
        loading = true;
        DisplayStyle();
        loading = false;
        UpdateDirtyState();
        RequestPreview(styleOnly: true);
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        settings = settings with { AutoStart = AutoStart.IsChecked == true };
        UpdateDirtyState();
    }

    private void QuickPreview_Click(object sender, RoutedEventArgs e)
    {
        settings = settings with { QuickPreview = QuickPreviewEnabled.IsChecked == true };
        UpdateBindingControls();
        UpdateDirtyState();
    }

    private void QuickPreviewSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || QuickPreviewSize.SelectedValue is not int size || settings.QuickPreviewSize == size)
            return;
        settings = settings with { QuickPreviewSize = size };
        UpdateDirtyState();
    }

    private void ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (capture is not null)
        {
            StopCapture();
            return;
        }
        try
        {
            capture = new QuickPreviewInputHook(CaptureInput);
            CapturePrompt.Text = "Нажмите клавишу или кнопку мыши. Esc отменяет назначение.";
            CapturePrompt.Foreground = Ink("Secondary");
        }
        catch (Win32Exception)
        {
            CapturePrompt.Text = "Не удалось включить захват кнопки.";
            CapturePrompt.Foreground = Ink("Rose");
        }
    }

    private bool CaptureInput(int binding, bool down, Point pointer)
    {
        if (!IsActive)
            return false;
        if (capturedInput == binding)
        {
            if (!down)
                Dispatcher.BeginInvoke(StopCapture);
            return true;
        }
        if (!down || capturedInput != 0)
            return false;
        capturedInput = binding;
        Dispatcher.BeginInvoke(() =>
        {
            if (capture is null || capturedInput != binding)
                return;
            if (binding != QuickPreviewKeys.Escape && settings.QuickPreviewKey != binding)
            {
                settings = settings with { QuickPreviewKey = binding };
                UpdateDirtyState();
            }
            UpdateBindingControls();
            CapturePrompt.Text = "";
        });
        return true;
    }

    private void StopCapture()
    {
        capture?.Dispose();
        capture = null;
        capturedInput = 0;
        CapturePrompt.Text = "";
    }

    private void UpdateBindingControls()
    {
        QuickPreviewKey.Text = QuickPreviewKeys.DisplayName(settings.QuickPreviewKey);
        QuickPreviewKey.Foreground = Ink(settings.QuickPreview ? "Sand" : "Disabled");
        ChangeQuickPreviewKey.IsEnabled = QuickPreviewSize.IsEnabled = settings.QuickPreview;
    }

    private FormatSettings GetFormat(string extension) => settings.Formats.TryGetValue(extension, out var format) ? format : new();

    private string? ActivePreviewFile() => previewFile is { } path
        && (Selected is null || Path.GetExtension(path).Equals(Selected.Extension, StringComparison.OrdinalIgnoreCase)) ? path : null;

    private void OpenModel_Click(object sender, RoutedEventArgs e)
    {
        string patterns = string.Join(';', FormatCatalog.All.Select(format => $"*{format.Extension}"));
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = $"3D-модели|{patterns}|Все файлы|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
            return;
        previewFile = dialog.FileName;
        loading = true;
        FormatSearch.Clear();
        loading = false;
        RebuildFormats(Path.GetExtension(previewFile).ToLowerInvariant());
    }

    private void Sample_Click(object sender, RoutedEventArgs e)
    {
        previewFile = null;
        RequestPreview();
    }

    private void PreviewSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || PreviewSize.SelectedValue is not int size || resolution == size)
            return;
        resolution = size;
        RequestPreview();
    }

    private void RequestPreview(bool styleOnly = false)
    {
        if (closed || !settings.ThumbnailsWidgetEnabled)
            return;
        if (!styleOnly)
        {
            ++previewVersion;
            previewRender?.Cancel();
            var path = ActivePreviewFile();
            PreviewName.Text = path is null ? "Демонстрационная модель" : Path.GetFileName(path);
            PreviewName.ToolTip = PreviewName.Text;
            RefreshThumbnail.IsEnabled = path is not null && !refreshing;
            SetPreviewStatus(path is null ? "Рендер образца…" : "Чтение файла…", "Cyan");
        }
        // Finish the current frame and then take the latest style. Repeated input must not
        // restart the render or prevent intermediate frames from reaching the screen.
        previewPending = true;
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        if (!previewPending || previewQueued || previewRender is not null || !CanRenderPreview)
            return;
        previewQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            previewQueued = false;
            _ = RenderPreviewAsync();
        }, DispatcherPriority.Background);
    }

    private async Task RenderPreviewAsync()
    {
        if (!previewPending || previewRender is not null || !CanRenderPreview)
            return;
        previewPending = false;
        await using var cancellation = new PreviewCancellation();
        previewRender = cancellation;
        int version = previewVersion;
        string? path = ActivePreviewFile();
        var style = editing;
        int size = resolution;
        previewIdleTimer.Stop();
        var session = meshPreview;
        meshPreview = null;
        RenderFailure failure = RenderFailure.None;
        try
        {
            var image = await Task.Run(() =>
            {
                if (session is not null && (path is null || !session.Matches(path)))
                {
                    session.Dispose();
                    session = null;
                }
                if (path is not null)
                {
                    session ??= WorkerClient.DesktopMeshPreview.Open(path, cancellation.Token);
                    if (session?.Render(size, style, cancellation.Token) is { } pixels)
                        return ToImageSource(size, size, pixels);
                    session?.Dispose();
                    session = null;
                }

                using var bitmap = path is null ? PreviewService.RenderSample(size, style)
                    : PreviewService.RenderFile(path, size, style, cancellation.Token, report => failure = report.Failure);
                cancellation.Token.ThrowIfCancellationRequested();
                return bitmap is not null ? ToImageSource(bitmap) : null;
            }, cancellation.Token);
            if (!CanRenderPreview || version != previewVersion || cancellation.IsCancellationRequested)
                return;
            PreviewImage.Source = image;
            SetPreviewStatus(image is null ? PreviewService.FailureMessage(failure)
                : path is null ? "Образец с текущими параметрами." : "Стиль для выбранного файла.", image is null ? "Rose" : "Muted");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!closed && version == previewVersion && !cancellation.IsCancellationRequested)
            {
                PreviewImage.Source = null;
                SetPreviewStatus($"Не удалось построить миниатюру. {error.Message}", "Rose");
            }
        }
        finally
        {
            if (!CanRenderPreview || version != previewVersion || cancellation.IsCancellationRequested)
                session?.Dispose();
            else if (session is not null)
            {
                meshPreview = session;
                previewIdleTimer.Start();
            }
            previewRender = null;
            SchedulePreview();
        }
    }

    private bool CanRenderPreview => !closed && IsActive && WindowState != WindowState.Minimized &&
        settings.ThumbnailsWidgetEnabled && PreviewCard.IsVisible;

    private void PausePreview()
    {
        if (previewRender is { IsCancellationRequested: false } current)
        {
            previewPending = settings.ThumbnailsWidgetEnabled;
            current.Cancel();
        }
        ReleaseMeshPreview();
    }

    private void ReleaseMeshPreview()
    {
        previewIdleTimer.Stop();
        meshPreview?.Dispose();
        meshPreview = null;
    }

    private static BitmapSource ToImageSource(Bitmap bitmap)
    {
        const System.Drawing.Imaging.PixelFormat format = System.Drawing.Imaging.PixelFormat.Format32bppPArgb;
        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        int stride = bounds.Width * 4;
        byte[] pixels = new byte[stride * bounds.Height];
        var data = bitmap.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadOnly, format);
        try
        {
            for (int row = 0; row < bounds.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * stride, stride);
        }
        finally { bitmap.UnlockBits(data); }
        return ToImageSource(bounds.Width, bounds.Height, pixels);
    }

    private static BitmapSource ToImageSource(int width, int height, byte[] pixels)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private void SetPreviewStatus(string message, string color)
    {
        PreviewStatus.Text = message;
        PreviewStatus.Foreground = Ink(color);
        PreviewStatus.ToolTip = message;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePreviewFile() is not { } path)
            return;
        if (dirty)
        {
            SetStatus("Сначала сохраните изменённые настройки.", "Amber");
            return;
        }
        refreshing = true;
        RefreshThumbnail.IsEnabled = false;
        try
        {
            SetStatus("Запрашиваю обновление значка…", "Muted");
            int updated = await PreviewService.RefreshAsync([path]);
            if (!closed)
                SetStatus(updated > 0 ? "Миниатюра обновлена." : "Windows не вернула миниатюру для этого файла.", updated > 0 ? "Green" : "Amber");
        }
        catch (Exception error)
        {
            if (!closed)
                SetStatus($"Ошибка обновления. {error.Message}", "Rose");
        }
        finally
        {
            refreshing = false;
            if (!closed)
                RefreshThumbnail.IsEnabled = ActivePreviewFile() is not null;
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private bool SaveSettings()
    {
        if (resettingCache)
            return false;
        var previous = SettingsStore.Load();
        if (ClipboardChoice.IsChecked == true)
            return true;
        bool styleChanged = previous.DefaultStyle != settings.DefaultStyle || FormatCatalog.Entries.Any(format =>
            previous.Formats[format.Extension].Style != GetFormat(format.Extension).Style);
        bool clear = styleChanged && ConfirmCacheReset();
        try
        {
            ShellRegistration.Apply(settings, () => SettingsStore.Save(settings));
        }
        catch (Exception error)
        {
            SetStatus($"Ошибка применения настроек. {error.Message}", "Rose");
            return false;
        }

        savedSettings = CopySettings(settings);
        dirty = false;
        ClearRecoveredDraft();
        Saved?.Invoke(this, EventArgs.Empty);
        if (clear)
            _ = ResetCacheAsync();
        else
            ShowAppliedStatus("Настройки применены.");
        return true;
    }

    private bool ConfirmCacheReset() => ConsoleDialog.Show(this, "Сброс кэша эскизов",
        "Windows пересоздаст все эскизы, включая эскизы фотографий и видео. Сбросить кэш после сохранения?",
        ActionResult.Cancel,
        new ConsoleDialog.Action("Сбросить кэш", ActionResult.Yes, Dangerous: true),
        new ConsoleDialog.Action("Сохранить без сброса", ActionResult.Cancel, Primary: true)) == ActionResult.Yes;

    private async Task ResetCacheAsync()
    {
        resettingCache = true;
        SaveSettingsButton.IsEnabled = false;
        try
        {
            while (!closed)
            {
                SetStatus("Сброс кэша эскизов…", "Cyan");
                try
                {
                    await Task.Run(ThumbnailCacheReset.Clear);
                    if (!closed)
                        ShowAppliedStatus("Настройки применены, кэш эскизов сброшен.");
                    break;
                }
                catch (Exception error)
                {
                    if (closed)
                        break;
                    SetStatus($"Ошибка сброса кэша. {error.Message}", "Rose");
                    if (ConsoleDialog.Show(this, "Не удалось сбросить кэш эскизов", error.Message, ActionResult.Cancel,
                        new ConsoleDialog.Action("Повторить сброс", ActionResult.Retry),
                        new ConsoleDialog.Action("Закрыть", ActionResult.Cancel, Primary: true)) != ActionResult.Retry)
                        break;
                }
            }
        }
        finally
        {
            resettingCache = false;
            if (!closed)
                SaveSettingsButton.IsEnabled = true;
        }
    }

    private void ShowAppliedStatus(string message)
    {
        SetStatus(message, "Green");
        statusTimer.Start();
    }

    private static AppSettings CopySettings(AppSettings source) =>
        source with { Formats = new(source.Formats, StringComparer.OrdinalIgnoreCase) };

    private bool HasSettingsChanges() =>
        (settings with { Formats = savedSettings.Formats }) != savedSettings ||
        FormatCatalog.Entries.Any(format => savedSettings.Formats[format.Extension] != GetFormat(format.Extension));

    private void UpdateDirtyState()
    {
        dirty = HasSettingsChanges();
        if (!dirty)
            ClearRecoveredDraft();
        ShowEditStatus();
    }

    private void ClearRecoveredDraft()
    {
        if (recoveredDraft)
        {
            LaunchState.ClearDraft();
            recoveredDraft = false;
        }
    }

    private void ShowEditStatus()
    {
        if (ClipboardChoice.IsChecked == true)
            SetStatus("Настройки истории применяются сразу", "Muted");
        else if (dirty)
            SetStatus("Есть несохранённые изменения", "Amber");
        else
            SetStatus("Ctrl+S · Применить настройки", "Muted");
    }

    private void SetStatus(string message, string color)
    {
        statusTimer.Stop();
        StatusLine.Text = message;
        StatusLine.ToolTip = message;
        StatusDot.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.Fill = Ink(color);
    }

    internal void SetDialogShade(bool visible)
    {
        DialogShade.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Cockpit.Effect = visible ? new BlurEffect { Radius = 4 } : null;
    }

    private async void RestartWumo_Click(object sender, RoutedEventArgs e)
    {
        if (RestartRequested is not { } restart)
            return;
        RestartWumoButton.IsEnabled = false;
        SetStatus("Проверяю наличие новой сборки…", "Cyan");
        try
        {
            string? problem = await restart();
            if (!closed && problem is not null)
                SetStatus(problem, "Rose");
        }
        finally
        {
            if (!closed)
                RestartWumoButton.IsEnabled = true;
        }
    }

    internal bool CanRestart => !resettingCache && !refreshing && !OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

    internal bool PrepareForUpgrade()
    {
        if (!CanRestart)
            return false;
        if (dirty)
        {
            LaunchState.SaveDraft(settings);
            recoveredDraft = true;
        }
        return true;
    }

    internal void CloseForShutdown()
    {
        shuttingDown = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (shuttingDown || e.Cancel)
            return;
        if (resettingCache)
        {
            e.Cancel = true;
            return;
        }
        if (!dirty)
            return;
        var choice = ConsoleDialog.Show(this, "Есть несохранённые изменения", "Сохранить изменения перед закрытием?",
            ActionResult.Cancel,
            new ConsoleDialog.Action("Закрыть без сохранения", ActionResult.No),
            new ConsoleDialog.Action("Отмена", ActionResult.Cancel),
            new ConsoleDialog.Action("Сохранить", ActionResult.Yes, Primary: true));
        e.Cancel = choice == ActionResult.Cancel || (choice == ActionResult.Yes && (!SaveSettings() || resettingCache));
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        if (!shuttingDown)
            ClearRecoveredDraft();
        previewRender?.Cancel();
        ReleaseMeshPreview();
        statusTimer.Stop();
        StopCapture();
        base.OnClosed(e);
    }

    internal static SolidColorBrush UiColor(int color) => new(System.Windows.Media.Color.FromRgb(
        (byte)(color >> 16), (byte)(color >> 8), (byte)color));

    private sealed record StylePreset(string Name, int Model, int Background, bool Transparent = false, bool PreferEmbedded = false)
    {
        public SolidColorBrush Swatch => UiColor(Model);
    }

    private sealed record SizeOption(int Pixels, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class FormatRow(FormatInfo format, bool enabled, Action<string, bool> changed) : INotifyPropertyChanged
    {
        private bool isEnabled = enabled;
        public string Extension => format.Extension;
        public string Label => format.Label;
        public string Detail => format.Detail;
        public bool Enabled
        {
            get => isEnabled;
            set
            {
                if (isEnabled == value)
                    return;
                isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                changed(Extension, value);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out WindowRect bounds);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
