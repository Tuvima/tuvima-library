using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Services;

/// <summary>
/// The one rule for whether a person sees the entries of a Series the Library does not own. The Series panel on item
/// pages (Dashboard) and the Universe page read (Engine) both call it, so they cannot drift apart.
/// </summary>
/// <remarks>
/// A person sees missing entries for a Series when that media type's policy shows missing entries at all
/// (<c>enabled</c>), the Series has some missing, and either the person chose so for this Series or, with no choice of
/// their own, the media default is <c>shown</c>.
/// </remarks>
public static class SeriesMissingItemVisibility
{
    /// <summary>The key of a media type in the library preferences' <c>missing_item_display</c>; null for a media type that has none.</summary>
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

    /// <summary>The policy key for a media type written as a format or type string ("Books", "Movie", "TV", …).</summary>
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

    /// <summary>Whether a media default of <paramref name="defaultVisibility"/> (<c>shown</c> or <c>hidden</c>) shows missing entries.</summary>
    public static bool IsDefaultShown(string? defaultVisibility) =>
        string.Equals(defaultVisibility, "shown", StringComparison.OrdinalIgnoreCase);

    /// <summary>The visibility the person has before any choice of their own: the media default when the policy is enabled.</summary>
    public static bool Resolve(bool policyEnabled, string? defaultVisibility, bool? profileOverride, int missingCount) =>
        policyEnabled
        && missingCount > 0
        && (profileOverride ?? IsDefaultShown(defaultVisibility));
}
