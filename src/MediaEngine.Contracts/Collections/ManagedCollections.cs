using System.Text.Json.Serialization;
using MediaEngine.Domain.Models;

namespace MediaEngine.Contracts.Collections;

/// <summary>
/// DTO for non-Universe collections displayed in the managed collections surface.
/// Includes management fields (enabled, featured, rules) not present in <see cref="CollectionDto"/>.
/// </summary>
public class ManagedCollectionDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("icon_name")]
    public string? IconName { get; init; }

    [JsonPropertyName("cover_artwork_url")]
    public string? CoverArtworkUrl { get; set; }

    [JsonPropertyName("background_artwork_url")]
    public string? BackgroundArtworkUrl { get; set; }

    [JsonPropertyName("banner_artwork_url")]
    public string? BannerArtworkUrl { get; set; }

    [JsonPropertyName("logo_artwork_url")]
    public string? LogoArtworkUrl { get; set; }

    [JsonPropertyName("collection_type")]
    public string CollectionType { get; init; } = "Custom";

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "library";

    [JsonPropertyName("profile_id")]
    public Guid? ProfileId { get; init; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; init; } = "private";

    [JsonPropertyName("membership_mode")]
    public string MembershipMode { get; init; } = "Manual";

    [JsonPropertyName("primary_area")]
    public string PrimaryArea { get; init; } = "Mixed";

    [JsonPropertyName("owner_kind")]
    public string OwnerKind { get; init; } = "Profile";

    [JsonPropertyName("audience")]
    public string Audience { get; init; } = "Private";

    [JsonPropertyName("selected_profile_ids")]
    public IReadOnlyList<Guid> SelectedProfileIds { get; init; } = [];

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; init; } = true;

    [JsonPropertyName("is_featured")]
    public bool IsFeatured { get; init; }

    [JsonPropertyName("min_items")]
    public int MinItems { get; init; }

    [JsonPropertyName("rule_json")]
    public string? RuleJson { get; init; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; init; } = "query";

    [JsonPropertyName("rule_hash")]
    public string? RuleHash { get; init; }

    [JsonPropertyName("match_mode")]
    public string MatchMode { get; init; } = "all";

    [JsonPropertyName("sort_field")]
    public string? SortField { get; init; }

    [JsonPropertyName("sort_direction")]
    public string SortDirection { get; init; } = "desc";

    [JsonPropertyName("secondary_sort_field")]
    public string? SecondarySortField { get; init; }

    [JsonPropertyName("secondary_sort_direction")]
    public string? SecondarySortDirection { get; init; }

    [JsonPropertyName("refresh_schedule")]
    public string? RefreshSchedule { get; init; }

    [JsonPropertyName("item_count")]
    public int ItemCount { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Active";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("modified_at")]
    public DateTimeOffset? ModifiedAt { get; init; }

    [JsonPropertyName("can_edit")]
    public bool CanEdit { get; init; }

    [JsonPropertyName("can_share")]
    public bool CanShare { get; init; }

}

/// <summary>
/// DTO for the rich Collections hub. This intentionally keeps classification
/// decisions server-side so the UI does not guess system/global/user semantics.
/// </summary>
public class CollectionManagementCatalogDto : ManagedCollectionDto
{
    [JsonPropertyName("family")]
    public string Family { get; init; } = "User";

    [JsonPropertyName("system_key")]
    public string? SystemKey { get; init; }

    /// <summary>
    /// The user-facing kind of group: <c>show</c>, <c>series</c>, <c>collection</c> (a rollup across
    /// shelves or a rule-built curated group) or <c>list</c> (a hand-picked or system list).
    /// </summary>
    [JsonPropertyName("group_type")]
    public string GroupType { get; init; } = "collection";

    /// <summary>
    /// The Wikidata QID of the Universe this entry is, or null when the entry is not a Universe. Only a well-formed
    /// QID is ever set; paired apps use it to open <c>GET /api/v1/universe/{qid}</c>.
    /// </summary>
    [JsonPropertyName("universe_qid")]
    public string? UniverseQid { get; init; }

