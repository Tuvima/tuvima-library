using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain;

/// <summary>
/// Helper methods for persisting artwork-truth canonicals.
/// </summary>
public static class ArtworkCanonicalHelper
{
    public static List<CanonicalValue> CreateFlags(
        Guid entityId,
        string coverState,
        string? coverSource,
        string heroState,
        DateTimeOffset lastScoredAt,
        bool settled)
    {
        var values = new List<CanonicalValue>
        {
            Create(entityId, MetadataFieldConstants.CoverState, coverState, lastScoredAt),
            Create(entityId, MetadataFieldConstants.HeroState, heroState, lastScoredAt),
        };

        if (!string.IsNullOrWhiteSpace(coverSource))
        {
            values.Add(Create(entityId, MetadataFieldConstants.CoverSource, coverSource, lastScoredAt));
        }

        if (settled)
        {
            values.Add(Create(entityId, MetadataFieldConstants.ArtworkSettledAt, lastScoredAt.ToString("o"), lastScoredAt));
        }

        return values;
    }

    public static CanonicalValue Create(Guid entityId, string key, string value, DateTimeOffset lastScoredAt) => new()
    {
        EntityId = entityId,
        Key = key,
        Value = value,
        LastScoredAt = lastScoredAt,
    };

    /// <summary>
    /// Canonical holding the bridge id (TMDB or TheTVDB id) for which that provider's images pass last chose
    /// the owner's poster. Written only when the pass actually chose one, never by a pass that skipped or failed.
    /// </summary>
    public static string PosterChoiceKey(string provider) => $"{provider}_artwork_poster_bridge_id";

    /// <summary>Address prefix of a stored artwork variant, as written into display canonicals.</summary>
    public const string StoredArtworkUrlPrefix = "/stream/artwork/";

