using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PixelThumbs;

internal static class StandardUserRestart
{
    private const int TokenLinkedToken = 19;
    private const int TokenElevation = 20;
    private const uint PrimaryTokenAccess = 0x0001 | 0x0002 | 0x0008;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;
    private const int ExitTimeout = 15_000;

    internal static async Task<PendingRestart?> StartAndWaitAsync(InstalledBuild build, CancellationToken cancellation)
    {
        TrayLauncher.ValidateRestartExecutable(build);
        string nonce = Guid.NewGuid().ToString("N");
        SafeWaitHandle? child = await Task.Run(() => Start(build, nonce), cancellation).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            while (WaitForSingleObject(child, 0) == WaitTimeout)
            {
                if (EventWaitHandle.TryOpenExisting(SignalName(nonce, "Ready"), out var ready))
                {
                    using (ready)
                    {
                        if (ready.WaitOne(0) && EventWaitHandle.TryOpenExisting(SignalName(nonce, "Proceed"), out var proceed))
                        {
                            var pending = new PendingRestart(child, proceed);
                            child = null;
                            return pending;
                        }
                    }
                }
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
            return null;
        }
        finally { child?.Dispose(); }
    }

    internal static bool WaitForPredecessor(string previousId, string nonce, string profile)
    {
        if (!int.TryParse(previousId, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ||
            pid <= 0 || pid == Environment.ProcessId || !Guid.TryParseExact(nonce, "N", out _))
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return false;

        string? directory = profile == "-" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(profile));
        if (directory is not null && !Path.IsPathFullyQualified(directory))
            return false;
        Environment.SetEnvironmentVariable("PIXELTHUMBS_DATA_DIR", directory);

        using var predecessor = OpenProcess(Synchronize, false, pid);
        if (predecessor.IsInvalid)
            return false;

        // The new process creates the signals at its own integrity level and waits until
        // the previous process confirms the restart and releases the tray mutex.
        using var proceed = new EventWaitHandle(false, EventResetMode.ManualReset, SignalName(nonce, "Proceed"), out bool createdProceed);
        if (!createdProceed)
            return false;
        using var ready = new EventWaitHandle(true, EventResetMode.ManualReset, SignalName(nonce, "Ready"), out bool createdReady);
        return createdReady && proceed.WaitOne(ExitTimeout) && WaitForSingleObject(predecessor, ExitTimeout) == 0;
    }

    private static SafeWaitHandle Start(InstalledBuild build, string nonce)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, TokenLinkedToken, out nint linkedHandle, IntPtr.Size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не предоставила обычные права для запуска обновления Wumo.");
        using var linked = new SafeAccessTokenHandle(linkedHandle);
        using var limited = new WindowsIdentity(linkedHandle);
        if (limited.User is not { } user || identity.User is not { } originalUser || !user.Equals(originalUser) ||
            new WindowsPrincipal(limited).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Windows не предоставила обычные права для запуска обновления Wumo.");
        if (!DuplicateTokenEx(linked, PrimaryTokenAccess, 0, 2, 1, out var primary))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось подготовить обычные права для запуска Wumo.");
        using var token = primary;
        if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) || elevated != 0)
            throw new InvalidOperationException("Windows не предоставила обычные права для запуска обновления Wumo.");

        string profile = TrayLauncher.UsesCustomProfile
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(SettingsStore.DataDirectory)) : "-";
        var command = new StringBuilder($"\"{build.Executable}\" --restart-after {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} {nonce} {profile}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessWithTokenW(token, 0, build.Executable, command, 0, 0, build.Directory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось запустить обновление Wumo с обычными правами.");

        using var threadHandle = new SafeWaitHandle(info.Thread, ownsHandle: true);
        return new SafeWaitHandle(info.Process, ownsHandle: true);
    }

    private static string SignalName(string nonce, string phase) => @"Local\PixelThumbs.Restart." + nonce + "." + phase;

    internal sealed class PendingRestart(SafeWaitHandle child, EventWaitHandle proceed) : IDisposable
    {
        internal bool Commit() => WaitForSingleObject(child, 0) == WaitTimeout && proceed.Set();

        public void Dispose()
        {
            proceed.Dispose();
            child.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out nint value, int size, out int actualSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out int value, int size, out int actualSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, nint attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string executable,
        StringBuilder command, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
