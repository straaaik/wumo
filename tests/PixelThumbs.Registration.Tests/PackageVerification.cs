using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PixelThumbs;

internal static class PackageVerification
{
    internal static void Run(string models)
    {
        string source = AppContext.BaseDirectory;
        using var profile = new TemporarySettings();
        File.WriteAllText(profile.Path, """
            {
              "F3dExecutable": "Z:\\legacy\\f3d.exe",
              "AutoStart": true,
              "QuickPreviewKey": 66,
              "Formats": { ".stl": { "Enabled": true }, ".step": { "Enabled": true } }
            }
            """);
        var settings = SettingsStore.Load();
        Require(settings.AutoStart && settings.QuickPreviewKey == 66 && settings.Formats[".step"].Enabled,
            "The legacy profile lost unrelated settings.");

        var directory = Directory.CreateTempSubdirectory("PixelThumbs.PackageChecks-");
        try
        {
            string root = Path.Combine(directory.FullName, "ProgramFiles", "PixelThumbs");
            string systemCopy = MachineInstallation.EnsureProtectedCopy(source, root);
            Require(systemCopy == MachineInstallation.EnsureProtectedCopy(source, root), "The protected copy was not reused.");
            string runtimeFile = Path.Combine("f3d", "bin", "f3d.exe");
            Require(File.Exists(Path.Combine(systemCopy, runtimeFile)), "The protected copy omitted F3D.");

            string stl = Path.Combine(directory.FullName, "triangle.stl");
            File.WriteAllText(stl, """
                solid triangle
                facet normal 0 0 1
                outer loop
                vertex 0 0 0
                vertex 2 0 0
                vertex 0 2 0
                endloop
                endfacet
                endsolid triangle
                """);
            var cad = new List<string> { stl };
            cad.AddRange(new[] { "cheese.stp", "cheese.step", "cheese.igs", "cheese.iges", "phong_cube.fbx" }
                .Select(name => Path.Combine(models, name)));
            RunComPackage(source, cad);
            RunComPackage(systemCopy, cad);
            SettingsStore.Save(settings);
            Require(!File.ReadAllText(profile.Path).Contains("F3dExecutable", StringComparison.Ordinal),
                "The obsolete renderer path is still persisted.");
            Console.WriteLine("PASS COM host, protected package contents and legacy profile migration.");
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void RunComPackage(string directory, List<string> models)
    {
        var command = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        command.ArgumentList.Add("--verify-comhost");
        command.ArgumentList.Add(directory);
        foreach (string model in models)
            command.ArgumentList.Add(model);
        using var process = Process.Start(command)!;
        if (!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The thumbnail COM host run timed out.");
        }
        Require(process.ExitCode == 0, $"The thumbnail COM host run failed ({process.ExitCode}).");
    }

    internal static void VerifyComPackage(string directory, string[] models)
    {
        var library = NativeLibrary.Load(Path.Combine(directory, "PixelThumbs.Shell.comhost.dll"));
        // Keep the native COM host loaded until this test process exits.
        var factoryMethod = Marshal.GetDelegateForFunctionPointer<DllGetClassObject>(
            NativeLibrary.GetExport(library, "DllGetClassObject"));
        foreach (string model in models)
        {
            var classId = new Guid(FormatCatalog.Entries.Single(format => format.Extension == Path.GetExtension(model)).Id);
            var factoryId = new Guid("00000001-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(factoryMethod(ref classId, ref factoryId, out nint factory));
            nint provider = 0, initializer = 0, stream = 0;
            nint thumbnail = 0;
            try
            {
                var iid = new Guid(FormatCatalog.HandlerCategory);
                var initializerId = new Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F");
                Marshal.ThrowExceptionForHR(GetMethod<CreateInstance>(factory, 3)(factory, 0, ref iid, out provider));
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(provider, in initializerId, out initializer));
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(model, 0x40, 0, false, 0, out stream));
                Marshal.ThrowExceptionForHR(GetMethod<Initialize>(initializer, 3)(initializer, stream, 0));
                Marshal.ThrowExceptionForHR(GetMethod<GetThumbnail>(provider, 3)(provider, 512, out thumbnail, out uint alpha));
                Require(thumbnail != 0 && alpha == 2, $"No ARGB thumbnail for {model}.");
                using var image = Image.FromHbitmap(thumbnail);
                Require(image.Width == 512 && image.Height == 512, $"Wrong thumbnail size for {model}.");
                Console.WriteLine($"PASS {Path.GetFileName(directory.TrimEnd('\\'))}: {Path.GetExtension(model)} thumbnail.");
            }
            finally
            {
                if (thumbnail != 0)
                    DeleteObject(thumbnail);
                if (stream != 0)
                    Marshal.Release(stream);
                if (initializer != 0)
                    Marshal.Release(initializer);
                if (provider != 0)
                    Marshal.Release(provider);
                Marshal.Release(factory);
            }
        }
    }

    private static T GetMethod<T>(nint instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObject(ref Guid clsid, ref Guid iid, out nint factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateInstance(nint self, nint outer, ref Guid iid, out nint instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Initialize(nint self, nint stream, uint mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetThumbnail(nint self, uint size, out nint bitmap, out uint alpha);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, nint template, out nint stream);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
