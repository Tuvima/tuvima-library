using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Vault;

// Identify for a Vault video (Videos spec D38), how a video was matched (D30) and the quiet Vault activity list (D32).
// Every one of these answers "not found" to anyone without an unlocked Vault mode session. Search text is sent in a request body, never in
// an address, so a title typed here cannot end up in an access log.

/// <summary>What Identify searches for. Empty text means "use the name patterns' title and the product code in the file name".</summary>
public sealed class VaultIdentifySearchRequest
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>One record that might be this video.</summary>
public sealed class VaultCandidateDto
{
    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("record_id")]
    public string RecordId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("studio")]
    public string? Studio { get; init; }

    /// <summary>yyyy-MM-dd, or null.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("product_code")]
    public string? ProductCode { get; init; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; init; }

    [JsonPropertyName("performers")]
    public IReadOnlyList<string> Performers { get; init; } = [];

    /// <summary>True when the record's length agrees with this file's length.</summary>
    [JsonPropertyName("length_agrees")]
    public bool LengthAgrees { get; init; }
}

public sealed class VaultCandidateListDto
{
    /// <summary>"ok", "not_connected" (no server with a working key), "lookup_off" (the library's lookup is None) or "unavailable".</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>The text that was searched for, so the person sees what "use the file name" turned into.</summary>
    [JsonPropertyName("searched_for")]
    public string? SearchedFor { get; init; }

    [JsonPropertyName("product_code")]
    public string? ProductCode { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<VaultCandidateDto> Candidates { get; init; } = [];
}

public sealed class VaultIdentifyPreviewRequest
{
    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("record_id")]
    public string RecordId { get; init; } = string.Empty;
}

/// <summary>One row of the tick-per-field screen.</summary>
public sealed class VaultFieldChoiceDto
{
    /// <summary>"title", "description", "release_date", "cover", "studio", "performers" or "tags".</summary>
    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    [JsonPropertyName("current")]
    public string? Current { get; init; }

    [JsonPropertyName("proposed")]
    public string? Proposed { get; init; }

    [JsonPropertyName("changes")]
    public bool Changes { get; init; }

    /// <summary>The current value is the person's own edit. It is never replaced.</summary>
    [JsonPropertyName("persons_own")]
    public bool PersonsOwn { get; init; }

    /// <summary>Whether the box starts ticked.</summary>
    [JsonPropertyName("ticked")]
    public bool Ticked { get; init; }
}

public sealed class VaultIdentifyPreviewDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("record_id")]
    public string RecordId { get; init; } = string.Empty;

    [JsonPropertyName("record_title")]
    public string? RecordTitle { get; init; }

    [JsonPropertyName("product_code")]
    public string? ProductCode { get; init; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; init; }

    [JsonPropertyName("length_agrees")]
    public bool LengthAgrees { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<VaultFieldChoiceDto> Fields { get; init; } = [];
}

public sealed class VaultIdentifyApplyRequest
{
    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("record_id")]
    public string RecordId { get; init; } = string.Empty;

    /// <summary>The fields the person ticked. Only these are applied; the values are read again from the server.</summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<string> Fields { get; init; } = [];
}

public sealed class VaultIdentifyApplyResultDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("applied")]
    public IReadOnlyList<string> Applied { get; init; } = [];

    /// <summary>Fields the record had that failed a check and were not stored.</summary>
    [JsonPropertyName("refused")]
    public IReadOnlyList<string> Refused { get; init; } = [];
}

/// <summary>Where one field of a Vault video came from.</summary>
public sealed class VaultFieldSourceDto
{
    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("record_id")]
    public string RecordId { get; init; } = string.Empty;

    /// <summary>"fingerprint", "product_code", "title" or "identify".</summary>
    [JsonPropertyName("matched_by")]
    public string MatchedBy { get; init; } = string.Empty;

    /// <summary>"automatic" or "identify".</summary>
    [JsonPropertyName("applied_via")]
    public string AppliedVia { get; init; } = string.Empty;

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("applied_at")]
    public DateTimeOffset AppliedAt { get; init; }
}

/// <summary>How a Vault video was matched, if it was. A video that was not matched is Unmatched.</summary>
public sealed class VaultMatchDto
{
    [JsonPropertyName("matched")]
    public bool Matched { get; init; }

    [JsonPropertyName("server_id")]
    public string? ServerId { get; init; }

    [JsonPropertyName("record_id")]
    public string? RecordId { get; init; }

    [JsonPropertyName("matched_by")]
    public string? MatchedBy { get; init; }

    [JsonPropertyName("applied_via")]
    public string? AppliedVia { get; init; }

    [JsonPropertyName("matched_at")]
    public DateTimeOffset? MatchedAt { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<VaultFieldSourceDto> Fields { get; init; } = [];
}

/// <summary>One line of the quiet Vault activity list. The detail is fixed plain words.</summary>
public sealed class VaultActivityItemDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("asset_id")]
    public Guid AssetId { get; init; }

    /// <summary>"added", "matched", "unmatched", "identified" or "failed".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// How many saved Vault bookmarks that came back with a restored backup are still waiting for their video. A backup keeps a bookmark
/// as the title it belongs to (never as a Vault file); each one is re-linked when a rescan finds the matching video. Counts only, no titles.
/// </summary>
public sealed class VaultRestoredBookmarksDto
{
    /// <summary>Bookmarks not yet re-linked. Zero when nothing was restored, or when every bookmark found its video.</summary>
    [JsonPropertyName("waiting")]
    public int Waiting { get; init; }

    /// <summary>Plain words for the Vault settings page, or empty when nothing is waiting.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
