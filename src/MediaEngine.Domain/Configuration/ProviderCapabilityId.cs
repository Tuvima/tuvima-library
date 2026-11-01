namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Stable contribution identifiers declared by provider manifests. These describe
/// what a provider can supply without exposing adapter or pipeline implementation details.
/// </summary>
public static class ProviderCapabilityId
{
    public const string Identity = "identity";
    public const string Metadata = "metadata";
    public const string Artwork = "artwork";
    public const string Lyrics = "lyrics";
    public const string Subtitles = "subtitles";
    public const string Ratings = "ratings";
    public const string People = "people";
    public const string Relationships = "relationships";
    public const string Lists = "lists";
    public const string Other = "other";
}
