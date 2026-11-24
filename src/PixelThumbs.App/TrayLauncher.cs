using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PixelThumbs;

internal static class TrayLauncher
{
    internal static bool UsesCustomProfile => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PIXELTHUMBS_DATA_DIR"));

    private static readonly string Scope = UsesCustomProfile
        ? "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SettingsStore.DataDirectory.ToUpperInvariant())).AsSpan(0, 12))
        : "";
    private static string LaunchMutex => @"Local\PixelThumbs.Launch" + Scope;
    private static string TrayMutex => @"Local\PixelThumbs.Tray" + Scope;

    internal static bool UnregisterInstalledCopy()
    {
        using var launch = new Mutex(false, LaunchMutex);
        if (!Acquire(launch, 12_000))
            return false;
        try
        {
            using var tray = new Mutex(false, TrayMutex);
            if (!Acquire(tray, 0))
                return false;
            try
            {
                ShellRegistration.Unregister(currentCopyOnly: true);
                LaunchState.Forget(InstalledBuild.Current);
                return true;
            }
            finally { tray.ReleaseMutex(); }
        }
        finally { launch.ReleaseMutex(); }
    }

    internal static int Run(TrayLaunchAction action, Action<TrayChannel> runTray)
    {
        var current = InstalledBuild.Current;
        using var launch = new Mutex(false, LaunchMutex);
        using var resident = new Mutex(false, TrayMutex);
        var channel = new TrayChannel(Scope);
        if (!Acquire(launch, 12_000))
            throw new InvalidOperationException("Другой запуск PixelThumbs ещё выполняется.");

        bool ownsResident = false;
        try
        {
            var known = LaunchState.ReadLatest();
            if (known?.Revision > current.Revision)
                return OpenKnownBuild(known, action);

            ownsResident = Acquire(resident, 0);
            if (!ownsResident)
            {
                var reply = channel.Request(current.Revision, action);
                if (reply == TrayReply.Activated)
                    return 0;
                if (reply == TrayReply.Busy)
                    throw new InvalidOperationException("Завершите текущее действие в PixelThumbs и повторите запуск новой сборки.");

                ownsResident = Acquire(resident, reply == TrayReply.Replacing ? 8_000 : 0);
                if (!ownsResident)
                    throw new InvalidOperationException("В трее работает прежняя сборка PixelThumbs. Завершите её через меню трея и запустите новую сборку.");
            }

            try
            {
                // Keep the first usable path to equal copies so an in-place update remains discoverable.
                var canonical = known?.Revision == current.Revision && InstalledBuild.FromDirectory(known.Directory) is { } equal
                    && equal.Revision == current.Revision ? equal : current;
                if (known != canonical)
                    LaunchState.Remember(canonical);
                if (!UsesCustomProfile && SettingsStore.Load().AutoStart)
                    ShellRegistration.RefreshStartupPath(canonical);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Trace.TraceWarning($"PixelThumbs launcher registration: {error.Message}");
            }
        }
        catch
        {
            if (ownsResident)
                resident.ReleaseMutex();
            throw;
        }
        finally
        {
            launch.ReleaseMutex();
        }

        try
        {
            runTray(channel);
            return 0;
        }
        finally
        {
            resident.ReleaseMutex();
        }
    }

    private static int OpenKnownBuild(InstalledBuild known, TrayLaunchAction action)
    {
        var installed = InstalledBuild.FromDirectory(known.Directory);
        if (installed is null || installed.Revision < known.Revision)
            throw new FileNotFoundException("Последняя сборка PixelThumbs недоступна.", known.Executable);

        using var child = StartBuild(installed, action);
        return 0;
    }

