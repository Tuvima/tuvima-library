namespace MediaEngine.Contracts.Artwork;

public enum ArtworkResolutionMode
{
    None,
    Explicit,
    AutomaticGroup,
}

public enum ArtworkPickerScope
{
    All,
    Recommended,
    Related,
}

public enum ArtworkUsageFilter
{
    All,
    Linked,
    Selected,
    Unlinked,
}

public enum ArtworkAssetSort
{
    Relevance,
    Newest,
    RecentlyUpdated,
    Resolution,
}

public sealed record ArtworkAssetQuery(
    string? Search = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Aspects = null,
    IReadOnlyList<string>? MediaTypes = null,
    IReadOnlyList<string>? SourceProviders = null,
    IReadOnlyList<string>? Years = null,
    IReadOnlyList<string>? EntityTypes = null,
    string? RelatedEntityType = null,
    Guid? RelatedEntityId = null,
    string? TargetEntityType = null,
    Guid? TargetEntityId = null,
    string? TargetRole = null,
    string? TargetSourceAssetType = null,
    ArtworkPickerScope PickerScope = ArtworkPickerScope.All,
    ArtworkUsageFilter Usage = ArtworkUsageFilter.All,
    ArtworkAssetSort Sort = ArtworkAssetSort.Relevance,
    int? MinimumWidth = null,
    int? MinimumHeight = null,
    int Offset = 0,
    int Limit = 48);

public sealed record ArtworkLibraryPreviewItemDto(
    Guid WorkId,
    Guid? AssetId,
    string Title,
    string ImageUrl,
    string Shape,
    string? Position,
    string? MediaType);

public sealed record ArtworkBrowsePageDto(
    IReadOnlyList<ArtworkLibraryItemDto> Items,
    int Offset,
    int Limit,
    int Total)
{
    public bool HasMore => Offset + Items.Count < Total;
}

public sealed record ArtworkLibraryItemDto(
    Guid EntityId,
    string EntityType,
    string DisplayTitle,
    string? MediaType,
    string? Year,
    string? Subtitle,
    string? ImageUrl,
    string? PrimaryAssetType,
    IReadOnlyList<string> AssetTypes,
    int VariantCount,
    int OwnedWorkCount,
    bool UsesLegacyPersonImage)
{
    public bool IsStructural { get; init; }
    public string? GroupKind { get; init; }
    public ArtworkResolutionMode ResolutionMode { get; init; }
    public IReadOnlyList<ArtworkLibraryPreviewItemDto> PreviewItems { get; init; } = [];
    public string? BackgroundImageUrl { get; init; }
    public string? LogoImageUrl { get; init; }
    public string? CanonicalId { get; init; }
}

public sealed record ArtworkAssetContextDto(
    Guid EntityId,
    string EntityType,
    string EntityLabel,
    string? MediaType,
    string? Year,
    string? Role,
    string? Provider)
{
    public string? CanonicalId { get; init; }
    public string RelationshipKind { get; init; } = "Assignment";
    public string? MatchReason { get; init; }
}

public sealed record ArtworkAssetDto(
    Guid Id,
    string ContentUrl,
    string ThumbnailUrl,
    int? Width,
    int? Height,
    string Aspect,
    string? SourceProvider,
    string? SourceUrl,
    IReadOnlyList<ArtworkAssetContextDto> Contexts,
    bool AlreadyLinked = false)
{
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int LinkCount { get; init; }
    public int PreferredLinkCount { get; init; }
    public bool IsPreferredForTarget { get; init; }
    public ArtworkAssetContextDto? DisplayContext { get; init; }
    public string? MatchExplanation { get; init; }
}

public sealed record ArtworkAssetPageDto(
    IReadOnlyList<ArtworkAssetDto> Items,
    int Offset,
    int Limit,
    int Total)
{
    public bool HasMore => Offset + Items.Count < Total;
}

public sealed record ArtworkEntityVariantDto(
    Guid LinkId,
    Guid ArtworkAssetId,
    string Role,
    string? Context,
    string? SourceAssetType,
    bool IsPreferred,
    bool IsUserOverride,
    string ContentUrl,
    string ThumbnailUrl,
    int? Width,
    int? Height,
    string Aspect,
    string? SourceProvider,
    string? SourceUrl);

public sealed record ArtworkRoleDescriptorDto(
    string Role,
    string? SourceAssetType,
    string PresentationKey,
    bool IsDefault = false,
    bool SupportsAutomatic = false);

