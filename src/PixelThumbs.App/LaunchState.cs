using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PixelThumbs;

internal sealed record InstalledBuild(long Revision, string Directory)
{
    internal static InstalledBuild Current { get; } = new(
        ParseRevision(typeof(InstalledBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion),
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

    internal string Executable => Path.Combine(Directory, "PixelThumbs.exe");

    internal static InstalledBuild FindForRestart()
    {
        var best = Current;
        bool protectedInstallation = MachineInstallation.IsProtectedLocation(Current.Directory);
        Consider(FromDirectory(Current.Directory));
        if (LaunchState.ReadLatest() is { } known)
            Consider(FromDirectory(known.Directory));

        if (protectedInstallation)
            return best;

        try
        {
            foreach (string directory in System.IO.Directory.EnumerateDirectories(LaunchState.UpdatesDirectory))
                if (long.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out long revision) &&
                    revision > best.Revision && FromDirectory(directory) is { } candidate && candidate.Revision == revision)
                    Consider(candidate);
        }
        catch (DirectoryNotFoundException) { }
        return best;

        void Consider(InstalledBuild? candidate)
        {
            if (candidate is not null && candidate.Revision > best.Revision && candidate.IsComplete() &&
                (!protectedInstallation || MachineInstallation.IsProtectedLocation(candidate.Directory)))
                best = candidate;
        }
    }

    private bool IsComplete() =>
        File.Exists(Path.Combine(Directory, "PixelThumbs.Core.dll")) &&
        File.Exists(Path.Combine(Directory, "PixelThumbs.deps.json")) &&
        File.Exists(Path.Combine(Directory, "PixelThumbs.runtimeconfig.json")) &&
        File.Exists(Path.Combine(Directory, "PixelThumbs.Shell.comhost.dll")) &&
        File.Exists(Path.Combine(Directory, "f3d", "bin", "f3d.exe")) &&
        File.Exists(Path.Combine(Directory, "f3d", "bin", "f3d.dll")) &&
        File.Exists(Path.Combine(Directory, "f3d", "bin", "osmesa.dll"));

    internal static InstalledBuild? FromDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            return null;

        try
        {
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!File.Exists(Path.Combine(directory, "PixelThumbs.exe")))
                return null;
            var version = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "PixelThumbs.dll"));
            long revision = ParseRevision(version.ProductVersion);
            return version.ProductName == "PixelThumbs" && revision > 0 ? new InstalledBuild(revision, directory) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static long ParseRevision(string? version)
    {
        const string prefix = "+pt.";
        int start = version?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
        if (start < 0)
            return 0;
        ReadOnlySpan<char> stamp = version.AsSpan(start + prefix.Length);
        int suffix = stamp.IndexOf('.');
        if (suffix >= 0)
            stamp = stamp[..suffix];
        return long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0 ? parsed : 0;
    }
}

internal static class LaunchState
{
    internal static string UpdatesDirectory => Path.Combine(SettingsStore.DataDirectory, "updates");

    private const string LatestFile = "latest-launch.json";
    private const string DraftFile = "upgrade-draft.json";
    internal static InstalledBuild? ReadLatest()
    {
        var record = Read<InstalledBuild>(LatestFile);
        if (record is not { Revision: > 0, Directory.Length: > 0 })
            return null;

        // A known installation can be updated in place before its new executable is started.
        var present = InstalledBuild.FromDirectory(record.Directory);
        return present is not null && present.Revision >= record.Revision ? present : record;
    }

    internal static void Remember(InstalledBuild build) => Write(LatestFile, build);
    internal static void Forget(InstalledBuild build)
    {
        if (ReadLatest() is { } latest && latest.Directory.Equals(build.Directory, StringComparison.OrdinalIgnoreCase))
            File.Delete(Path.Combine(SettingsStore.DataDirectory, LatestFile));
    }


    internal static AppSettings? ReadDraft() => Read<AppSettings>(DraftFile);

    internal static void SaveDraft(AppSettings settings) => Write(DraftFile, settings);

    internal static void ClearDraft()
    {
        try { File.Delete(Path.Combine(SettingsStore.DataDirectory, DraftFile)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static T? Read<T>(string name) where T : class
    {
        try
        {
            using var file = File.Open(Path.Combine(SettingsStore.DataDirectory, name), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return file.Length <= 1_048_576 ? JsonSerializer.Deserialize<T>(file) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write<T>(string name, T value)
    {
        string directory = SettingsStore.DataDirectory;
        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Open(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(staging, Path.Combine(directory, name), overwrite: true);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
