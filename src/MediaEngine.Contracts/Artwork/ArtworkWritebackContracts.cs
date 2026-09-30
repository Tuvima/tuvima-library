using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Artwork;

/// <summary>The physical-file result for a preferred inherited artwork choice.</summary>
public sealed record ArtworkWritebackStatusDto(
    Guid MediaAssetId,
    string Status,
    Guid? DesiredArtworkAssetId,
    Guid? EmbeddedArtworkAssetId,
    Guid? ArtworkOwnerId,
    string? Reason,
    int Attempts,
    DateTimeOffset? UpdatedAt)
{
    [JsonIgnore]
    public string? DesiredVersion { get; init; }
}

public sealed record ArtworkWritebackSettingsDto(bool MetadataWritebackEnabled, bool ArtworkEnabled);

public sealed record ArtworkWritebackStatusesRequestDto(IReadOnlyList<Guid> MediaAssetIds);
public sealed record UpdateArtworkWritebackSettingsDto(bool ArtworkEnabled);
