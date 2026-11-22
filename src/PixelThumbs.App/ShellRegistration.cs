using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using static PixelThumbs.RegistryTransaction;

namespace PixelThumbs;

internal static class ShellRegistration
{
    private const string Classes = @"Software\Classes";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyBackup = @"Software\PixelThumbs\PreviousThumbnailHandlers";
    private const string Backup = @"Software\PixelThumbs\PreviousRegistryValues";
    private const string MachineOwners = @"Software\PixelThumbs\ThumbnailProviderUsers";
    private const string ProfileList = @"Software\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private const string Verb = "PixelThumbs.Refresh";
    private const string VerbLabel = "Обновить миниатюру PixelThumbs";
    private const string Overlay = "TypeOverlay";
    private const string ServerName = "PixelThumbs.Shell.comhost.dll";

    internal static bool Apply(AppSettings settings, Action saveSettings) => Update(settings, saveSettings);

    internal static void Unregister(bool currentCopyOnly = false) => Update(null, static () => { }, currentCopyOnly);

    internal static void RefreshStartupPath(InstalledBuild build)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } sid)
            return;
        using var userLock = new Mutex(false, @"Global\PixelThumbs.Registration." + sid.Value);
        WithMutex(userLock, () =>
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            return RefreshStartupPath(user, build);
        });
    }

    internal static bool RefreshStartupPath(RegistryKey user, InstalledBuild build)
    {
        const string argument = " --startup";
        var current = Read(user, Run, "PixelThumbs");
        if (!IsOurCommand(current, argument) || current?.Data is not string command)
            return false;
        string executable = command[1..^(argument.Length + 1)];
        if (!Path.IsPathFullyQualified(executable) || executable.Equals(build.Executable, StringComparison.OrdinalIgnoreCase))
            return false;
        if (InstalledBuild.FromDirectory(Path.GetDirectoryName(executable)!) is { } installed && installed.Revision >= build.Revision)
            return false;
        using var key = user.OpenSubKey(Run, writable: true);
        key?.SetValue("PixelThumbs", $"\"{build.Executable}\"{argument}", RegistryValueKind.String);
        return key is not null;
    }

    private static bool Update(AppSettings? settings, Action saveSettings, bool currentCopyOnly = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Не удалось определить профиль Windows.");
        bool elevated = !currentCopyOnly && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        using var userLock = new Mutex(false, @"Global\PixelThumbs.Registration." + sid.Value);
        return WithMutex(userLock, () =>
        {
            if (!elevated)
                return UpdateLocked(settings, saveSettings, sid.Value, elevated: false, currentCopyOnly);
            var security = new MutexSecurity();
            foreach (var type in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(type, null),
                    MutexRights.FullControl, AccessControlType.Allow));
            using var machineLock = MutexAcl.Create(false, @"Global\PixelThumbs.MachineRegistration", out _, security);
            return WithMutex(machineLock, () => UpdateLocked(settings, saveSettings, sid.Value, elevated: true));
        });
    }

    private static bool WithMutex(Mutex mutex, Func<bool> change)
    {
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* This thread owns the mutex now. */ }
        try { return change(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool UpdateLocked(AppSettings? settings, Action saveSettings, string sid, bool elevated,
        bool currentCopyOnly = false)
    {
        bool hasF3d = settings is not null && BundledF3d.FindExecutable() is not null;
        bool needsThumbnails = FormatCatalog.Entries.Any(format =>
            IsEnabled(settings, format.Extension, format.RequiresF3d, hasF3d));
        bool installMachine = elevated && needsThumbnails;
        string directory = installMachine ? MachineInstallation.EnsureProtectedCopy(AppContext.BaseDirectory)
            : AppContext.BaseDirectory;
        string server = Path.Combine(directory, ServerName);
        if (needsThumbnails && !File.Exists(server))
            throw new FileNotFoundException("Компонент миниатюр отсутствует. Распакуйте все файлы приложения.", server);

        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var system = elevated ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64) : null;
        using var users = elevated ? RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64) : null;
        var otherClasses = new Lazy<HashSet<Guid>?>(() => ReadOtherThumbnailClasses(system!, users!, sid));
        bool changed = ApplyRegistry(user, system, sid, settings, directory, hasF3d, saveSettings,
            (_, id) => otherClasses.Value?.Contains(id) ?? true, currentCopyOnly);
        if (changed)
            ShellViews.NotifyAssociations();
        return changed;
    }

    internal static bool ApplyRegistry(RegistryKey user, RegistryKey? system, string sid,
        AppSettings? settings, string directory, bool hasF3d, Action saveSettings, Func<string, Guid, bool> legacyInUse,
        bool currentCopyOnly = false)
    {
        var tx = new RegistryTransaction();
        bool changed = false;
        string app = Path.Combine(directory, "PixelThumbs.exe");
        string server = Path.Combine(directory, ServerName);
        tx.Run(() =>
        {
            foreach (var (id, extension, _, _, requiresF3d) in FormatCatalog.Entries)
            {
                var format = new RegisteredFormat(extension, id);
                if (currentCopyOnly)
                {
                    var ownedLegacy = format.LegacyIds.Where(UsesCurrentServer).ToArray();
                    bool ownsClass = UsesCurrentServer(format.ClassId);
                    bool ownsVerb = PointsTo(Read(user, format.Command + @"\command"), $"\"{app}\" --refresh \"%1\"");
                    if (ownsClass || ownedLegacy.Length > 0)
                    {
                        changed |= RestoreHandler(tx, user, format.ExtensionHandler, format);
                        changed |= RestoreHandler(tx, user, format.SharedHandler, format);
                        changed |= RestoreOverlay(tx, user, format.Shared);
                    }
                    if (ownsVerb)
                        changed |= RemoveVerb(tx, user, format.Command);
                    if (ownsClass)
                        changed |= RemoveClass(tx, user, format.ClassId);
                    foreach (Guid legacy in ownedLegacy)
                        changed |= RemoveClass(tx, user, legacy);
                    continue;
                }

                bool enabled = IsEnabled(settings, extension, requiresF3d, hasF3d);
                if (enabled)
                {
                    changed |= RegisterClass(tx, system ?? user, format.ClassId, server);
                    if (system is not null)
                    {
                        tx.Write(system, OwnersPath(format.ClassId), sid, new Value(1, RegistryValueKind.DWord));
                        changed |= RemoveClass(tx, user, format.ClassId);
                    }

                    changed |= SetHandler(tx, user, format.ExtensionHandler, format);
                    changed |= SetHandler(tx, user, format.SharedHandler, format);
                    changed |= SuppressOverlay(tx, user, format.Shared);
                    changed |= SetVerb(tx, user, format.Command, app);
                }
                else
                {
                    changed |= RestoreHandler(tx, user, format.ExtensionHandler, format);
                    changed |= RestoreHandler(tx, user, format.SharedHandler, format);
                    changed |= RestoreOverlay(tx, user, format.Shared);
                    changed |= RemoveVerb(tx, user, format.Command);
                    changed |= RemoveClass(tx, user, format.ClassId);
                    if (system is not null)
                        changed |= ReleaseMachineClass(tx, system, sid, format.ClassId);
                }

                foreach (Guid legacy in format.LegacyIds)
                {
                    changed |= RemoveClass(tx, user, legacy);
                    if (system is not null && IsOurServer(Read(system, ServerPath(legacy))) &&
                        !legacyInUse(extension, legacy))
                        changed |= RemoveClass(tx, system, legacy);
                }
            }

            if (settings?.AutoStart is true)
                tx.Write(user, Run, "PixelThumbs", new Value($"\"{app}\" --startup"));
            else
            {
                var startup = Read(user, Run, "PixelThumbs");
                if (currentCopyOnly ? PointsTo(startup, $"\"{app}\" --startup") : IsOurCommand(startup, " --startup"))
                    tx.Write(user, Run, "PixelThumbs", null);
            }

            tx.Prune(user, LegacyBackup, @"Software");
            tx.Prune(user, Backup, @"Software");
            saveSettings();
        });
        return changed;

        bool UsesCurrentServer(Guid id) => PointsTo(Read(user, ServerPath(id)), server);
    }

    private static bool PointsTo(Value? value, string target) =>
        value is { Data: string path } && path.Equals(target, StringComparison.OrdinalIgnoreCase);

    private static bool SetHandler(RegistryTransaction tx, RegistryKey user, string path, RegisteredFormat format)
    {
        var current = Read(user, path);
        if (!format.Owns(current))
        {
            tx.Write(user, Backup, path, current);
            tx.Write(user, LegacyBackup, path, null);
        }
        return tx.Write(user, path, "", new Value(format.ClassId.ToString("B").ToUpperInvariant()));
    }

    private static bool RestoreHandler(RegistryTransaction tx, RegistryKey user, string path, RegisteredFormat format)
    {
        bool changed = false;
        if (format.Owns(Read(user, path)))
        {
            var previous = Read(user, Backup, path);
            // Older builds stored only a string and used an empty string for an absent value.
            if (previous is null && Read(user, LegacyBackup, path) is { Data: string { Length: > 0 } } legacy)
                previous = legacy;
            changed = tx.Write(user, path, "", previous);
            if (previous is null)
                tx.Prune(user, path, Classes);
        }
        tx.Write(user, Backup, path, null);
        tx.Write(user, LegacyBackup, path, null);
        return changed;
    }

    private static bool SuppressOverlay(RegistryTransaction tx, RegistryKey user, string path)
    {
        var current = Read(user, path, Overlay);
        if (current is { Data: "" })
            return false;

        tx.Write(user, Backup, path + @"\" + Overlay, current);
        tx.Write(user, Backup, path, new Value(1, RegistryValueKind.DWord));
        return tx.Write(user, path, Overlay, new Value(""));
    }

    private static bool RestoreOverlay(RegistryTransaction tx, RegistryKey user, string path)
    {
        if (Read(user, Backup, path) is null)
            return false;

        bool changed = false;
        string backup = path + @"\" + Overlay;
        if (Read(user, path, Overlay) is { Data: "" })
        {
            changed = tx.Write(user, path, Overlay, Read(user, Backup, backup));
            tx.Prune(user, path, Classes);
        }
        tx.Write(user, Backup, backup, null);
        tx.Write(user, Backup, path, null);
        return changed;
    }

    private static bool RegisterClass(RegistryTransaction tx, RegistryKey root, Guid id, string server)
    {
        string path = ServerPath(id);
        bool changed = tx.Write(root, path, "", new Value(server));
        changed |= tx.Write(root, path, "ThreadingModel", new Value("Both"));
        return changed;
    }

    private static bool RemoveClass(RegistryTransaction tx, RegistryKey root, Guid id)
    {
        string path = ServerPath(id);
        if (!IsOurServer(Read(root, path)))
            return false;

        tx.Write(root, path, "", null);
        if (Read(root, path, "ThreadingModel") is { Data: "Both" })
            tx.Write(root, path, "ThreadingModel", null);
        tx.Prune(root, path, Classes);
        return true;
    }

    private static bool ReleaseMachineClass(RegistryTransaction tx, RegistryKey system, string sid, Guid id)
    {
        string owners = OwnersPath(id);
        bool removed = tx.Write(system, owners, sid, null);
        using (var remaining = system.OpenSubKey(owners))
            if (!removed || remaining is { ValueCount: > 0 })
                return false;
        tx.Prune(system, owners, @"Software");
        return RemoveClass(tx, system, id);
    }

    private static HashSet<Guid>? ReadOtherThumbnailClasses(RegistryKey system, RegistryKey users, string currentSid)
    {
        try
        {
            var referenced = new HashSet<Guid>();
            var loaded = users.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var profiles = system.OpenSubKey(ProfileList);
            if (profiles is null)
                return null;
            ReadThumbnailClasses(system, Classes, referenced);
            foreach (string classes in loaded)
                if (classes.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) &&
                    !classes.Equals(currentSid + "_Classes", StringComparison.OrdinalIgnoreCase))
                    ReadThumbnailClasses(users, classes, referenced);

            foreach (string profile in profiles.GetSubKeyNames())
            {
                if (!profile.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ||
                    profile == currentSid || profile is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" ||
                    loaded.Contains(profile + "_Classes"))
                    continue;
                using var record = profiles.OpenSubKey(profile);
                if (record?.GetValue("ProfileImagePath") is not string path || !Path.IsPathFullyQualified(path))
                    return null;
                string hive = Path.Combine(path, "AppData", "Local", "Microsoft", "Windows", "UsrClass.dat");
                try { File.GetAttributes(hive); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
                // Query the signed-out profile without mounting it under a public registry key.
                int result = RegLoadAppKey(hive, out var handle, 0x20019, 0, 0); // KEY_READ
                using (handle)
                {
                    if (result != 0)
                        return null;
                    using var offline = RegistryKey.FromHandle(handle, RegistryView.Default);
                    ReadThumbnailClasses(offline, "", referenced);
                }
            }
            return referenced;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void ReadThumbnailClasses(RegistryKey root, string prefix, HashSet<Guid> referenced)
    {
        if (prefix.Length > 0)
            prefix += "\\";
        string category = @"\ShellEx\" + FormatCatalog.HandlerCategory;
        foreach (var (_, extension, _, _, _) in FormatCatalog.Entries)
        {
            Add(Read(root, prefix + extension + category));
            Add(Read(root, prefix + @"SystemFileAssociations\" + extension + category));
        }

        void Add(Value? value)
        {
            if (value is { Data: string text } && Guid.TryParse(text, out Guid id))
                referenced.Add(id);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKey(string path, out SafeRegistryHandle key, int access, int options, int reserved);

    private static bool SetVerb(RegistryTransaction tx, RegistryKey user, string path, string app)
    {
        bool changed = tx.Write(user, path, "MUIVerb", new Value(VerbLabel));
        changed |= tx.Write(user, path, "MultiSelectModel", new Value("Single"));
        changed |= tx.Write(user, path, "Icon", new Value(app));
        changed |= tx.Write(user, path + @"\command", "", new Value($"\"{app}\" --refresh \"%1\""));
        return changed;
    }

    private static bool RemoveVerb(RegistryTransaction tx, RegistryKey user, string path)
    {
        string command = path + @"\command";
        if (!IsOurCommand(Read(user, command), " --refresh \"%1\""))
            return false;

        tx.Write(user, command, "", null);
        if (Read(user, path, "Icon") is { Data: string icon } && IsOurExecutable(icon))
            tx.Write(user, path, "Icon", null);
        if (Read(user, path, "MultiSelectModel") is { Data: "Single" })
            tx.Write(user, path, "MultiSelectModel", null);
        if (Read(user, path, "MUIVerb") is { Data: VerbLabel })
            tx.Write(user, path, "MUIVerb", null);
        tx.Prune(user, command, Classes);
        return true;
    }

    private static bool IsEnabled(AppSettings? settings, string extension, bool requiresF3d, bool hasF3d) =>
        settings is { ThumbnailsWidgetEnabled: true } && settings.Formats.GetValueOrDefault(extension)?.Enabled is true
        && (!requiresF3d || hasF3d);

    private static bool Matches(Value? value, Guid id) =>
        value is { Data: string text } && Guid.TryParse(text, out Guid found) && found == id;

    private static bool IsOurServer(Value? value) => value is { Data: string path } &&
        string.Equals(Path.GetFileName(path), ServerName, StringComparison.OrdinalIgnoreCase);

    private static bool IsOurExecutable(string path) =>
        string.Equals(Path.GetFileName(path), "PixelThumbs.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsOurCommand(Value? value, string arguments) =>
        value is { Data: string text } && text.StartsWith('"') &&
        text.EndsWith("\"" + arguments, StringComparison.OrdinalIgnoreCase) &&
        IsOurExecutable(text[1..^(arguments.Length + 1)]);

    private static string ServerPath(Guid id) => $@"{Classes}\CLSID\{id:B}\InprocServer32";
    private static string OwnersPath(Guid id) => $@"{MachineOwners}\{id:B}";

    private sealed record RegisteredFormat(string Extension, string Id)
    {
        internal Guid ClassId { get; } = new(Id);
        internal Guid[] LegacyIds { get; } =
            new[] { "835", "836", "837", "838" }.Select(prefix =>
                new Guid("CDB49CA7-2710-4EEA-94E0-D9F361B" + prefix + Id[^2..])).ToArray();
        internal string ExtensionHandler => $@"{Classes}\{Extension}\ShellEx\{FormatCatalog.HandlerCategory}";
        internal string Shared => $@"{Classes}\SystemFileAssociations\{Extension}";
        internal string SharedHandler => $@"{Shared}\ShellEx\{FormatCatalog.HandlerCategory}";
        internal string Command => $@"{Shared}\shell\{Verb}";
        internal bool Owns(Value? value) => Matches(value, ClassId) || LegacyIds.Any(id => Matches(value, id));
    }
}
