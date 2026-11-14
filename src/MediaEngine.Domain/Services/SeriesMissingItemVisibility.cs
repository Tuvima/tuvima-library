using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Services;

/// <summary>
/// The one place that names where a profile's own "Show missing" tick for a Series is kept. The Series panel on item
/// pages (Dashboard) and the Universe page read (Engine) both call it, so they cannot drift apart.
/// </summary>
/// <remarks>
/// There is no Library-wide setting any more (spec details-page-bottom D12): a Series starts unticked and the profile's
/// own tick, stored per profile and Series, is the only thing that decides whether its missing entries show.
/// </remarks>
public static class SeriesMissingItemVisibility
{
    /// <summary>The key a media type is stored under with a profile's tick; null for a media type that has none.</summary>
    public static string? PolicyKey(MediaType mediaType) => mediaType switch
    {
        MediaType.Books => "books",
        MediaType.Audiobooks => "audiobooks",
        MediaType.Movies => "movies",
        MediaType.TV => "tv",
        MediaType.Music => "music",
        MediaType.Comics => "comics",
        _ => null,
    };

    /// <summary>The key for a media type written as a format or type string ("Books", "Movie", "TV", …).</summary>
    public static string? PolicyKey(string? mediaType) => PolicyKey(MediaTypeClassifier.Classify(mediaType));

    /// <summary>
    /// The identities a profile's own choice for a Series can be stored under, most specific first: the Series' own
    /// collection id, then its Wikidata QID. The Series panel keys its choice by the collection id when the Series is
    /// local and by the QID when the Series is known only from Wikidata, so a reader must try them in this order.
    /// </summary>
    public static IReadOnlyList<string> OverrideKeys(Guid? collectionId, string? seriesQid)
    {
        var keys = new List<string>(2);
        if (collectionId is { } id && id != Guid.Empty)
        {
            keys.Add(id.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(seriesQid))
        {
            keys.Add(seriesQid.Trim());
        }

        return keys;
    }
}
