using System.Text.Json.Serialization;
using MediaEngine.Domain;

namespace MediaEngine.Contracts.Profiles;

public sealed class ProfileResponseDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("avatar_color")]
    public string AvatarColor { get; init; } = "#7C4DFF";

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("navigation_config")]
    public string? NavigationConfig { get; init; }

    [JsonPropertyName("avatar_image_url")]
    public string? AvatarImageUrl { get; init; }

    /// <summary>Key of a built-in avatar icon drawn on <see cref="AvatarColor"/>; null shows the initial.</summary>
    [JsonPropertyName("avatar_icon")]
    public string? AvatarIcon { get; init; }

    /// <summary>The highest rating this profile may see (G, PG, PG-13 or R); null means Everything.</summary>
    [JsonPropertyName("content_limit")]
    public string? ContentLimit { get; init; }

    /// <summary>True when items with no rating stay visible although a content limit is set.</summary>
    [JsonPropertyName("content_limit_allow_unrated")]
    public bool ContentLimitAllowUnrated { get; init; }

    /// <summary>True when switching to this profile needs its PIN. Filled in by the profile list only.</summary>
    [JsonPropertyName("has_pin")]
    public bool HasPin { get; init; }
}

public sealed class ProfileOverviewResponseDto
{
    [JsonPropertyName("profile")]
    public ProfileResponseDto Profile { get; init; } = new();

    [JsonPropertyName("stats")]
    public ProfileOverviewStatsDto Stats { get; init; } = new();

    [JsonPropertyName("recent_items")]
    public List<ProfileOverviewItemDto> RecentItems { get; init; } = [];

    [JsonPropertyName("continue_items")]
    public List<ProfileOverviewItemDto> ContinueItems { get; init; } = [];

    [JsonPropertyName("completed_items")]
    public List<ProfileOverviewItemDto> CompletedItems { get; init; } = [];

    [JsonPropertyName("recently_added_items")]
    public List<ProfileOverviewItemDto> RecentlyAddedItems { get; init; } = [];

    [JsonPropertyName("activity")]
    public List<ProfileOverviewActivityDto> Activity { get; init; } = [];

    [JsonPropertyName("taste")]
    public TasteProfileDto? Taste { get; init; }
}

public enum TasteProfileBuildStatusDto
{
    Generated,
    InsufficientData,
}

public sealed class TasteProfileDto
{
    public Guid UserId { get; init; }
    public IReadOnlyDictionary<string, double> GenreDistribution { get; init; } =
        new Dictionary<string, double>();
    public IReadOnlyDictionary<string, double> EraPreferences { get; init; } =
        new Dictionary<string, double>();
    public IReadOnlyDictionary<string, double> MediaTypeMix { get; init; } =
        new Dictionary<string, double>();
    public IReadOnlyDictionary<string, double> MoodPreferences { get; init; } =
        new Dictionary<string, double>();
    public string? Summary { get; init; }
    public DateTimeOffset LastUpdatedAt { get; init; }
}

public sealed record TasteProfileBuildResponse(
    TasteProfileBuildStatusDto Status,
    Guid UserId,
    TasteProfileDto? Profile,
    int SignalCount,
    string InputFingerprint,
    string? Reason = null);

public sealed class ProfileOverviewStatsDto
{
    [JsonPropertyName("total_items")]
    public int TotalItems { get; init; }

    [JsonPropertyName("in_progress")]
    public int InProgress { get; init; }

    [JsonPropertyName("completed")]
    public int Completed { get; init; }

    [JsonPropertyName("recent_activity")]
    public int RecentActivity { get; init; }

    [JsonPropertyName("media_type_mix")]
    public Dictionary<string, int> MediaTypeMix { get; init; } = [];

    [JsonPropertyName("library_counts")]
    public Dictionary<string, int> LibraryCounts { get; init; } = [];

    [JsonPropertyName("activity_buckets")]
    public Dictionary<string, int> ActivityBuckets { get; init; } = [];

    [JsonPropertyName("top_genres")]
    public Dictionary<string, int> TopGenres { get; init; } = [];

    [JsonPropertyName("consumed_seconds")]
    public double ConsumedSeconds { get; init; }

    [JsonPropertyName("consumed_seconds_by_media_type")]
    public Dictionary<string, double> ConsumedSecondsByMediaType { get; init; } = [];
}

public sealed class ProfileOverviewItemDto
{
    [JsonPropertyName("asset_id")]
    public Guid AssetId { get; init; }

    [JsonPropertyName("work_id")]
    public Guid? WorkId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; init; } = "Media";

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }

    [JsonPropertyName("collection_name")]
    public string? CollectionName { get; init; }

    [JsonPropertyName("genre")]
    public string? Genre { get; init; }

    [JsonPropertyName("route")]
    public string? Route { get; init; }

    [JsonPropertyName("position_seconds")]
    public double? PositionSeconds { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("progress_pct")]
    public double ProgressPct { get; init; }

    [JsonPropertyName("last_accessed")]
    public DateTimeOffset LastAccessed { get; init; }

    [JsonPropertyName("added_at")]
    public DateTimeOffset? AddedAt { get; init; }
}

public sealed class ProfileOverviewActivityDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("action_type")]
    public string ActionType { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("entity_id")]
    public Guid? EntityId { get; init; }
}

public sealed record UpdateProfileExperienceRequest(
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string AvatarColor,
    [property: JsonPropertyName("navigation_config")] string? NavigationConfig = null);

/// <summary>
/// Administrator-managed access policy for a profile's personal View space.
/// Access to the shared aggregate and contribution to it are intentionally
/// independent decisions.
/// </summary>
public sealed class ViewProfilePolicyDto
{
    [JsonPropertyName("profile_id")]
    public Guid ProfileId { get; init; }

    [JsonPropertyName("view_enabled")]
    public bool ViewEnabled { get; init; }

    [JsonPropertyName("access_shared_library")]
    public bool AccessSharedLibrary { get; init; }

    [JsonPropertyName("submit_to_shared_library")]
    public bool SubmitToSharedLibrary { get; init; }

    [JsonPropertyName("allow_gallery_sharing")]
    public bool AllowGallerySharing { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class UpdateViewProfilePolicyRequest
{
    [JsonPropertyName("view_enabled")]
    public bool ViewEnabled { get; init; }

    [JsonPropertyName("access_shared_library")]
    public bool AccessSharedLibrary { get; init; }

    [JsonPropertyName("submit_to_shared_library")]
    public bool SubmitToSharedLibrary { get; init; }

    [JsonPropertyName("allow_gallery_sharing")]
    public bool AllowGallerySharing { get; init; }
}
