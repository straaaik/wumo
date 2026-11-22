using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Principal;
using Microsoft.Win32;

internal static class BaselineRegistration
{
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegOverridePredefKey(nint predefined, nint replacement);

    internal static void ReproducePartialWrite(string baselineDirectory, RegistryKey scratch)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run the baseline registry reproduction without elevation.");

        var context = new AssemblyLoadContext("BaselineRegistration", isCollectible: true);
        baselineDirectory = Path.GetFullPath(baselineDirectory);
        context.Resolving += (_, name) =>
        {
            string path = Path.Combine(baselineDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        Assembly app = context.LoadFromAssemblyPath(Path.Combine(baselineDirectory, "PixelThumbs.dll"));
        Assembly core = context.LoadFromAssemblyPath(Path.Combine(baselineDirectory, "PixelThumbs.Core.dll"));
        Type settingsType = core.GetType("PixelThumbs.AppSettings", throwOnError: true)!;
        Type formatType = core.GetType("PixelThumbs.FormatSettings", throwOnError: true)!;
        object settings = Activator.CreateInstance(settingsType)!;
        var formats = (IDictionary)settingsType.GetProperty("Formats")!.GetValue(settings)!;
        foreach (string extension in new[] { ".stl", ".step", ".stp", ".iges", ".igs", ".fbx" })
            formats.Add(extension, Activator.CreateInstance(formatType)!);

        using var temporarySettings = new TemporarySettings();
        string marker = "RedirectedProfile-" + Guid.NewGuid().ToString("N");
        scratch.SetValue(marker, marker);
        nint currentUser = unchecked((int)0x80000001);
        Check(0 == RegOverridePredefKey(currentUser, scratch.Handle.DangerousGetHandle()), "Cannot redirect HKCU.");
        try
        {
            using var redirected = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            Check(Equals(redirected.GetValue(marker), marker), "HKCU redirection is unverified.");
            var registration = app.GetType("PixelThumbs.ShellRegistration", throwOnError: true)!;
            var apply = registration.GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!;
            bool missingFormat = false;
            try { apply.Invoke(null, [settings, false]); }
            catch (TargetInvocationException error) when (error.InnerException is KeyNotFoundException)
            {
                missingFormat = true;
            }
            Check(missingFormat, "Expected the baseline to fail at the missing .3mf settings.");
            const string handler = @"Software\Classes\.stl\ShellEx\{E357FCCD-A995-4576-B01F-234630154E96}";
            using var stl = scratch.OpenSubKey(handler);
            Check(stl?.GetValue("") is "{CDB49CA7-2710-4EEA-94E0-D9F361B83801}",
                "Expected the first changed handler to survive the failed baseline apply.");
            Console.WriteLine("PASS reproduced the baseline applying the first format before a later failure.");
        }
        finally
        {
            Check(0 == RegOverridePredefKey(currentUser, 0), "Cannot restore HKCU mapping.");
            context.Unload();
        }
    }

    private static void Check(bool success, string reason)
    {
        if (!success)
            throw new InvalidOperationException(reason);
    }
}
