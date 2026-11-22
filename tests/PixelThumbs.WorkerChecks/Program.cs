using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PixelThumbs;

return new Checks().Run(args);

sealed class Checks
{
    private const string Triangle = "v 0 0 0\nv 2 0 0\nv 0 2 0\nf 1 2 3\n";
    private static readonly ThumbnailStyle Style = new();
    private int failures;

    internal int Run(string[] args)
    {
        if (args is ["--memory-probe"])
            return ProbeMemory();
        if (args is ["--benchmark" or "--benchmark-desktop", .. var files])
        {
            foreach (string file in files)
                Check(Path.GetFileName(file), () => Benchmark(file, desktop: args[0] == "--benchmark-desktop"));
            return failures == 0 ? 0 : 1;
        }

        Check("Built-in OBJ", () =>
        {
            using var input = Text(Triangle);
            using var image = Render(input, ".obj", RenderFailure.None);
            Require(image is { Width: 512, Height: 512 }, "Rendered dimensions differ.");
        });
        Check("Invalid mesh", () =>
        {
            using var input = Text("not a mesh");
            using var image = Render(input, ".obj", RenderFailure.ReadError);
            Require(image is null, "Corrupt mesh returned an image.");
        });
        Check("Missing embedded preview", () =>
        {
            using var input = Text("G28\nG0 X1 Y1\n");
            using var image = Render(input, ".gcode", RenderFailure.NoEmbeddedImage);
            Require(image is null, "G-code without an embedded preview returned an image.");
        });
        Check("Failed job attachment never reads the model", () =>
        {
            using var input = new ObservedInput(TimeSpan.Zero);
            int pid = 0;
            RenderDiagnostics? report = null;
            using var image = WorkerClient.RenderIsolated(input, ".obj", 512, Style, default, result => report = result,
                process => { pid = process.Id; return null; });
            Require(image is null && report?.Failure == RenderFailure.Isolation && input.Reads == 0,
                $"Expected isolation failure before input, got {report} and {input.Reads} reads.");
            RequireStopped(pid);
        });
        Check("Worker timeout kills process", () => CheckInterruption(cancel: false));
        Check("Worker cancellation kills process", () => CheckInterruption(cancel: true));
        Check("Native job committed-memory limit", CheckMemory);
        if (args is [var models])
        {
            foreach (string name in new[] { "cheese.stp", "cheese.step", "cheese.igs", "cheese.iges", "phong_cube.fbx" })
                Check(name, () =>
                {
                    using var input = File.OpenRead(Path.Combine(models, name));
                    using var image = Render(input, Path.GetExtension(name), RenderFailure.None);
                    Require(image is { Width: 512, Height: 512 }, "The bundled renderer did not produce a 512 px image.");
                    Require(Enumerable.Range(180, 150).Any(x => image!.GetPixel(x, 256).A != 0), "The center of the render is empty.");
                });
            Check("Corrupt STEP", () =>
            {
                using var input = File.OpenRead(Path.Combine(models, "invalid.step"));
                using var image = Render(input, ".step", RenderFailure.ExternalRenderer);
                Require(image is null, "Corrupt STEP returned an image.");
            });
            Check("Corrupt STEP with desktop backend selection", () =>
            {
                using var input = File.OpenRead(Path.Combine(models, "invalid.step"));
                RenderDiagnostics? report = null;
                using var image = WorkerClient.RenderForDesktop(input, ".step", 512, Style, diagnostics: result => report = result);
                Require(image is null && report?.Failure == RenderFailure.ExternalRenderer,
                    $"Corrupt STEP did not report a desktop renderer error: {report}.");
            });
            Check("Cancellation during F3D rendering", () => CheckExternalCancellation(Path.Combine(models, "cheese.stp")));
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Benchmark(string file, bool desktop)
    {
        const int rounds = 5;
        var elapsed = new double[rounds];
        var peak = new double[rounds];
        for (int i = 0; i < rounds; i++)
        {
            using var input = File.OpenRead(file);
            RenderDiagnostics? report = null;
            var stopwatch = Stopwatch.StartNew();
            string extension = Path.GetExtension(file);
            using var image = desktop
                ? WorkerClient.RenderForDesktop(input, extension, 512, Style, diagnostics: result => report = result)
                : WorkerClient.Render(input, extension, 512, Style, diagnostics: result => report = result);
            elapsed[i] = stopwatch.Elapsed.TotalMilliseconds;
            Require(image is not null && report?.Failure == RenderFailure.None, $"Render failed: {report}.");
            peak[i] = report!.PeakMemory / 1048576d;
        }
        Array.Sort(elapsed);
        Array.Sort(peak);
        Console.WriteLine($"{rounds} renders at 512 px: median {elapsed[rounds / 2]:F1} ms, " +
            $"median peak committed memory {peak[rounds / 2]:F1} MiB.");
    }

    private void Check(string name, Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            action();
            Console.WriteLine($"PASS {name} ({stopwatch.Elapsed.TotalMilliseconds:F0} ms)");
        }
        catch (Exception error)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {name}: {error.Message}");
        }
    }

