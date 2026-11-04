using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>What <see cref="ItemQuickFactsDto.Kind"/> says the id is.</summary>
public static class ItemQuickFactsKinds
{
    /// <summary>A library title at any level: a film, a show, a season, an episode, an album, a song, a book, a comic issue or a video.</summary>
    public const string Work = "work";

    public const string Person = "person";

    /// <summary>A Series, a curated collection or a Universe (the collection record).</summary>
    public const string Collection = "collection";

    /// <summary>A character, place, faction or other thing of a Universe.</summary>
    public const string Thing = "thing";
}

/// <summary>
/// The totals the editor's Quick Facts need that no other read carries (universal editor v4, D4): how many of a container's children
/// are owned, and the total size, runtime and file count of everything under the item. Facts a single item already has (its year, pages,
/// ISBN, narrator) come from the editor's own field list, not from here. Every count and total covers only files that are in the library.
/// </summary>
public sealed class ItemQuickFactsDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }

    /// <summary>One of <see cref="ItemQuickFactsKinds"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = ItemQuickFactsKinds.Work;

    /// <summary>How many media files sit under the item (its own, or those of everything inside it).</summary>
    [JsonPropertyName("file_count")] public int FileCount { get; init; }

    /// <summary>The size of all those files; null when none of them has a stored size.</summary>
    [JsonPropertyName("size_bytes")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? SizeBytes { get; init; }

    /// <summary>The summed running time of those files in seconds; null when none of them has a stored length.</summary>
    [JsonPropertyName("runtime_seconds")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RuntimeSeconds { get; init; }

    /// <summary>The best picture or sound quality among the files, such as "4K HDR", "1080p" or "FLAC"; null when unknown.</summary>
    [JsonPropertyName("best_quality")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BestQuality { get; init; }

    /// <summary>Of the item's direct children (a show's seasons, a season's episodes, an album's tracks), how many are in the library.</summary>
    [JsonPropertyName("direct_owned")] public int DirectOwned { get; init; }

    /// <summary>Of the item's direct children, how many the library knows about (owned or not).</summary>
    [JsonPropertyName("direct_total")] public int DirectTotal { get; init; }

    /// <summary>Of the playable items under the item (a show's episodes, an album's tracks), how many are in the library.</summary>
    [JsonPropertyName("leaf_owned")] public int LeafOwned { get; init; }

    /// <summary>Of the playable items under the item, how many the library knows about (owned or not).</summary>
    [JsonPropertyName("leaf_total")] public int LeafTotal { get; init; }

    /// <summary>True when any of the item's files lives in a library folder marked read-only. The Engine sets it after the data store read, from the configured folders.</summary>
    [JsonPropertyName("read_only_library")] public bool ReadOnlyLibrary { get; set; }

    /// <summary>For a person: the works in the library that credit them. For a collection or Universe: the titles in it. For a thing of a Universe: the Works it appears in.</summary>
    [JsonPropertyName("item_count")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ItemCount { get; init; }

    /// <summary>For a curated collection: the name of its owner, or "Library" when everyone shares it.</summary>
    [JsonPropertyName("owner")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Owner { get; init; }

    /// <summary>For a person: the roles they hold (Author, Director, Actor ...).</summary>
    [JsonPropertyName("roles")] public List<string> Roles { get; init; } = [];

    /// <summary>For a Universe: how many titles it holds of each kind (Films, Books ...).</summary>
    [JsonPropertyName("counts_by_kind")] public List<ItemQuickFactCountDto> CountsByKind { get; init; } = [];
}

/// <summary>One "how many of this kind" line.</summary>
public sealed class ItemQuickFactCountDto
{
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;

    [JsonPropertyName("count")] public int Count { get; init; }
}
