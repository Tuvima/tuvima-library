using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Activity;

/// <summary>
/// The values of the <c>category</c> field and filter on the Events part of System &gt; Logs (the Activity log):
/// added media, Needs Review, failures and system events. <see cref="All"/> is a filter only, never an entry's category.
/// </summary>
public static class ActivityLogCategoryValues
{
    public const string Added = "added";
    public const string Review = "review";
    public const string Failed = "failed";
    public const string System = "system";

    /// <summary>Every entry of the four categories above. The default filter when no other is asked for.</summary>
    public const string All = "all";
}

/// <summary>
/// One row of the Activity log. The Engine writes the words; the Dashboard shows them as they are, so the same event
/// reads the same everywhere. Photos and personal videos arrive as one count row, never one row per file.
/// </summary>
public sealed class ActivityLogEntryDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>UTC.</summary>
    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary><c>added</c>, <c>review</c>, <c>failed</c> or <c>system</c>.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>The stored kind of event (for example <c>MediaAdded</c> or <c>ToolMissing</c>), for symbols and tests, never shown as text.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The plain headline: a title, a file name, "3,000 photos" or "Couldn't find FFmpeg".</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The plain explanation under the headline, when there is one.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>The media type of an added, reviewed or failed item (for example <c>Movies</c> or <c>Photos</c>), when known.</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>How many items one count row stands for (photos and personal videos); null for a single item.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    /// <summary>A Dashboard address that opens the item the row is about, when it has one.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; set; }
}