    [JsonPropertyName("primary_lane")]
    public string PrimaryLane { get; init; } = "CrossMedia";

    [JsonPropertyName("is_global")]
    public bool IsGlobal { get; init; }

    [JsonPropertyName("is_system")]
    public bool IsSystem { get; init; }

    [JsonPropertyName("is_cross_media")]
    public bool IsCrossMedia { get; init; }

    [JsonPropertyName("watch_count")]
    public int WatchCount { get; init; }

    [JsonPropertyName("listen_count")]
    public int ListenCount { get; init; }

    [JsonPropertyName("read_count")]
    public int ReadCount { get; init; }

    [JsonPropertyName("other_count")]
    public int OtherCount { get; init; }

    [JsonPropertyName("movie_count")]
    public int MovieCount { get; init; }

    [JsonPropertyName("tv_count")]
    public int TvCount { get; init; }

    [JsonPropertyName("book_count")]
    public int BookCount { get; init; }

    [JsonPropertyName("comic_count")]
    public int ComicCount { get; init; }

    [JsonPropertyName("music_count")]
    public int MusicCount { get; init; }

    [JsonPropertyName("audiobook_count")]
    public int AudiobookCount { get; init; }

    [JsonPropertyName("earliest_year")]
    public int? EarliestYear { get; init; }

    [JsonPropertyName("latest_year")]
    public int? LatestYear { get; init; }

    [JsonPropertyName("can_delete")]
    public bool CanDelete { get; init; }

    [JsonPropertyName("can_rename")]
    public bool CanRename { get; init; }

    [JsonPropertyName("can_toggle_global")]
    public bool CanToggleGlobal { get; init; }

    [JsonPropertyName("artwork_items")]
    public IReadOnlyList<CollectionArtworkItemDto> ArtworkItems { get; init; } = [];

    [JsonPropertyName("artwork_palette")]
    public ArtworkPalette ArtworkPalette { get; init; } = ArtworkPalette.TuvimaDefault();

    [JsonPropertyName("person")]
    public CollectionCatalogPersonDto? Person { get; init; }

}

public sealed class CollectionCatalogPersonDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("headshot_url")]
    public string? HeadshotUrl { get; set; }

    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; init; } = [];
}

public sealed class CollectionArtworkItemDto
{
    [JsonPropertyName("work_id")]
    public Guid WorkId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("media_type")]
    public string MediaType { get; init; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("facts")]
    public IReadOnlyList<string> Facts { get; init; } = [];

    [JsonPropertyName("primary_color")]
    public string? PrimaryColor { get; init; }

    [JsonPropertyName("secondary_color")]
    public string? SecondaryColor { get; init; }

    [JsonPropertyName("accent_color")]
    public string? AccentColor { get; init; }

    [JsonPropertyName("artwork_shape")]
    public string ArtworkShape { get; init; } = "square";

    [JsonIgnore]
    public string? LocalImagePath { get; init; }
}

public sealed record CollectionCatalogClassification(
    string Family,
    string CollectionType,
    string? SystemKey,
    bool IsSystem,
    string? PrimaryLaneOverride = null);

public sealed record CollectionMediaCounts(
    int WatchCount,
    int ListenCount,
    int ReadCount,
    int OtherCount,
    int TvCount = 0,
    int MovieCount = 0,
    int BookCount = 0,
    int ComicCount = 0,
    int MusicCount = 0,
    int AudiobookCount = 0,
    int? EarliestYear = null,
    int? LatestYear = null)
{
    public int TotalCount => WatchCount + ListenCount + ReadCount + OtherCount;

    public bool IsCrossMedia =>
        new[] { WatchCount, ListenCount, ReadCount, OtherCount }
            .Count(count => count > 0) != 1;

    public string PrimaryLane
    {
        get
        {
            if (IsCrossMedia)
            {
                return "CrossMedia";
            }

            if (WatchCount > 0)
            {
                return "Watch";
            }

            if (ListenCount > 0)
            {
                return "Listen";
            }

            if (ReadCount > 0)
            {
                return "Read";
            }

            return "CrossMedia";
        }
    }
}
