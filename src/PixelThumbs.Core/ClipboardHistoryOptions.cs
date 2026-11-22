namespace PixelThumbs;

[Flags]
public enum ClipboardKind
{
    Text = 1,
    Image = 2,
    Files = 4,
    Other = 8,
    All = Text | Image | Files | Other
}

public enum ClipboardEdge { Right, Left, Top, Bottom }

public sealed record ClipboardHistoryOptions
{
    public bool Enabled { get; init; }
    public ClipboardKind Types { get; init; } = ClipboardKind.All;
    public ClipboardEdge Edge { get; init; }
    public string? Monitor { get; init; }
    public int ExpireHours { get; init; }
    public bool KeepBetweenSessions { get; init; }

    public ClipboardHistoryOptions Normalize() => this with
    {
        Types = Types & ClipboardKind.All,
        Edge = Enum.IsDefined(Edge) ? Edge : ClipboardEdge.Right,
        Monitor = Monitor is { Length: > 0 and <= 128 } ? Monitor : null,
        ExpireHours = ExpireHours is 1 or 6 or 24 or 168 ? ExpireHours : 0
    };
}
