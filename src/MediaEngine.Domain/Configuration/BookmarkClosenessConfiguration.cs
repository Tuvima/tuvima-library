using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// How close two bookmarks must be to count as the same spot, close, or far (Global bookmarks spec D7, D12). Loaded from
/// <c>config/bookmarks.json</c>; the values below apply when the file is missing so a fresh install behaves the same. One block per
/// kind of position: time (films, episodes, videos, audiobooks), page (comics, PDFs) and text (EPUB books).
/// </summary>
public sealed class BookmarkClosenessConfiguration
{
    [JsonPropertyName("time")]
    public BookmarkGapLimits Time { get; set; } = new() { SameSpot = 3, Close = 30, Far = 180 };

    [JsonPropertyName("page")]
    public BookmarkGapLimits Page { get; set; } = new() { SameSpot = 0, Close = 2, Far = 10 };

    /// <summary>
    /// EPUB books measure in chapters: <c>same_spot</c> is the number of characters (as a fraction of one chapter, 0 to 1) that still
    /// counts as the same paragraph; <c>close</c> and <c>far</c> are whole chapters away.
    /// </summary>
    [JsonPropertyName("text")]
    public BookmarkGapLimits Text { get; set; } = new() { SameSpot = 0.02, Close = 0, Far = 2 };

    public BookmarkGapLimits For(BookmarkPositionKind kind) => kind switch
    {
        BookmarkPositionKind.Page => Page,
        BookmarkPositionKind.Text => Text,
        _ => Time,
    };
}

/// <summary>The largest gap that still earns each label. Anything past <see cref="Far"/> is New.</summary>
public sealed class BookmarkGapLimits
{
    [JsonPropertyName("same_spot")]
    public double SameSpot { get; set; }

    [JsonPropertyName("close")]
    public double Close { get; set; }

    [JsonPropertyName("far")]
    public double Far { get; set; }
}

public enum BookmarkPositionKind
{
    /// <summary>Seconds into a film, episode, video or audiobook.</summary>
    Time,

    /// <summary>A page of a comic or PDF.</summary>
    Page,

    /// <summary>A place in the text of an EPUB book.</summary>
    Text,
}

/// <summary>The label a compare row earns from its gap.</summary>
public enum BookmarkCloseness
{
    SameSpot,
    Close,
    Far,
    New,
}
