using System.Drawing;
using System.Reflection;
using Microsoft.Win32;
using PixelThumbs;
using static PixelThumbs.RegistryTransaction;

if (args is ["--check-thumbnail-cache"])
{
    ulong bytes = ThumbnailCacheReset.Run(purge: false);
    Console.WriteLine($"PASS native Windows thumbnail category initialized and reported {bytes:N0} cache bytes.");
    return;
}

if (args is ["--check-package", var models])
{
    PackageVerification.Run(models);
    return;
}

if (args is ["--verify-comhost", var comPackage, .. var files])
{
    PackageVerification.VerifyComPackage(comPackage, files);
    return;
}

const string classes = @"Software\Classes";
const string stl = classes + @"\.stl";
const string step = classes + @"\.step";
const string sharedStl = classes + @"\SystemFileAssociations\.stl";
const string previous = @"Software\PixelThumbs\PreviousThumbnailHandlers";
const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
const string application = classes + @"\OrcaSlicer.STL";
const string choice = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.stl\UserChoice";
const string thirdParty = "{00000000-BBED-49D6-92B1-000000000042}";
const string legacyStl = "{CDB49CA7-2710-4EEA-94E0-D9F361B83801}";
const string directory = @"C:\Example\PixelThumbs";
const string sidA = "S-1-5-21-111-1";
const string sidB = "S-1-5-21-111-2";
const string newOverlay = "";
string stlHandler = Handler(stl), sharedHandler = Handler(sharedStl);
string stlId = "{" + FormatCatalog.StlId + "}";
string rootPath = @"Software\PixelThumbs.Registration.Tests-" + Guid.NewGuid().ToString("N");
using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
try
{
    using var root = currentUser.CreateSubKey(rootPath);
    using (var startup = root.CreateSubKey("StartupMigration"))
    {
        var currentBuild = new InstalledBuild(200, @"C:\Example\Updated PixelThumbs");
        Require(!ShellRegistration.RefreshStartupPath(startup, currentBuild), "Migration never enables a missing startup entry.");
        Put(startup, run, "PixelThumbs", "\"C:\\Example\\Old PixelThumbs\\PixelThumbs.exe\" --startup");
        Require(ShellRegistration.RefreshStartupPath(startup, currentBuild), "Migration updates a legacy startup path.");
        Require(Read(startup, run, "PixelThumbs")?.Data as string == $"\"{currentBuild.Executable}\" --startup",
            "Migration points startup at the current executable.");
        Require(!ShellRegistration.RefreshStartupPath(startup, currentBuild), "Startup path repair is idempotent.");
        Put(startup, run, "PixelThumbs", "\"C:\\Example\\OtherApp.exe\" --startup");
        Require(!ShellRegistration.RefreshStartupPath(startup, currentBuild), "Migration leaves other executables untouched.");
    }
    if (args is [var baseline])
    {
        using var baselineRegistry = root.CreateSubKey("Baseline");
        ReproduceBadge(Path.Combine(baseline, "PixelThumbs.Core.dll"));
        BaselineRegistration.ReproducePartialWrite(baseline, baselineRegistry);
    }
    using var user = root.CreateSubKey("User");
    using var otherUser = root.CreateSubKey("OtherUser");
    using var machine = root.CreateSubKey("Machine");
    using var settings = new TemporarySettings();
    File.WriteAllText(settings.Path, """{"AutoStart":true}""");
    Require(SettingsStore.Load().ThumbnailsWidgetEnabled, "Older settings leave 3D Thumbnails enabled.");
    File.WriteAllText(settings.Path, """{"Formats":{".STL":{"Enabled":false,"Style":{"Yaw":999}},".unknown":{"Enabled":true}}}""");
    var savedFormats = SettingsStore.Load().Formats;
    Require(savedFormats[".stl"] is { Enabled: false, Style.Yaw: 360 } && !savedFormats.ContainsKey(".unknown"),
        "Existing format preferences are recognized regardless of extension casing.");
    var enabled = new AppSettings
    {
        AutoStart = true,
        Formats = new(StringComparer.OrdinalIgnoreCase)
        {
            [".stl"] = new(),
            [".step"] = new()
        }
    };

    Put(user, stl, "", "OrcaSlicer.STL");
    Put(user, choice, "ProgId", "OrcaSlicer.STL");
    Put(user, choice, "Hash", "example-of-an-existing-user-choice");
    Put(user, application + @"\shell\open\command", "", "\"C:\\OrcaSlicer\\OrcaSlicer.exe\" \"%1\"");
    Put(user, stlHandler, "", thirdParty, RegistryValueKind.ExpandString);
    Put(user, sharedHandler, "", legacyStl);
    Put(user, previous, sharedHandler, "{00000000-CAFE-4D81-9706-000000000042}");
    Put(user, sharedStl, "TypeOverlay", "custom.dll,-7");
    Put(user, run, "AnotherApp", "another.exe");
    Put(machine, ClassPath(legacyStl), "", Path.Combine(directory, "PixelThumbs.Shell.comhost.dll"));
    Put(machine, ClassPath(legacyStl), "ThreadingModel", "Both");
    string[] userBefore = Snapshot(user), machineBefore = Snapshot(machine);
    string settingsBefore = File.ReadAllText(settings.Path);

    bool partial = false;
    ExpectIOException(() => ShellRegistration.ApplyRegistry(user, machine, sidA, enabled, directory, true,
        () => throw new Exception("Settings save must follow all registry changes."), (_, _) =>
        {
            partial = Read(user, stlHandler)?.Data is string installed &&
                installed.Equals(stlId, StringComparison.OrdinalIgnoreCase);
            throw new IOException("Injected registry failure after updating a handler.");
        }));
    Require(partial, "The injected registry failure followed a real partial write.");
    Require(userBefore.SequenceEqual(Snapshot(user)), "HKCU fully restored after a partial registry failure.");
    Require(machineBefore.SequenceEqual(Snapshot(machine)), "HKLM fully restored after a partial registry failure.");
    Require(settingsBefore == File.ReadAllText(settings.Path), "Settings unchanged after registry failure.");

    ExpectIOException(() => Apply(user, machine, sidA, enabled, true,
        () => throw new IOException("Injected settings save failure.")));
    Require(userBefore.SequenceEqual(Snapshot(user)), "HKCU fully restored after a settings save failure.");
    Require(machineBefore.SequenceEqual(Snapshot(machine)), "HKLM fully restored after legacy class deletion.");
    Require(settingsBefore == File.ReadAllText(settings.Path), "Settings unchanged after failed save.");

    Require(Apply(user, null, sidA, enabled, false, () => SettingsStore.Save(enabled)), "Retry installs the user provider.");
    Require(SettingsStore.Load().AutoStart, "Successful retry saves the settings.");
    Require(Read(user, ClassPath(stlId)) is not null, "User COM class registered.");
    Require(Read(user, Handler(step)) is null, "Renderer-dependent formats require the bundled renderer.");
    Require(Read(user, stl, "")?.Data is "OrcaSlicer.STL", "File association default preserved.");
    Require(Read(user, choice, "ProgId")?.Data is "OrcaSlicer.STL", "Default application selection preserved.");
    Require(Read(user, application + @"\shell\open\command")?.Data is "\"C:\\OrcaSlicer\\OrcaSlicer.exe\" \"%1\"",
        "OrcaSlicer's open command preserved.");
    Require(Read(user, sharedStl, "TypeOverlay")?.Data is newOverlay, "Extension thumbnail overlay suppressed.");
    Require(!Apply(user, null, sidA, enabled, false), "Applying unchanged settings does not report Shell changes.");
    var widgetOff = enabled with { ThumbnailsWidgetEnabled = false, QuickPreviewSize = 768 };
    Require(Apply(user, null, sidA, widgetOff, true, () => SettingsStore.Save(widgetOff)),
        "Switching off the widget removes its thumbnail integration.");
    var savedOff = SettingsStore.Load();
    Require(!savedOff.ThumbnailsWidgetEnabled && savedOff.QuickPreview && savedOff.QuickPreviewSize == 768 &&
        savedOff.Formats[".stl"].Enabled, "Disabling the widget preserves its saved preferences.");
    Require(Read(user, stlHandler) == new Value(thirdParty, RegistryValueKind.ExpandString) &&
        Read(user, ClassPath(stlId)) is null, "Turning off the widget restores the previous Explorer handler.");
    Require(Read(user, run, "PixelThumbs")?.Data is string, "Wumo can keep starting with Windows while the widget is off.");
    Require(Apply(user, null, sidA, enabled, false, () => SettingsStore.Save(enabled)) && SettingsStore.Load().ThumbnailsWidgetEnabled,
        "Turning the widget back on restores its thumbnail integration.");
    Require(Apply(user, null, sidA, null, false), "User-only unregister removes the provider.");
    Require(Read(user, stlHandler) == new Value(thirdParty, RegistryValueKind.ExpandString),
        "Previous third-party handler and registry type restored.");
    Require(Read(user, sharedHandler)?.Data is "{00000000-CAFE-4D81-9706-000000000042}",
        "Legacy backup survives an upgrade and is restored.");
    Require(Read(user, sharedStl, "TypeOverlay")?.Data is "custom.dll,-7", "Previous overlay restored.");
    Require(Read(user, run, "AnotherApp")?.Data is "another.exe" && Read(user, run, "PixelThumbs") is null,
        "Unregister removes only PixelThumbs autostart.");
    Require(Read(user, ClassPath(stlId)) is null, "User COM class removed.");

    using (var setup = root.CreateSubKey("SetupUnregistration"))
    {
        Put(setup, stlHandler, "", thirdParty);
        Require(Apply(setup, null, sidA, enabled, true), "The installer copy registers user providers.");
        string otherDirectory = @"C:\Example\Other Wumo";
        string stepId = "{" + FormatCatalog.StepId + "}";
        const string stepCommand = classes + @"\SystemFileAssociations\.step\shell\PixelThumbs.Refresh\command";
        string otherExecutable = Path.Combine(otherDirectory, "PixelThumbs.exe");
        Put(setup, ClassPath(stepId), "", Path.Combine(otherDirectory, "PixelThumbs.Shell.comhost.dll"));
        string otherStartup = $"\"{otherExecutable}\" --startup";
        string otherRefresh = $"\"{otherExecutable}\" --refresh \"%1\"";
        Put(setup, run, "PixelThumbs", otherStartup);
        Put(setup, stepCommand, "", otherRefresh);
        Require(ShellRegistration.ApplyRegistry(setup, null, sidA, null, directory, false, static () => { },
            static (_, _) => false, currentCopyOnly: true), "Removing the installer copy unregisters its provider.");
        Require(Read(setup, ClassPath(stlId)) is null && Read(setup, stlHandler)?.Data is thirdParty,
            "Installer removal restores the previous thumbnail handler.");
        Require(Read(setup, ClassPath(stepId)) is not null && Read(setup, Handler(step)) is not null &&
            Equals(Read(setup, run, "PixelThumbs")?.Data, otherStartup) && Equals(Read(setup, stepCommand)?.Data, otherRefresh),
            "Installer removal preserves another Wumo copy.");
        Require(!ShellRegistration.ApplyRegistry(setup, null, sidA, null, directory, false, static () => { },
            static (_, _) => false, currentCopyOnly: true), "Installer removal can be retried safely.");
    }

    Require(Apply(user, machine, sidA, enabled, true, legacyInUse: true), "Machine install migrated the active profile.");
    Require(Apply(otherUser, machine, sidB, enabled, true, legacyInUse: true), "Second profile joined machine registration.");
    Require(Read(machine, ClassPath(stlId)) is not null && Read(user, ClassPath(stlId)) is null,
        "Machine registration points at the shared COM class.");
    Require(Read(user, Handler(step)) is not null, "Bundled renderer formats are registered.");
    Require(Apply(user, machine, sidA, null, false, legacyInUse: true), "First machine profile unregistered.");
    Require(Read(machine, ClassPath(stlId)) is not null && Read(otherUser, stlHandler) is not null,
        "Shared class survives unregister from another profile.");
    Require(Apply(otherUser, machine, sidB, null, false, legacyInUse: true), "Last tracked machine profile unregistered.");
    Require(Read(machine, ClassPath(stlId)) is null, "Shared class removed after the final profile.");
    Require(Read(machine, ClassPath(legacyStl)) is not null, "Untracked legacy class preserved for an unavailable profile.");
    Require(Apply(user, machine, sidA, null, false), "An unreferenced legacy class is eligible for cleanup.");
    Require(Read(machine, ClassPath(legacyStl)) is null, "Unreferenced legacy class was removed.");
    Console.WriteLine("PASS registration rollback, retry, migration, association preservation and per-profile unregister.");
}
finally
{
    currentUser.DeleteSubKeyTree(rootPath, throwOnMissingSubKey: false);
}