    /// <summary>
    /// Reads the variant id from a stored artwork address (<c>/stream/artwork/{id}</c>, with or without a
    /// size query). Provider URLs and other values return false.
    /// </summary>
    public static bool TryParseStoredArtworkId(string? url, out Guid assetId)
    {
        assetId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(url)
            || !url.StartsWith(StoredArtworkUrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var idPart = url.AsSpan(StoredArtworkUrlPrefix.Length);
        var queryStart = idPart.IndexOf('?');
        return Guid.TryParse(queryStart >= 0 ? idPart[..queryStart] : idPart, out assetId);
    }

    /// <summary>Display canonicals written for an owner's preferred variant of <paramref name="assetType"/>.</summary>
    public static IReadOnlyList<string> DisplayCanonicalKeys(string assetType)
    {
        var prefix = assetType switch
        {
            "CoverArt" => "cover",
            "Background" => "background",
            "Logo" => "logo",
            "SeasonPoster" => "season_poster",
            "SeasonThumb" => "season_thumb",
            "EpisodeStill" => "episode_still",
            _ => null,
        };
        if (prefix is null)
        {
            return [];
        }

        var keys = new List<string>
        {
            $"{prefix}_url_s",
            $"{prefix}_url_m",
            $"{prefix}_url_l",
            $"{prefix}_aspect_class",
            $"{prefix}_width_px",
            $"{prefix}_height_px",
            $"{prefix}_primary_hex",
            $"{prefix}_secondary_hex",
            $"{prefix}_accent_hex",
        };
        keys.AddRange(assetType switch
        {
            "CoverArt" => [MetadataFieldConstants.CoverUrl],
            "Background" => ["background", "background_url"],
            "Logo" => ["logo", "logo_url"],
            "SeasonPoster" => ["season_poster", "season_poster_url"],
            "SeasonThumb" => ["season_thumb", "season_thumb_url"],
            "EpisodeStill" => ["episode_still", "episode_still_url"],
            _ => [],
        });
        if (assetType == "CoverArt")
        {
            keys.AddRange(
            [
                MetadataFieldConstants.ArtworkPrimaryHex,
                MetadataFieldConstants.ArtworkSecondaryHex,
                MetadataFieldConstants.ArtworkAccentHex,
                "dominant_color",
            ]);
        }

        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<CanonicalValue> CreatePreferredAssetCanonicals(
        Guid entityId,
        EntityAsset asset,
        DateTimeOffset lastScoredAt)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var values = new List<CanonicalValue>();
        var baseUrl = $"{StoredArtworkUrlPrefix}{asset.Id}";
        var hasRenditions = ShouldGenerateRenditions(asset.AssetTypeValue);
        var smallUrl = $"{baseUrl}?size=s";
        var mediumUrl = $"{baseUrl}?size=m";
        var largeUrl = $"{baseUrl}?size=l";

        foreach (var key in ResolvePrimaryUrlKeys(asset.AssetTypeValue))
        {
            values.Add(Create(entityId, key, baseUrl, lastScoredAt));
        }

        var prefix = ResolveMetadataPrefix(asset.AssetTypeValue);
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            if (hasRenditions)
            {
                values.Add(Create(entityId, $"{prefix}_url_s", smallUrl, lastScoredAt));
                values.Add(Create(entityId, $"{prefix}_url_m", mediumUrl, lastScoredAt));
                values.Add(Create(entityId, $"{prefix}_url_l", largeUrl, lastScoredAt));
            }

            if (!string.IsNullOrWhiteSpace(asset.AspectClass))
            {
                values.Add(Create(entityId, $"{prefix}_aspect_class", asset.AspectClass, lastScoredAt));
            }

            if (asset.WidthPx is > 0)
            {
                values.Add(Create(entityId, $"{prefix}_width_px", asset.WidthPx.Value.ToString(), lastScoredAt));
            }

            if (asset.HeightPx is > 0)
            {
                values.Add(Create(entityId, $"{prefix}_height_px", asset.HeightPx.Value.ToString(), lastScoredAt));
            }

            if (!string.IsNullOrWhiteSpace(asset.PrimaryHex))
            {
                values.Add(Create(entityId, $"{prefix}_primary_hex", asset.PrimaryHex, lastScoredAt));
            }

            if (!string.IsNullOrWhiteSpace(asset.SecondaryHex))
            {
                values.Add(Create(entityId, $"{prefix}_secondary_hex", asset.SecondaryHex, lastScoredAt));
            }

            if (!string.IsNullOrWhiteSpace(asset.AccentHex))
            {
                values.Add(Create(entityId, $"{prefix}_accent_hex", asset.AccentHex, lastScoredAt));
            }
        }

        if (!string.IsNullOrWhiteSpace(asset.PrimaryHex))
        {
            values.Add(Create(entityId, MetadataFieldConstants.ArtworkPrimaryHex, asset.PrimaryHex, lastScoredAt));
        }

        if (!string.IsNullOrWhiteSpace(asset.SecondaryHex))
        {
            values.Add(Create(entityId, MetadataFieldConstants.ArtworkSecondaryHex, asset.SecondaryHex, lastScoredAt));
        }

        if (!string.IsNullOrWhiteSpace(asset.AccentHex))
        {
            values.Add(Create(entityId, MetadataFieldConstants.ArtworkAccentHex, asset.AccentHex, lastScoredAt));
            values.Add(Create(entityId, "dominant_color", asset.AccentHex, lastScoredAt));
        }

        return values;
    }

    private static IReadOnlyList<string> ResolvePrimaryUrlKeys(string assetTypeValue) => assetTypeValue switch
    {
        "CoverArt" => ["cover_url"],
        "Background" => ["background", "background_url"],
        "Logo" => ["logo", "logo_url"],
        "NetworkLogo" => ["network_logo", "network_logo_url"],
        "StudioLogo" => ["studio_logo", "studio_logo_url"],
        "Headshot" => ["artist_photo_url", "headshot_url"],
        "SeasonPoster" => ["season_poster", "season_poster_url"],
        "SeasonThumb" => ["season_thumb", "season_thumb_url"],
        "EpisodeStill" => ["episode_still", "episode_still_url"],
        "CharacterPortrait" => ["character_portrait", "character_portrait_url"],
        _ => [],
    };

    private static string? ResolveMetadataPrefix(string assetTypeValue) => assetTypeValue switch
    {
        "CoverArt" => "cover",
        "Background" => "background",
        "Logo" => "logo",
        "NetworkLogo" => "network_logo",
        "StudioLogo" => "studio_logo",
        "Headshot" => "artist_photo",
        "SeasonPoster" => "season_poster",
        "SeasonThumb" => "season_thumb",
        "EpisodeStill" => "episode_still",
        "CharacterPortrait" => "character_portrait",
        _ => null,
    };

    private static bool ShouldGenerateRenditions(string assetTypeValue) => assetTypeValue switch
    {
        "Logo" or "NetworkLogo" or "StudioLogo" => false,
        _ => true,
    };
}
