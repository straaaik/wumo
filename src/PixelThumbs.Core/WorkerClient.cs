using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PixelThumbs;

public static partial class WorkerClient
{
    private static readonly SemaphoreSlim Queue = new(1, 1);
    private const int MaximumPngBytes = 4 * 1024 * 1024;
    private const int StartRendering = 0x51;
    private const string InputLengthVariable = "PIXELTHUMBS_RENDER_LENGTH";
    private const string DesktopBackendVariable = "PIXELTHUMBS_DESKTOP_RENDER";

    public static Bitmap? Render(Stream source, string extension, int size, ThumbnailStyle style,
        CancellationToken cancellation = default, Action<RenderDiagnostics>? diagnostics = null) =>
        RenderIsolated(source, extension, size, style, cancellation, diagnostics, RenderJob.Attach);

    public static Bitmap? RenderForDesktop(Stream source, string extension, int size, ThumbnailStyle style,
        CancellationToken cancellation = default, Action<RenderDiagnostics>? diagnostics = null) =>
        RenderIsolated(source, extension, size, style, cancellation, diagnostics, RenderJob.Attach, desktopBackend: true);

    internal static Bitmap? RenderIsolated(Stream source, string extension, int size, ThumbnailStyle style,
        CancellationToken cancellation, Action<RenderDiagnostics>? diagnostics, Func<Process, SafeFileHandle?> attach,
        TimeSpan? timeout = null, bool desktopBackend = false)
    {
        var report = new RenderDiagnostics(RenderFailure.ReadError);
        bool entered = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            extension = extension.ToLowerInvariant();
            if (!source.CanSeek || !FormatCatalog.IsSupported(extension))
                return null;

            long length = source.Length;
            if (length <= 0)
                return null;
            if (length > RenderPipeline.MaximumInputFor(extension))
            {
                report = new(RenderFailure.InputLimit);
                return null;
            }
            bool external = FormatCatalog.RequiresExternalRenderer(extension);
            if (external && BundledF3d.FindExecutable() is null)
            {
                report = new(RenderFailure.ExternalRenderer);
                return null;
            }

            if (!Queue.Wait(TimeSpan.FromSeconds(6), cancellation))
            {
                report = new(RenderFailure.Timeout);
                return null;
            }
            entered = true;
            var budget = timeout ?? TimeSpan.FromSeconds(external || length > 16 * 1024 * 1024 ? 15 : 5);
            return Run(source, length, extension, Math.Clamp(size, 16, 512), style, external, desktopBackend, budget, cancellation, attach,
                out report);
        }
        catch (OperationCanceledException)
        {
            report = report with { Failure = RenderFailure.Cancelled };
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException or ArgumentException or NotSupportedException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (entered) Queue.Release();
            Trace.TraceInformation("PixelThumbs {0}: {1}; startup {2:F1} ms, read {3:F1} ms, render {4:F1} ms, peak job {5}",
                extension, report.Failure, report.Startup.TotalMilliseconds, report.Read.TotalMilliseconds,
                report.Render.TotalMilliseconds, report.PeakMemory);
            diagnostics?.Invoke(report);
        }
    }

    private static Bitmap? Run(Stream source, long sourceLength, string extension, int size, ThumbnailStyle style, bool external,
        bool desktopBackend,
        TimeSpan budget, CancellationToken externalCancellation, Func<Process, SafeFileHandle?> attach,
        out RenderDiagnostics report)
    {
        report = new(RenderFailure.WorkerError);
        externalCancellation.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        deadline.CancelAfter(budget);
        string directory = Path.GetDirectoryName(typeof(WorkerClient).Assembly.Location)!;
        var command = new ProcessStartInfo(Path.Combine(directory, "PixelThumbs.exe"))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        command.ArgumentList.Add("--render-worker");
        command.ArgumentList.Add(extension);
        command.ArgumentList.Add(size.ToString(CultureInfo.InvariantCulture));
        command.Environment["PIXELTHUMBS_RENDER_STYLE"] = JsonSerializer.Serialize(style);
        command.Environment.Remove("PIXELTHUMBS_RENDER_DIRECTORY");
        bool scanGCodeWindows = extension == ".gcode" && sourceLength > 2 * EmbeddedPreview.GCodeScanBytes;
        long inputLength = scanGCodeWindows ? 2 * EmbeddedPreview.GCodeScanBytes + 4 : sourceLength;
        command.Environment[InputLengthVariable] = inputLength.ToString(CultureInfo.InvariantCulture);
        command.Environment[DesktopBackendVariable] = desktopBackend ? "1" : "0";

        DirectoryInfo? staging = external ? Directory.CreateTempSubdirectory("PixelThumbs-") : null;
        if (staging is not null)
            command.Environment["PIXELTHUMBS_RENDER_DIRECTORY"] = staging.FullName;
        try
        {
            long started = Stopwatch.GetTimestamp();
            using var process = Process.Start(command);
            if (process is null)
                return null;
            try
            {
                // The worker waits for a byte before reading settings, models or starting F3D.
                // A failure to assign the job therefore cannot run an unconstrained render.
                report = new(RenderFailure.Isolation);
                using var job = attach(process);
                report = report with { Startup = Stopwatch.GetElapsedTime(started) };
                if (job is null || job.IsInvalid)
                    return null;

                using var kill = deadline.Token.Register(() => RenderJob.Terminate(job));
                using var png = new MemoryStream();
                using var timings = new MemoryStream();
                var output = CopyBoundedAsync(process.StandardOutput.BaseStream, png, MaximumPngBytes, deadline.Token);
                var diagnosticOutput = CopyBoundedAsync(process.StandardError.BaseStream, timings, 4096, deadline.Token);
                try
                {
                    report = report with { Failure = RenderFailure.ReadError };
                    source.Position = 0;
                    using (var input = process.StandardInput.BaseStream)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        input.WriteByte(StartRendering);
                        if (scanGCodeWindows)
                            CopyGCodeWindows(source, input, deadline.Token);
                        else
                            CopyBounded(source, input, RenderPipeline.MaximumInputFor(extension), deadline.Token);
                    }

                    report = report with { Failure = RenderFailure.WorkerError };
                    output.GetAwaiter().GetResult();
                    diagnosticOutput.GetAwaiter().GetResult();
                    process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
                    var phaseTimes = ReadTimings(timings);
                    var peak = RenderJob.PeakMemory(job);
                    var exit = DecodeExitCode(process.ExitCode, peak);
                    report = new(exit, report.Startup, phaseTimes.Read, phaseTimes.Render, peak.Job);
                    if (exit != RenderFailure.None)
                        return null;
                    if (png.Length == 0)
                    {
                        report = report with { Failure = RenderFailure.WorkerError };
                        return null;
                    }

                    png.Position = 0;
                    report = report with { Failure = RenderFailure.WorkerError };
                    using var decoded = Image.FromStream(png, useEmbeddedColorManagement: false, validateImageData: true);
                    if (decoded.Height != size || decoded.Width != size)
                        return null;
                    var image = new Bitmap(decoded);
                    report = report with { Failure = RenderFailure.None };
                    return image;
                }
                catch (Exception error) when (deadline.IsCancellationRequested &&
                    error is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    report = report with
                    {
                        Failure = externalCancellation.IsCancellationRequested ? RenderFailure.Cancelled : RenderFailure.Timeout,
                        PeakMemory = RenderJob.PeakMemory(job).Job
                    };
                    return null;
                }
                finally
                {
                    RenderJob.Terminate(job);
                    Drain(output);
                    Drain(diagnosticOutput);
                }
            }
            finally { StopWorker(process); }
        }
        finally
        {
            if (staging is not null)
            {
                try { staging.Delete(recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("PixelThumbs: temporary renderer files could not be removed ({0}).", error.HResult);
                }
            }
        }
    }

    private static RenderFailure DecodeExitCode(int code, (long Process, long Job) peak)
    {
        if (code != 0 && (peak.Process >= RenderJob.ProcessMemoryLimit - 16 * 1024 * 1024 ||
            peak.Job >= RenderJob.JobMemoryLimit - 16 * 1024 * 1024 || code == unchecked((int)0xc0000017)))
            return RenderFailure.MemoryLimit;
        return Enum.IsDefined(typeof(RenderFailure), code) ? (RenderFailure)code : RenderFailure.WorkerError;
    }

    private static (TimeSpan Read, TimeSpan Render) ReadTimings(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        var parts = reader.ReadLine()?.Split(' ');
        return parts is ["timings", var read, var render] &&
            long.TryParse(read, CultureInfo.InvariantCulture, out var readTicks) &&
            long.TryParse(render, CultureInfo.InvariantCulture, out var renderTicks)
            ? (TimeSpan.FromTicks(readTicks), TimeSpan.FromTicks(renderTicks)) : default;
    }

    public static int RunRenderer(string extension, string size)
    {
        using var input = Console.OpenStandardInput();
        if (input.ReadByte() != StartRendering || !RenderJob.IsMemoryLimited())
            return (int)RenderFailure.Isolation;
        if (!FormatCatalog.IsSupported(extension) ||
            !int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out int resolution))
            return (int)RenderFailure.ReadError;

        TimeSpan read = default, render = default;
        var stage = RenderFailure.ReadError;
        try
        {
            using var process = Process.GetCurrentProcess();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }

            var style = JsonSerializer.Deserialize<ThumbnailStyle>(
                Environment.GetEnvironmentVariable("PIXELTHUMBS_RENDER_STYLE") ?? "{}") ?? new ThumbnailStyle();
            long started = Stopwatch.GetTimestamp();
            string? directory = Environment.GetEnvironmentVariable("PIXELTHUMBS_RENDER_DIRECTORY");
            bool external = FormatCatalog.RequiresExternalRenderer(extension);
            using Bitmap? image = external
                ? ExternalImage()
                : LocalImage();

            if (image is null)
                return (int)(external ? RenderFailure.ExternalRenderer : RenderPipeline.UsesEmbeddedImage(extension)
                    ? RenderFailure.NoEmbeddedImage : RenderFailure.ReadError);

            stage = RenderFailure.WorkerError;
            using var output = Console.OpenStandardOutput();
            image.Save(output, ImageFormat.Png);
            return (int)RenderFailure.None;

            Bitmap? ExternalImage()
            {
                if (string.IsNullOrEmpty(directory))
                    return null;
                string model = Path.Combine(directory, "model" + extension);
                using (var file = new FileStream(model, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    CopyBounded(input, file, RenderPipeline.MaximumInputFor(extension), CancellationToken.None);
                read = Stopwatch.GetElapsedTime(started);
                stage = RenderFailure.ExternalRenderer;
                started = Stopwatch.GetTimestamp();
                bool desktopBackend = Environment.GetEnvironmentVariable(DesktopBackendVariable) == "1";
                try { return RenderPipeline.RenderExternal(model, resolution, style, desktopBackend); }
                finally { render = Stopwatch.GetElapsedTime(started); }
            }

            Bitmap? LocalImage()
            {
                int limit = RenderPipeline.MaximumInputFor(extension);
                int.TryParse(Environment.GetEnvironmentVariable(InputLengthVariable), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int expectedLength);
                using var data = Spool(input, limit, Math.Clamp(expectedLength, 0, limit));
                read = Stopwatch.GetElapsedTime(started);
                return RenderPipeline.Render(data, extension, resolution, style, (parse, draw) =>
                {
                    read += parse;
                    render += draw;
                });
            }
        }
        catch (InputLimitException) { return (int)RenderFailure.InputLimit; }
        catch (OutOfMemoryException) { return (int)RenderFailure.MemoryLimit; }
        catch (Exception error) when (error is not StackOverflowException) { return (int)stage; }
        finally { Console.Error.WriteLine(FormattableString.Invariant($"timings {read.Ticks} {render.Ticks}")); }
    }

    private static Stream Spool(Stream input, int limit, int expectedLength, bool leaveInputOpen = false)
    {
        const int memoryLimit = 8 * 1024 * 1024;
        Stream data = expectedLength > memoryLimit ? OpenSpoolFile() : new MemoryStream(expectedLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int remaining = leaveInputOpen ? expectedLength : int.MaxValue;
            while (remaining > 0)
            {
                int count = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (count == 0)
                {
                    if (leaveInputOpen)
                        throw new EndOfStreamException();
                    break;
                }
                if (leaveInputOpen)
                    remaining -= count;
                if (data.Length + count > limit)
                    throw new InputLimitException();
                if (data is MemoryStream && data.Length + count > memoryLimit)
                {
                    var disk = OpenSpoolFile();
                    using (var memory = data)
                    {
                        data = disk;
                        memory.Position = 0;
                        memory.CopyTo(disk);
                    }
                }
                data.Write(buffer, 0, count);
            }
            data.Position = 0;
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static FileStream OpenSpoolFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"PixelThumbs-{Guid.NewGuid():N}.tmp");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
    }

    private static void CopyGCodeWindows(Stream source, Stream destination, CancellationToken cancellation)
    {
        const int headSize = EmbeddedPreview.GCodeScanBytes - 1;
        const int tailSize = EmbeddedPreview.GCodeScanBytes + 1;
        long tailStart = source.Length - tailSize;
        CopyBounded(source, destination, headSize, cancellation, exactLength: true);
        // Preserve two separate scan windows for the worker, without joining split records.
        destination.Write("\nG0\n"u8);
        source.Position = tailStart;
        CopyBounded(source, destination, tailSize, cancellation, exactLength: true);
    }

    private static void CopyBounded(Stream source, Stream destination, int limit, CancellationToken cancellation,
        bool exactLength = false)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long seen = 0;
            while (!exactLength || seen < limit)
            {
                cancellation.ThrowIfCancellationRequested();
                int requested = exactLength ? Math.Min(buffer.Length, limit - (int)seen) : buffer.Length;
                int read = source.Read(buffer, 0, requested);
                if (read == 0)
                {
                    if (exactLength)
                        throw new EndOfStreamException();
                    break;
                }
                seen += read;
                if (seen > limit)
                    throw new InputLimitException();
                destination.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, int limit, CancellationToken cancellation)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int seen = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
            {
                seen += read;
                if (seen > limit)
                    throw new IOException("Renderer output exceeds its limit.");
                destination.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void Drain(Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    private static void StopWorker(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }

    private sealed class InputLimitException : IOException;
}
