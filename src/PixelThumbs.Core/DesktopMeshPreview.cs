using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PixelThumbs;

public static partial class WorkerClient
{
    internal sealed class DesktopMeshPreview : IDisposable
    {
        private readonly string path;
        private readonly DateTime modified;
        private readonly int length;
        private readonly Process process;
        private readonly SafeFileHandle job;
        private readonly BinaryWriter input;
        private readonly BinaryReader output;

        private DesktopMeshPreview(string path, DateTime modified, int length, Process process, SafeFileHandle job)
        {
            this.path = path;
            this.modified = modified;
            this.length = length;
            this.process = process;
            this.job = job;
            input = new BinaryWriter(process.StandardInput.BaseStream);
            output = new BinaryReader(process.StandardOutput.BaseStream);
        }

        internal static DesktopMeshPreview? Open(string path, CancellationToken cancellation)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (!MeshRenderer.SupportsMesh(extension.TrimStart('.')))
                return null;

            Process? process = null;
            SafeFileHandle? job = null;
            DesktopMeshPreview? session = null;
            try
            {
                var modified = File.GetLastWriteTimeUtc(path);
                using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (source.Length <= 0 || source.Length > RenderPipeline.MaximumInputFor(extension))
                    return null;

                int length = (int)source.Length;
                string directory = Path.GetDirectoryName(typeof(WorkerClient).Assembly.Location)!;
                var command = new ProcessStartInfo(Path.Combine(directory, "PixelThumbs.exe"))
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = directory,
                };
                command.ArgumentList.Add("--preview-worker");
                command.ArgumentList.Add(extension);
                command.ArgumentList.Add(length.ToString(CultureInfo.InvariantCulture));
                cancellation.ThrowIfCancellationRequested();
                process = Process.Start(command);
                if (process is null)
                    return null;
                job = RenderJob.Attach(process);
                if (job is null || job.IsInvalid)
                    return null;
                session = new DesktopMeshPreview(path, modified, length, process, job);

                session.SendModel(source, cancellation);
                var opened = session;
                session = null;
                process = null;
                job = null;
                return opened;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            {
                cancellation.ThrowIfCancellationRequested();
                return null;
            }
            finally
            {
                if (session is not null)
                    session.Dispose();
                else if (process is not null)
                {
                    job?.Dispose();
                    StopWorker(process);
                    process.Dispose();
                }
            }
        }

        private void SendModel(Stream source, CancellationToken cancellation)
        {
            using var deadline = Deadline(cancellation);
            using var kill = deadline.Token.Register(() => RenderJob.Terminate(job));
            input.Write((byte)StartRendering);
            CopyBounded(source, input.BaseStream, length, deadline.Token, exactLength: true);
            input.Flush();
            deadline.Token.ThrowIfCancellationRequested();
        }

        internal bool Matches(string path)
        {
            if (!this.path.Equals(path, StringComparison.OrdinalIgnoreCase) || process.HasExited)
                return false;
            var file = new FileInfo(path);
            return file.Exists && file.LastWriteTimeUtc == modified && file.Length == length;
        }

        internal byte[]? Render(int size, ThumbnailStyle style, CancellationToken cancellation)
        {
            using var deadline = Deadline(cancellation);
            using var kill = deadline.Token.Register(() => RenderJob.Terminate(job));
            try
            {
                input.Write(size);
                input.Write(style.ModelColor);
                input.Write(style.BackgroundColor);
                input.Write(style.Transparent);
                input.Write(style.UseSourceColors);
                input.Write(style.PreferEmbedded);
                input.Write(style.Yaw);
                input.Write(style.Pitch);
                input.Write(style.Lighting);
                input.Write(style.Margin);
                input.Flush();
                if (!output.ReadBoolean())
                    return null;
                var pixels = new byte[size * size * 4];
                output.BaseStream.ReadExactly(pixels);
                cancellation.ThrowIfCancellationRequested();
                return pixels;
            }
            catch (IOException)
            {
                cancellation.ThrowIfCancellationRequested();
                return null;
            }
        }

        private CancellationTokenSource Deadline(CancellationToken cancellation)
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(length > 16 * 1024 * 1024 ? 15 : 5));
            return deadline;
        }

        public void Dispose()
        {
            job.Dispose();
            try { input.Dispose(); }
            catch (IOException) { }
            output.Dispose();
            process.Dispose();
        }
    }

    internal static int RunPreviewWorker(string extension, string bytes)
    {
        using var reader = new BinaryReader(Console.OpenStandardInput());
        if (reader.BaseStream.ReadByte() != StartRendering || !RenderJob.IsMemoryLimited())
            return (int)RenderFailure.Isolation;
        int limit = RenderPipeline.MaximumInputFor(extension);
        if (!MeshRenderer.SupportsMesh(extension.TrimStart('.')) ||
            !int.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out int length) || length <= 0 || length > limit)
            return (int)RenderFailure.ReadError;

        try
        {
            using var model = Spool(reader.BaseStream, limit, length, leaveInputOpen: true);
            using var output = new BinaryWriter(Console.OpenStandardOutput());
            var geometry = new Lazy<Func<int, ThumbnailStyle, Bitmap?>?>(() => MeshRenderer.PreparePreview(model, extension));
            byte[] pixels = [];
            while (true)
            {
                int size = reader.ReadInt32();
                if (size is < 16 or > 512)
                    return (int)RenderFailure.ReadError;
                var style = new ThumbnailStyle
                {
                    ModelColor = reader.ReadInt32(),
                    BackgroundColor = reader.ReadInt32(),
                    Transparent = reader.ReadBoolean(),
                    UseSourceColors = reader.ReadBoolean(),
                    PreferEmbedded = reader.ReadBoolean(),
                    Yaw = reader.ReadSingle(),
                    Pitch = reader.ReadSingle(),
                    Lighting = reader.ReadSingle(),
                    Margin = reader.ReadSingle(),
                }.Normalize();
                using Bitmap? bitmap = (style.PreferEmbedded ? RenderPipeline.RenderEmbedded(model, extension, size, style) : null)
                    ?? geometry.Value?.Invoke(size, style)
                    ?? (!style.PreferEmbedded ? RenderPipeline.RenderEmbedded(model, extension, size, style) : null);
                output.Write(bitmap is not null);
                if (bitmap is not null)
                    WriteFrame(bitmap);
                output.Flush();
            }

            void WriteFrame(Bitmap bitmap)
            {
                int stride = bitmap.Width * 4;
                int count = stride * bitmap.Height;
                if (pixels.Length != count)
                    pixels = new byte[count];
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    for (int row = 0; row < bitmap.Height; row++)
                        Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * stride, stride);
                }
                finally { bitmap.UnlockBits(data); }
                output.Write(pixels);
            }
        }
        catch (EndOfStreamException) { return (int)RenderFailure.None; }
        catch (Exception error) when (error is not StackOverflowException) { return (int)RenderFailure.WorkerError; }
    }
}