    private static Bitmap? Render(Stream input, string extension, RenderFailure expected)
    {
        RenderDiagnostics? report = null;
        var stopwatch = Stopwatch.StartNew();
        var image = WorkerClient.Render(input, extension, 512, Style, diagnostics: result => report = result);
        Console.WriteLine($"{extension} {report?.Failure} startup={report?.Startup.TotalMilliseconds:F1}ms "
            + $"read={report?.Read.TotalMilliseconds:F1}ms render={report?.Render.TotalMilliseconds:F1}ms "
            + $"peak={report?.PeakMemory / 1048576d:F1}MiB total={stopwatch.Elapsed.TotalMilliseconds:F1}ms");
        if (report?.Failure != expected)
        {
            image?.Dispose();
            throw new InvalidOperationException($"Expected {expected}, got {report}.");
        }
        return image;
    }

    private static void CheckInterruption(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new ObservedInput(TimeSpan.FromMilliseconds(300));
        int pid = 0;
        RenderDiagnostics? report = null;
        using var image = WorkerClient.RenderIsolated(input, ".obj", 512, Style, cancellation.Token,
            result => report = result, process =>
            {
                pid = process.Id;
                var job = RenderJob.Attach(process);
                if (cancel)
                    cancellation.CancelAfter(75);
                return job;
            }, cancel ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(175));
        var expected = cancel ? RenderFailure.Cancelled : RenderFailure.Timeout;
        Require(image is null && report?.Failure == expected, $"Expected {expected}, got {report}.");
        RequireStopped(pid);
    }

    private static void CheckExternalCancellation(string path)
    {
        string executable = BundledF3d.FindExecutable() ?? throw new InvalidOperationException("No bundled renderer.");
        var before = Process.GetProcessesByName("f3d").Select(process =>
        {
            using (process)
                return process.Id;
        }).ToHashSet();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var observer = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                int pid = FindNewProcess(executable, before);
                if (pid != 0)
                {
                    cancellation.Cancel();
                    return pid;
                }
                await Task.Delay(5);
            }
            return 0;
        });
        using var input = File.OpenRead(path);
        int worker = 0;
        RenderDiagnostics? report = null;
        try
        {
            using var image = WorkerClient.RenderIsolated(input, ".stp", 512, Style, cancellation.Token,
                result => report = result, process => { worker = process.Id; return RenderJob.Attach(process); });
        }
        finally { cancellation.Cancel(); }
        int renderer = observer.GetAwaiter().GetResult();
        Require(renderer != 0 && report?.Failure == RenderFailure.Cancelled, $"F3D cancellation was not observed: {report}.");
        RequireStopped(worker);
        RequireStopped(renderer);
    }

    private static int FindNewProcess(string executable, HashSet<int> before)
    {
        int found = 0;
        foreach (var process in Process.GetProcessesByName("f3d"))
        {
            using (process)
            {
                if (before.Contains(process.Id))
                    continue;
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        found = process.Id;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return found;
    }

    private static void CheckMemory()
    {
        var command = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        command.ArgumentList.Add("--memory-probe");
        using var process = Process.Start(command)!;
        using var job = RenderJob.Attach(process);
        Require(job is not null, "Could not assign the memory probe to a job.");
        process.StandardInput.WriteLine("start");
        process.StandardInput.Close();
        Require(process.WaitForExit(8000), "The memory probe did not terminate.");
        Require(process.ExitCode == 0, $"Memory probe failed with {process.ExitCode}.");
    }

    private static int ProbeMemory()
    {
        if (Console.ReadLine() != "start" || !RenderJob.IsMemoryLimited())
            return 1;
        const int chunk = 32 * 1024 * 1024;
        var blocks = new List<nint>();
        try
        {
            while (blocks.Count * chunk < RenderJob.ProcessMemoryLimit)
            {
                nint address = VirtualAlloc(0, chunk, 0x3000, 4);
                if (address == 0)
                    return blocks.Count >= 16 ? 0 : 2;
                blocks.Add(address);
            }
            return 3;
        }
        finally
        {
            foreach (nint address in blocks)
                VirtualFree(address, 0, 0x8000);
        }
    }

    private static void RequireStopped(int pid)
    {
        Require(pid > 0, "No child process was started.");
        try
        {
            using var process = Process.GetProcessById(pid);
            Require(process.WaitForExit(1500), $"Child process {pid} survived.");
        }
        catch (ArgumentException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static MemoryStream Text(string value) => new(Encoding.UTF8.GetBytes(value));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);

    private sealed class ObservedInput(TimeSpan delay) : MemoryStream(Encoding.UTF8.GetBytes(Triangle))
    {
        internal int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            Thread.Sleep(delay);
            return base.Read(buffer, offset, count);
        }
    }
}
