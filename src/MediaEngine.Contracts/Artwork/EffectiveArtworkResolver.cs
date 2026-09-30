namespace MediaEngine.Contracts.Artwork;

/// <summary>
/// Resolves presentation artwork from an owned child's explicit selection and
/// its verified parent scopes. Callers must supply the canonical ancestry;
/// this class never infers ownership from titles or provider search results.
/// </summary>
public static class EffectiveArtworkResolver
{
    public static EffectiveArtworkSelection Resolve(
        string role,
        string? sourceAssetType,
        ArtworkEntityWorkspaceDto child,
        IReadOnlyList<ArtworkEntityWorkspaceDto> parents)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parents);

        var own = Preferred(child, role, sourceAssetType);
        if (own is not null)
            return new(own, child.EntityType, child.EntityId, IsInherited: false);

        // An episode still is a child-specific image, never a season/show cover.
        // Similarly, a season poster must not be used as an episode still.
        if (string.Equals(sourceAssetType, "EpisodeStill", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Still", StringComparison.OrdinalIgnoreCase))
            return new(null, child.EntityType, child.EntityId, IsInherited: false);

        foreach (var parent in parents)
        {
            var inherited = Preferred(parent, role, sourceAssetType)
                ?? (sourceAssetType switch
                {
                    "SeasonPoster" => Preferred(parent, role, "CoverArt"),
                    "SeasonThumb" => Preferred(parent, role, "Background"),
                    _ => null,
                });
            if (inherited is not null)
                return new(inherited, parent.EntityType, parent.EntityId, IsInherited: true);
        }

        return new(null, child.EntityType, child.EntityId, IsInherited: false);
    }

    private static ArtworkEntityVariantDto? Preferred(
        ArtworkEntityWorkspaceDto workspace,
        string role,
        string? sourceAssetType) => workspace.Variants.FirstOrDefault(variant =>
            variant.IsPreferred
            && string.Equals(variant.Role, role, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(sourceAssetType)
                || string.IsNullOrWhiteSpace(variant.SourceAssetType)
                || string.Equals(variant.SourceAssetType, sourceAssetType, StringComparison.OrdinalIgnoreCase)));
}

public sealed record EffectiveArtworkSelection(
    ArtworkEntityVariantDto? Variant,
    string SourceEntityType,
    Guid SourceEntityId,
    bool IsInherited);
