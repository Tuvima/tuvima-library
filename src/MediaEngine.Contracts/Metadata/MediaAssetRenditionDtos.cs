namespace MediaEngine.Contracts.Metadata;

/// <summary>
/// Delivery characteristics for one owned file. These fields never identify a
/// Work or Edition and are not a canonical preferred-playback choice.
/// </summary>
public sealed record MediaAssetRenditionDto(
    Guid AssetId,
    Guid EditionId,
    string Purpose,
    Guid? DerivedFromAssetId,
    string? EncoderProfileVersion,
    int? Width,
    int? Height,
    long? BitrateBitsPerSecond,
    string? VideoCodec,
    string? AudioCodec,
    string? DynamicRange,
    string? AudioLayout,
    DateTimeOffset? GeneratedAt,
    string? SourceFingerprint);

public sealed record UpdateMediaAssetRenditionRequestDto(
    string Purpose,
    Guid? DerivedFromAssetId,
    string? EncoderProfileVersion,
    int? Width,
    int? Height,
    long? BitrateBitsPerSecond,
    string? VideoCodec,
    string? AudioCodec,
    string? DynamicRange,
    string? AudioLayout,
    DateTimeOffset? GeneratedAt,
    string? SourceFingerprint);
