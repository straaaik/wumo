namespace PixelThumbs;

public static class BundledF3d
{
    public const string DirectoryName = "f3d";

    public static string? FindExecutable()
    {
        string folder = Path.GetDirectoryName(typeof(BundledF3d).Assembly.Location)!;
        string renderer = Path.Combine(folder, DirectoryName);
        string bin = Path.Combine(renderer, "bin");
        string executable = Path.Combine(bin, "f3d.exe");
        try
        {
            foreach (string directory in new[] { folder, renderer, bin })
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return null;
            foreach (string name in new[] { "f3d.exe", "f3d.dll", "osmesa.dll" })
                if ((File.GetAttributes(Path.Combine(bin, name)) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return null;
            return executable;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
