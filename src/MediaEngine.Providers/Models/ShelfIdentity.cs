using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;

namespace MediaEngine.Providers.Models;

internal sealed record ShelfIdentity(
    MediaType MediaType,
    string Label,
    string? Qid,
    string? ProviderKey,
    IReadOnlyList<string> RelationshipKeys)
{
    /// <summary>The Series key: the Wikidata Series QID plus the media type, or the provider key when there is no QID.</summary>
    public string LockKey => ToIdentity().Key;

    /// <summary>The same identity in the form <see cref="SeriesPlacementRule"/> reads.</summary>
    public SeriesShelfIdentity ToIdentity() => new(MediaType.ToString(), Label, Qid, ProviderKey);
}
