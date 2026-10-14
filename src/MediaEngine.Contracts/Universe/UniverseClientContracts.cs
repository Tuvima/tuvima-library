using System.Text.Json.Serialization;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Contracts.Universe;

/// <summary>
/// One Universe for a paired native app: its identity and member shelves, with no graph edges and no lore.
/// Every count and card was already limited to what the requesting profile may see.
/// </summary>
public sealed class UniverseClientDetailDto
{
    [JsonPropertyName("qid")]
    public string Qid { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Always <c>universe</c>; see <see cref="MediaEngine.Contracts.Collections.CollectionGroupTypes"/>.</summary>
    [JsonPropertyName("group_type")]
    public string GroupType { get; init; } = MediaEngine.Contracts.Collections.CollectionGroupTypes.Universe;

    /// <summary>Items across all member shelves that the profile may see.</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; }

    [JsonPropertyName("shelves")]
    public IReadOnlyList<UniverseMemberShelfDto> Shelves { get; init; } = [];
}

/// <summary>A Series (or TV show) inside a Universe, shown as a display shelf.</summary>
public sealed class UniverseMemberShelfDto
{
    [JsonPropertyName("collection_id")]
    public Guid CollectionId { get; init; }

    /// <summary><c>show</c> for a TV show, otherwise <c>series</c>.</summary>
    [JsonPropertyName("group_type")]
    public string GroupType { get; init; } = MediaEngine.Contracts.Collections.CollectionGroupTypes.Series;

    /// <summary>All items of this shelf the profile may see; <c>shelf.items</c> holds the first of them.</summary>
    [JsonPropertyName("item_count")]
    public int ItemCount { get; init; }

    [JsonPropertyName("shelf")]
    public DisplayShelfDto Shelf { get; init; } = new(string.Empty, string.Empty, null, [], null);
}