public sealed record ArtworkEntityWorkspaceDto(
    Guid EntityId,
    string EntityType,
    IReadOnlyList<ArtworkEntityVariantDto> Variants)
{
    public IReadOnlyList<ArtworkRoleDescriptorDto> SupportedRoles { get; init; } = [];
}

public sealed record ArtworkLinkRequest(
    Guid ArtworkAssetId,
    string Role,
    string? Context = null,
    bool Preferred = true,
    string? EntityLabel = null,
    string? MediaType = null,
    string? Year = null,
    string? SourceAssetType = null);

public sealed record ArtworkFromUrlRequest(
    string Url,
    string Role,
    string? Context = null,
    bool Preferred = true,
    string? EntityLabel = null,
    string? MediaType = null,
    string? Year = null,
    string? SourceAssetType = null);

public static class ArtworkRoleCatalog
{
    public static IReadOnlyList<ArtworkRoleDescriptorDto> Resolve(
        string entityType,
        string? mediaType,
        string? groupKind,
        IReadOnlyCollection<string>? advertisedAssetTypes = null)
    {
        var normalizedEntity = entityType.Trim();
        var normalizedMedia = mediaType?.Trim() ?? string.Empty;
        var normalizedGroup = groupKind?.Trim() ?? string.Empty;

        if (normalizedEntity.Equals("Edition", StringComparison.OrdinalIgnoreCase))
            return [new("Primary", "CoverArt", "cover", IsDefault: true)];

        if (normalizedEntity.Equals("Person", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("Portrait", "Headshot", "portrait", IsDefault: true),
                new("Background", "Background", "background"),
                new("Logo", "Logo", "logo"),
            ];
        }

        if (normalizedEntity.Equals("FictionalEntity", StringComparison.OrdinalIgnoreCase)
            || normalizedGroup.Equals("Character", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("Portrait", "CharacterPortrait", "portrait", IsDefault: true),
                new("Background", "Background", "background"),
            ];
        }

        if (normalizedGroup.Equals("Episode", StringComparison.OrdinalIgnoreCase)
            || Has(advertisedAssetTypes, "EpisodeStill"))
        {
            return [new("Primary", "EpisodeStill", "still", IsDefault: true)];
        }

        if (normalizedGroup.Equals("Season", StringComparison.OrdinalIgnoreCase)
            || Has(advertisedAssetTypes, "SeasonPoster"))
        {
            return
            [
                new("Primary", "SeasonPoster", "poster", IsDefault: true),
                new("Background", "SeasonThumb", "background"),
            ];
        }

        if (normalizedGroup.Equals("Universe", StringComparison.OrdinalIgnoreCase))
        {
            return Standard("primary-artwork", supportsAutomatic: true);
        }

        if (normalizedEntity.Equals("Collection", StringComparison.OrdinalIgnoreCase))
        {
            return Standard("poster-cover", supportsAutomatic: true);
        }

        if (normalizedMedia.Contains("book", StringComparison.OrdinalIgnoreCase)
            || normalizedMedia.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            var roles = new List<ArtworkRoleDescriptorDto>
            {
                new("Primary", "CoverArt", "cover", IsDefault: true),
                new("Background", "Background", "background"),
            };
            if (Has(advertisedAssetTypes, "Logo")) roles.Add(new("Logo", "Logo", "logo"));
            return roles;
        }

        if (normalizedMedia.Contains("music", StringComparison.OrdinalIgnoreCase)
            || normalizedMedia.Contains("album", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("Primary", "CoverArt", "cover", IsDefault: true),
                new("Background", "Background", "background"),
                new("Logo", "Logo", "logo"),
            ];
        }

        if (normalizedMedia.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new("Primary", "CoverArt", "cover", IsDefault: true),
                new("Background", "Background", "background"),
            ];
        }

        return Standard("poster-cover");
    }

    private static IReadOnlyList<ArtworkRoleDescriptorDto> Standard(string primaryPresentationKey, bool supportsAutomatic = false) =>
    [
        new("Primary", "CoverArt", primaryPresentationKey, IsDefault: true, SupportsAutomatic: supportsAutomatic),
        new("Background", "Background", "background"),
        new("Logo", "Logo", "logo"),
    ];

    private static bool Has(IReadOnlyCollection<string>? values, string value) =>
        values?.Contains(value, StringComparer.OrdinalIgnoreCase) == true;
}