    internal static Process StartBuild(InstalledBuild build, TrayLaunchAction action)
    {
        if (RequiresStandardUser(build))
            throw new InvalidOperationException($"Откройте новую сборку напрямую: {build.Executable}");

        if (action == TrayLaunchAction.Restart)
            ValidateRestartExecutable(build);

        var command = new ProcessStartInfo(build.Executable) { WorkingDirectory = build.Directory, UseShellExecute = false };
        if (action == TrayLaunchAction.Background)
            command.ArgumentList.Add("--startup");
        else if (action == TrayLaunchAction.Restart)
            command.ArgumentList.Add("--restart");
        return Process.Start(command) ?? throw new InvalidOperationException("Не удалось открыть сборку Wumo.");
    }

    internal static bool RequiresStandardUser(InstalledBuild build)
    {
        if (build.Directory.Equals(InstalledBuild.Current.Directory, StringComparison.OrdinalIgnoreCase))
            return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static void ValidateRestartExecutable(InstalledBuild build)
    {
        try
        {
            using var binary = File.OpenRead(build.Executable);
            using var reader = new PEReader(binary);
            if (reader.PEHeaders.PEHeader is null || reader.PEHeaders.IsDll ||
                reader.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                throw new BadImageFormatException();
        }
        catch (BadImageFormatException error)
        {
            throw new InvalidOperationException("Файл запуска сборки Wumo повреждён.", error);
        }
    }

    private static bool Acquire(Mutex mutex, int wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }
}

internal enum TrayReply : byte
{
    Activated = 1,
    Replacing,
    Busy,
}

internal enum TrayLaunchAction : byte
{
    Background,
    OpenSettings,
    Restart
}

internal sealed class TrayChannel
{
    private readonly string pipeName;

    internal TrayChannel(string scope)
    {
        using var process = Process.GetCurrentProcess();
        pipeName = $"PixelThumbs.Tray.{process.SessionId}{scope}";
    }

    internal TrayReply? Request(long revision, TrayLaunchAction action) =>
        RequestAsync(revision, action).GetAwaiter().GetResult();

    private async Task<TrayReply?> RequestAsync(long revision, TrayLaunchAction action)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            byte[] request = new byte[9];
            BinaryPrimitives.WriteInt64LittleEndian(request, revision);
            request[8] = (byte)action;
            await pipe.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            byte[] reply = new byte[1];
            await pipe.ReadExactlyAsync(reply, deadline.Token).ConfigureAwait(false);
            if (reply[0] == (byte)TrayReply.Replacing && action == TrayLaunchAction.Restart)
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            return (TrayReply)reply[0];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    internal async Task ListenAsync(Func<long, TrayLaunchAction, CancellationToken, Task<TrayReply>> onLaunch,
        Action<bool> finishReplacement, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            bool listening = false;
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                listening = true;
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] request = new byte[9];
                await pipe.ReadExactlyAsync(request, deadline.Token).ConfigureAwait(false);
                long revision = BinaryPrimitives.ReadInt64LittleEndian(request);
                if (revision <= 0 || request[8] > (byte)TrayLaunchAction.Restart)
                    continue;

                var action = (TrayLaunchAction)request[8];
                var response = await onLaunch(revision, action, deadline.Token).ConfigureAwait(false);
                bool accepted = false;
                try
                {
                    await pipe.WriteAsync(new[] { (byte)response }, deadline.Token).ConfigureAwait(false);
                    if (response == TrayReply.Replacing && action == TrayLaunchAction.Restart)
                    {
                        byte[] acknowledgment = new byte[1];
                        await pipe.ReadExactlyAsync(acknowledgment, deadline.Token).ConfigureAwait(false);
                        accepted = acknowledgment[0] == 1;
                    }
                    else
                        accepted = true;
                }
                finally
                {
                    if (response == TrayReply.Replacing)
                        finishReplacement(accepted);
                }
            }
            catch (IOException) when (!listening)
            {
                // A temporarily unavailable listener must not turn into a tight retry loop.
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // A client can exit or time out while the settings window is busy.
            }
            catch (UnauthorizedAccessException error)
            {
                Trace.TraceWarning($"PixelThumbs tray channel: {error.Message}");
                return;
            }
        }
    }
}
