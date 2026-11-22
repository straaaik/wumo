using System.Security.Cryptography;
using System.Text;

namespace PixelThumbs;

internal static class MachineInstallation
{
    internal static string ProtectedRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PixelThumbs");

    internal static bool IsProtectedLocation(string directory) =>
        ProtectedRoot.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)), StringComparison.OrdinalIgnoreCase);

    private static readonly string[] PackageFiles =
    [
        "PixelThumbs.exe", "PixelThumbs.dll", "PixelThumbs.deps.json", "PixelThumbs.runtimeconfig.json",
        "PixelThumbs.Core.dll", "PixelThumbs.Shell.dll", "PixelThumbs.Shell.comhost.dll",
        "PixelThumbs.Shell.deps.json", "PixelThumbs.Shell.runtimeconfig.json", "zstd.dll", "Zstandard-LICENSE.txt",
        "OpenSWX-LICENSE.txt"
    ];

    // An elevated Explorer may ignore HKCU COM classes. Its machine class must point to a protected copy.
    internal static string EnsureProtectedCopy(string source, string? protectedRoot = null)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string renderer = Path.Combine(source, BundledF3d.DirectoryName);
        if (BundledF3d.FindExecutable() is null)
            throw new IOException("Комплектный F3D не найден в сборке PixelThumbs.");
        var package = PackageFiles.Concat(EnumerateFiles(renderer).Select(file => Path.GetRelativePath(source, file)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => (Name: name, Hash: FileHash(Path.Combine(source, name)))).ToArray();
        foreach (var item in package)
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(item.Name + '\0'));
            hasher.AppendData(item.Hash);
        }

        string root = protectedRoot ?? ProtectedRoot;
        Directory.CreateDirectory(root);
        CheckDirectory(root);
        string version = Convert.ToHexString(hasher.GetHashAndReset().AsSpan(0, 12));
        string target = Path.Combine(root, version);
        if (Directory.Exists(target))
        {
            CheckDirectory(target);
            ValidatePackage(target, package);
            return target;
        }

        string stage = Path.Combine(root, "setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var item in package)
            {
                string file = Path.Combine(stage, item.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.Copy(Path.Combine(source, item.Name), file);
            }
            ValidatePackage(stage, package);
            try { Directory.Move(stage, target); }
            catch (IOException) when (Directory.Exists(target))
            {
                CheckDirectory(target);
                ValidatePackage(target, package);
            }
        }
        finally
        {
            if (Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
        return target;
    }

    private static void CheckDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Папка установки PixelThumbs перенаправлена.");
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        CheckDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(directory))
            yield return file;
        foreach (string child in Directory.EnumerateDirectories(directory))
            foreach (string file in EnumerateFiles(child))
                yield return file;
    }

    private static byte[] FileHash(string file)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Файл поставки PixelThumbs перенаправлен.");
        using var stream = File.OpenRead(file);
        return SHA256.HashData(stream);
    }

    private static void ValidatePackage(string directory, (string Name, byte[] Hash)[] package)
    {
        string renderer = Path.Combine(directory, BundledF3d.DirectoryName);
        int bundledFiles = EnumerateFiles(renderer).Count();
        if (bundledFiles != package.Length - PackageFiles.Length)
            throw new IOException("Системная копия F3D не совпадает со сборкой.");
        foreach (var item in package)
        {
            string file = Path.Combine(directory, item.Name);
            if (!FileHash(file).AsSpan().SequenceEqual(item.Hash))
                throw new IOException("Системная копия PixelThumbs не совпадает со сборкой.");
        }
    }
}