static string Handler(string path) => path + @"\ShellEx\" + FormatCatalog.HandlerCategory;
static string ClassPath(string id) => $@"Software\Classes\CLSID\{id}\InprocServer32";

static bool Apply(RegistryKey user, RegistryKey? machine, string sid, AppSettings? settings, bool hasF3d,
    Action? save = null, bool legacyInUse = false) =>
    ShellRegistration.ApplyRegistry(user, machine, sid, settings, directory, hasF3d, save ?? (() => { }),
        (_, _) => legacyInUse);

static void Put(RegistryKey root, string key, string name, object value, RegistryValueKind type = RegistryValueKind.String)
{
    using var open = root.CreateSubKey(key);
    open.SetValue(name, value, type);
}

static string[] Snapshot(RegistryKey root)
{
    List<string> result = [];
    Capture(root, "");
    result.Sort(StringComparer.OrdinalIgnoreCase);
    return result.ToArray();

    void Capture(RegistryKey key, string path)
    {
        result.Add(path.ToUpperInvariant());
        foreach (string name in key.GetValueNames())
            result.Add($"{path.ToUpperInvariant()} [{name.ToUpperInvariant()}] {key.GetValueKind(name)} = " +
                key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        foreach (string name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name)!;
            Capture(child, path + @"\" + name);
        }
    }
}

static void ExpectIOException(Action action)
{
    try { action(); }
    catch (IOException) { return; }
    throw new Exception("Expected injected IOException.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static void ReproduceBadge(string baselineCore)
{
    using var settings = new TemporarySettings();
    string badges = System.IO.Path.Combine(SettingsStore.DataDirectory, "badges");
    Directory.CreateDirectory(badges);
    using var source = new Bitmap(16, 16);
    using (var drawing = Graphics.FromImage(source))
        drawing.Clear(Color.Red);
    source.Save(System.IO.Path.Combine(badges, "stl.png"), System.Drawing.Imaging.ImageFormat.Png);
    using var thumbnail = new Bitmap(64, 64);
    using (var drawing = Graphics.FromImage(thumbnail))
        drawing.Clear(Color.Black);
    var baseline = Assembly.LoadFile(System.IO.Path.GetFullPath(baselineCore));
    var badge = baseline.GetType("PixelThumbs.ApplicationBadge", throwOnError: true)!;
    badge.GetMethod("DrawIfNeeded")!.Invoke(null, [thumbnail, ".stl"]);
    Require(thumbnail.GetPixel(55, 55).ToArgb() == Color.Red.ToArgb(),
        "Baseline paints the associated app's cached icon into the model thumbnail.");
    Require(typeof(SettingsStore).Assembly.GetType("PixelThumbs.ApplicationBadge") is null,
        "Current build excludes the code that paints an app icon.");
    Console.WriteLine("PASS reproduced the baked-in STL badge in the baseline.");
}

sealed class TemporarySettings : IDisposable
{
    private const string Variable = "PIXELTHUMBS_DATA_DIR";
    private readonly string? previous = Environment.GetEnvironmentVariable(Variable);
    private readonly string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "PixelThumbs.Registration.Tests-" + Guid.NewGuid().ToString("N"));

    internal string Path => System.IO.Path.Combine(folder, "settings.json");

    internal TemporarySettings()
    {
        Environment.SetEnvironmentVariable(Variable, folder);
        SettingsStore.Save(new AppSettings());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, previous);
        string badges = System.IO.Path.Combine(folder, "badges");
        if (Directory.Exists(badges))
        {
            foreach (string name in new[] { "stl.png", "step.png", "stp.png" })
                File.Delete(System.IO.Path.Combine(badges, name));
            Directory.Delete(badges);
        }
        File.Delete(Path);
        Directory.Delete(folder);
    }
}
