using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>
/// One file the Engine could not match (Library setup spec D34). It stays exactly where it is and keeps playing and browsing with its
/// file name and tags; it is looked up again later. Only the file's name is shared, never its folder.
/// </summary>
public sealed class UnmatchedItemDto
{
    [JsonPropertyName("asset_id")]
    public Guid AssetId { get; init; }

    [JsonPropertyName("library_id")]
    public string LibraryId { get; init; } = string.Empty;

    /// <summary>The title the file plays under (from its tags or file name); null when none is known.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    /// <summary><c>no_match</c> (a lookup found nothing) or <c>no_youtube_id</c> (a YouTube library file with no video ID; never looked up).</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("first_unmatched_at")]
    public DateTimeOffset FirstUnmatchedAt { get; init; }

    [JsonPropertyName("last_tried_at")]
    public DateTimeOffset LastTriedAt { get; init; }

    /// <summary>When it is next looked up; null for an item kept as it is.</summary>
    [JsonPropertyName("next_retry_at")]
    public DateTimeOffset? NextRetryAt { get; init; }

    /// <summary>True after Keep as is: no retries, and the item is not in the Unmatched count.</summary>
    [JsonPropertyName("kept_as_is")]
    public bool KeptAsIs { get; init; }
}

/// <summary>One page of a library's Unmatched files, newest first.</summary>
public sealed class UnmatchedPageDto
{
    [JsonPropertyName("items")]
    public List<UnmatchedItemDto> Items { get; init; } = [];

    /// <summary>How many files match the filter in total (kept items are counted only when they were asked for).</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("skip")]
    public int Skip { get; init; }

    [JsonPropertyName("take")]
    public int Take { get; init; }
}

/// <summary>Each library's Unmatched count (kept items excluded), for the libraries the caller may see. A library with none is absent.</summary>
public sealed class UnmatchedCountsDto
{
    [JsonPropertyName("counts")]
    public Dictionary<string, int> Counts { get; init; } = new();
}

/// <summary>The answer to Retry now, Keep as is, Keep all and Look up again: how many files the action changed.</summary>
public sealed class UnmatchedActionResultDto
{
    [JsonPropertyName("affected")]
    public int Affected { get; init; }
}
