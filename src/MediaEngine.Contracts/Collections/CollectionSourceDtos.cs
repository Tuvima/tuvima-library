using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Collections;

/// <summary>
/// How a collection fills up, as the Collections page names it on each tile's source line (Collections and lists spec 4g).
/// Generated Series, Universes and system collections are <see cref="Automatic"/>.
/// </summary>
public static class CollectionFillKinds
{
    public const string HandPicked = "hand_picked";
    public const string Rules = "rules";
    public const string List = "list";

    /// <summary>A collection a pack made, inside its set.</summary>
    public const string Pack = "pack";

    /// <summary>A set: the parent collection a pack makes to hold several collections.</summary>
    public const string Set = "set";

    public const string Automatic = "automatic";
}

/// <summary>An imported list's source line on a tile and the counts behind "You own 83 of 250".</summary>
public sealed class CollectionCatalogListSourceDto
{
    /// <summary>imdb, letterboxd, trakt, goodreads, storygraph, csv or text.</summary>
    [JsonPropertyName("source_kind")]
    public string SourceKind { get; init; } = "csv";

    /// <summary>The source in words, for example "Letterboxd export".</summary>
    [JsonPropertyName("source_label")]
    public string SourceLabel { get; init; } = string.Empty;

    [JsonPropertyName("last_refreshed_at")]
    public DateTimeOffset? LastRefreshedAt { get; init; }

    /// <summary>never, daily or weekly.</summary>
    [JsonPropertyName("refresh")]
    public string Refresh { get; init; } = ListImportChoices.RefreshNever;

    [JsonPropertyName("entry_count")]
    public int EntryCount { get; init; }

    [JsonPropertyName("owned_count")]
    public int OwnedCount { get; init; }

    [JsonPropertyName("unmatched_count")]
    public int UnmatchedCount { get; init; }
}
