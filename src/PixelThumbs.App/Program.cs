using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using PixelThumbs.ClipboardHistory;

namespace PixelThumbs;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--render-worker", var extension, var size])
            return WorkerClient.RunRenderer(extension, size);
        if (args is ["--preview-worker", var previewExtension, var length])
            return WorkerClient.RunPreviewWorker(previewExtension, length);
        if (args is ["--stage-protected"])
            return MaintainProtectedInstallation(stage: true);
        if (args is ["--rebind-protected"])
            return MaintainProtectedInstallation(stage: false);
        if (args is ["--unregister-installed"])
        {
            try { return TrayLauncher.UnregisterInstalledCopy() ? 0 : 1; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                Trace.TraceWarning($"Wumo uninstallation: {error.Message}");
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        ConsoleTheme.ConfigureNativeControls();
        if (args is ["--restart-after", ..])
        {
            try
            {
                if (args is not [_, var predecessor, var nonce, var profile] ||
                    !StandardUserRestart.WaitForPredecessor(predecessor, nonce, profile))
                    return 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                Trace.TraceWarning($"PixelThumbs restart handoff: {error.Message}");
                return 1;
            }
        }
        if (args is ["--refresh", .. var paths])
            return Refresh(paths);
        if (args is ["--unregister"])
        {
            try { ShellRegistration.Unregister(); }
            catch (Exception error)
            {
                ShowError("Не удалось отключить интеграцию.", error);
                return 1;
            }
            return 0;
        }

        var action = args switch
        {
            ["--startup"] => TrayLaunchAction.Background,
            ["--restart"] => TrayLaunchAction.Restart,
            _ => TrayLaunchAction.OpenSettings
        };
        bool startup = action == TrayLaunchAction.Background;
        if (startup && !SettingsStore.Load().AutoStart)
            return 0;
        try
        {
            return TrayLauncher.Run(action, channel =>
            {
                using var tray = new TrayContext(channel, openSettings: !startup);
                Application.Run(tray);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"PixelThumbs launch: {error.Message}");
            if (action == TrayLaunchAction.OpenSettings)
                ShowError("Не удалось запустить Wumo", error);
            return 1;
        }
    }

    private static int MaintainProtectedInstallation(bool stage)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return 2;
            if (stage)
                MachineInstallation.EnsureProtectedCopy(AppContext.BaseDirectory);
            else if (MachineInstallation.IsProtectedLocation(InstalledBuild.Current.Directory))
                ShellRegistration.Apply(SettingsStore.Load(), static () => { });
            else
                return 2;
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            Trace.TraceWarning($"Wumo installation: {error.Message}");
            return 1;
        }
    }

    private static int Refresh(string[] paths)
    {
        try
        {
            if (PreviewService.RefreshAsync(paths).GetAwaiter().GetResult() > 0)
                return 0;
            ConsoleDialog.Show(null, "Миниатюра не обновилась", "Проверьте формат и настройки 3D Thumbnails.",
                DialogResult.OK, new ConsoleDialog.Action("Закрыть", DialogResult.OK, Primary: true));
        }
        catch (Exception error) { ShowError("Не удалось обновить миниатюру.", error); }
        return 1;
    }

    private static void ShowError(string message, Exception error) =>
        ConsoleDialog.Show(null, message, error.Message, DialogResult.OK,
            new ConsoleDialog.Action("Закрыть", DialogResult.OK, Primary: true));

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Control dispatcher = new();
        private readonly TrayMenu menu;
        private readonly NotifyIcon tray;
        private readonly Icon icon;
        private readonly CancellationTokenSource lifetime = new();
        private readonly QuickPreviewHotkey quickPreview;
        private readonly HistoryWidget clipboardHistory;
        private SettingsForm? settings;
        private bool closing;
        private bool replacing;
        private bool checkingForUpdate;
        private long? requestedRestartRevision;

        internal TrayContext(TrayChannel channel, bool openSettings)
        {
            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("PixelThumbs.AppIcon")!;
            using var source = new Icon(resource, 32, 32);
            icon = (Icon)source.Clone();
            clipboardHistory = new HistoryWidget(dispatcher);
            menu = new TrayMenu(OpenSettings, ExitThread, clipboardHistory);
            tray = new NotifyIcon
            {
                Icon = icon, Text = "Wumo", Visible = true, ContextMenuStrip = menu
            };
            tray.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) OpenSettings(); };
            _ = dispatcher.Handle;
            quickPreview = new QuickPreviewHotkey(dispatcher);
            var current = SettingsStore.Load();
            quickPreview.Configure(current);
            clipboardHistory.Configure(current.ClipboardHistory);
            var stop = lifetime.Token;
            _ = Task.Run(() => channel.ListenAsync((revision, action, cancellation) =>
                dispatcher.InvokeAsync(() => HandleLaunch(revision, action), cancellation), FinishReplacement, stop));

            if (openSettings)
                dispatcher.BeginInvoke(OpenSettings);
        }

        private void OpenSettings()
        {
            if (replacing || closing)
                return;
            if (settings is null)
            {
                settings = new SettingsForm();
                settings.Saved += (_, _) => ApplySettings();
                settings.ClipboardOptionsSaved += clipboardHistory.Configure;
                settings.ClipboardPreviewRequested += clipboardHistory.PreviewShelf;
                settings.RestartRequested += RestartAsync;
                settings.Closed += (_, _) => settings = null;
                System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(settings);
                settings.Show();
            }
            if (settings?.WindowState == System.Windows.WindowState.Minimized)
                settings.WindowState = System.Windows.WindowState.Normal;
            settings?.Activate();
        }

        private async Task<string?> RestartAsync()
        {
            if (checkingForUpdate || replacing || closing)
                return "Перезапуск уже начался.";
            checkingForUpdate = true;
            var stop = lifetime.Token;
            try
            {
                var target = await Task.Run(InstalledBuild.FindForRestart, stop);
                if (replacing || closing)
                    return null;
                if (settings?.CanRestart == false)
                    return "Дождитесь завершения текущего действия.";

                if (TrayLauncher.RequiresStandardUser(target))
                {
                    using var pending = await StandardUserRestart.StartAndWaitAsync(target, stop);
                    if (replacing || closing)
                        return null;
                    if (pending is null)
                        return "Новая сборка Wumo не подтвердила запуск.";
                    if (settings is not null && !settings.PrepareForUpgrade())
                        return "Дождитесь завершения текущего действия.";
                    if (!pending.Commit())
                        return "Процесс обновления Wumo завершился при запуске.";
                    replacing = true;
                    ExitThread();
                    return null;
                }

                requestedRestartRevision = target.Revision;
                using var child = await Task.Run(() => TrayLauncher.StartBuild(target, TrayLaunchAction.Restart), stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await child.WaitForExitAsync(timeout.Token);
                if (replacing || closing)
                    return null;
                return target.Revision > InstalledBuild.Current.Revision
                    ? "Не удалось запустить новую сборку Wumo."
                    : "Не удалось перезапустить Wumo.";
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return null; }
            catch (OperationCanceledException) { return "Перезапуск не завершился вовремя."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            {
                return error.Message;
            }
            finally
            {
                requestedRestartRevision = null;
                checkingForUpdate = false;
            }
        }

        private TrayReply HandleLaunch(long revision, TrayLaunchAction action)
        {
            if (replacing)
                return TrayReply.Replacing;
            bool handoff = action == TrayLaunchAction.Restart
                ? revision == requestedRestartRevision && revision >= InstalledBuild.Current.Revision
                : revision > InstalledBuild.Current.Revision;
            if (!handoff)
            {
                if (action == TrayLaunchAction.OpenSettings)
                    dispatcher.BeginInvoke(OpenSettings);
                return TrayReply.Activated;
            }

            try
            {
                if (settings is not null && !settings.PrepareForUpgrade())
                    return TrayReply.Busy;
                replacing = true;
                return TrayReply.Replacing;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"PixelThumbs update draft: {error.Message}");
                return TrayReply.Busy;
            }
        }

        private void FinishReplacement(bool accepted)
        {
            try
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (accepted)
                        ExitThread();
                    else
                        replacing = false;
                });
            }
            catch (InvalidOperationException) { }
        }

        private void ApplySettings() => quickPreview.Configure(SettingsStore.Load());

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closing)
            {
                closing = true;
                lifetime.Cancel();
                quickPreview.Dispose();
                clipboardHistory.Dispose();
                settings?.CloseForShutdown();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                icon.Dispose();
                dispatcher.Dispose();
                lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
