namespace PixelThumbs;

public enum RenderFailure
{
    None,
    ReadError,
    NoEmbeddedImage,
    ExternalRenderer,
    MemoryLimit,
    InputLimit,
    Timeout,
    Isolation,
    Cancelled,
    WorkerError
}

public sealed record RenderDiagnostics(RenderFailure Failure, TimeSpan Startup = default, TimeSpan Read = default,
    TimeSpan Render = default, long PeakMemory = 0);
