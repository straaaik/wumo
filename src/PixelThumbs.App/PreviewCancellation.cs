namespace PixelThumbs;

internal sealed class PreviewCancellation : IAsyncDisposable
{
    private readonly CancellationTokenSource source = new();
    private Task? cancellation;

    internal CancellationToken Token => source.Token;
    internal bool IsCancellationRequested => source.IsCancellationRequested;

    internal void Cancel() => cancellation ??= source.CancelAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (cancellation is { } pending)
                await pending.ConfigureAwait(false);
        }
        finally { source.Dispose(); }
    }
}
