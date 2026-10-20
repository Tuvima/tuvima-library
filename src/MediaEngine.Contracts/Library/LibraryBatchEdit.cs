using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

public sealed class LibraryBatchEditRequest
{
    [JsonPropertyName("entity_ids")]
    public List<Guid> EntityIds { get; init; } = [];

    [JsonPropertyName("field_changes")]
    public List<LibraryFieldChange> FieldChanges { get; init; } = [];
}

public sealed class LibraryFieldChange
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    /// <summary>
    /// How a list field (library tags) takes the value: <c>replace</c> (default), <c>add</c> or <c>remove</c> (details overlay spec D8).
    /// Single-value fields only accept <c>replace</c>.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = LibraryFieldChangeModes.Replace;
}

/// <summary>The ways a batch edit can change a list field.</summary>
public static class LibraryFieldChangeModes
{
    public const string Replace = "replace";
    public const string Add = "add";
    public const string Remove = "remove";
}

public sealed class LibraryBatchEditResult
{
    [JsonPropertyName("updated_count")]
    public int UpdatedCount { get; init; }

    [JsonPropertyName("failed_ids")]
    public List<Guid> FailedIds { get; init; } = [];

    [JsonPropertyName("errors")]
    public List<string> Errors { get; init; } = [];

    /// <summary>How many of the failed rows were sent to Needs Review so a person can look at them.</summary>
    [JsonPropertyName("sent_to_review_count")]
    public int SentToReviewCount { get; init; }
}

public sealed class LibraryBatchEditPreview
{
    [JsonPropertyName("affected_count")]
    public int AffectedCount { get; init; }

    [JsonPropertyName("changes")]
    public List<LibraryFieldChangePreview> Changes { get; init; } = [];
}

public sealed class LibraryFieldChangePreview
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("new_value")]
    public required string NewValue { get; init; }

    [JsonPropertyName("old_value_counts")]
    public Dictionary<string, int> OldValueCounts { get; init; } = new();
}
