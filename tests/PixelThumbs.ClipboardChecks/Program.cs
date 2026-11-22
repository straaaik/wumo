using PixelThumbs;
using PixelThumbs.ClipboardHistory;

try { return Run(args); }
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static int Run(string[] args)
{
    if (args.Contains("--native") || args.Contains("--idle"))
        return NativeChecks.Run(args);
    CheckHistory();
    CheckShelfMotion();
    // Image and transfer checks can run without touching the system clipboard.
    Exception? failure = null;
    var sta = new Thread(() =>
    {
        try { NativeChecks.CheckTransfer(); }
        catch (Exception error) { failure = error; }
    });
    sta.SetApartmentState(ApartmentState.STA);
    sta.Start();
    sta.Join();
    if (failure is not null)
        throw failure;
    return 0;
}

static void CheckHistory()
{
    var defaults = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.ClipboardHistory;
    Require(!defaults.Enabled && defaults.Types == ClipboardKind.All && !defaults.KeepBetweenSessions &&
        defaults.Edge == ClipboardEdge.Right && defaults.ExpireHours == 0, "existing profile defaults");

    var history = new HistoryStore(maxBytes: 86, maxEntries: 3);
    var time = new DateTime(2026, 11, 21, 12, 0, 0);
    history.Add(Item(ClipboardKind.Image, "first"), time);
    history.Add(Item(ClipboardKind.Text, "text"), time.AddMinutes(2));
    history.Add(Item(ClipboardKind.Image, "second"), time.AddMinutes(4));
    var late = history.Add(Item(ClipboardKind.Image, "first"), time.AddMinutes(10));
    Require(late.Copies == 2 && history.Entries.Count == 3 &&
        ReferenceEquals(history.Entries[0], late) && history.Entries.Count(entry => entry.Item.Key == "first") == 1, "deduplicate and move to newest");

    history.Add(Item(ClipboardKind.Image, "third", 48), time.AddMinutes(11));
    Require(history.Entries.Count == 2 && history.Bytes == 84, "byte eviction");
    var link = ClipboardCapture.FromDrop(new([], "https://example.net/reference"))!;
    Require(ShelfFilters.Matches(link, ShelfFilter.Links, "REFERENCE") &&
        !ShelfFilters.Matches(link, ShelfFilter.Text, ""), "category and case-insensitive link search");
    var longText = ClipboardCapture.FromDrop(new([], new string('x', 400) + "needle"))!;
    Require(ShelfFilters.Matches(longText, ShelfFilter.Text, "needle"), "search past the card preview");
    history.Clear();
    Require(history.Entries.Count == 0 && history.Bytes == 0, "clear history");
    Console.WriteLine("Clipboard shelf defaults, deduplication, limits, filters and full-text search passed.");
}

static ClipboardItem Item(ClipboardKind kind, string key, int bytes = 16) =>
    new(kind, key, key, [new ClipboardPayload(13, -1, new byte[bytes])], [], null, null, false, null);

static void CheckShelfMotion()
{
    foreach (var edge in Enum.GetValues<ClipboardEdge>())
    {
        var layout = ShelfMotion.Layout;
        bool horizontal = ShelfMotion.Horizontal(edge);
        double width = horizontal ? layout.HorizontalWidth : layout.SideWidth;
        double height = horizontal ? layout.HorizontalHeight : layout.SideHeight;
        Require(horizontal ? width > height : height > width, "layout follows the screen edge");
        var closed = ShelfMotion.Frame(width, height, edge, 0);
        var copied = ShelfMotion.Frame(width, height, edge, 0, 1);
        var opened = ShelfMotion.Frame(width, height, edge, 1);
        Require((horizontal ? closed.Bounds.Height : closed.Bounds.Width) == 12 &&
            (horizontal ? copied.Bounds.Height : copied.Bounds.Width) == 21 &&
            (horizontal ? copied.Bounds.Width : copied.Bounds.Height) == 94 &&
            closed.Opacity == 0 && copied.Opacity == 0 &&
            opened.Bounds.Width == width && opened.Bounds.Height == height && opened.Opacity == 1,
            "the copy pulse only slightly grows the tab and leaves the panel closed");
        Require(ShelfMotion.Frame(width, height, edge, 1, 1) == opened, "the copy pulse does not resize an open panel");
        CheckPosition(copied);
        for (int tick = 0; tick <= 20; tick++)
            CheckPosition(ShelfMotion.Frame(width, height, edge, tick / 20d));

        void CheckPosition(ShelfFrame frame)
        {
            var anchored = edge switch
            {
                ClipboardEdge.Top => frame.Bounds.Top,
                ClipboardEdge.Bottom => frame.Bounds.Bottom - height,
                ClipboardEdge.Left => frame.Bounds.Left,
                _ => frame.Bounds.Right - width
            };
            double center = horizontal ? frame.Bounds.Left + frame.Bounds.Right - width :
                frame.Bounds.Top + frame.Bounds.Bottom - height;
            Require(Math.Abs(anchored) < .001 && Math.Abs(center) < .001 && frame.Bounds.Left >= -.001 &&
                frame.Bounds.Top >= -.001 && frame.Bounds.Right <= width + .001 && frame.Bounds.Bottom <= height + .001,
                "the bubble stays centered, anchored and inside the working bounds");
        }
    }
    Require(ShelfMotion.CopyAmplitude(0, 0) == 0 && ShelfMotion.CopyAmplitude(300, 0) == 1 &&
        ShelfMotion.CopyAmplitude(ShelfMotion.CopyDuration, 0) == 0 &&
        ShelfMotion.CopyAmplitude(0, .5) == .5, "copy feedback has a finite pulse and smoothly extends for consecutive copies");
    Console.WriteLine("The centered showcase bubble and copy pulse stay anchored on all four edges.");
}

static void Require(bool success, string name)
{
    if (!success)
        throw new InvalidOperationException(name);
}
