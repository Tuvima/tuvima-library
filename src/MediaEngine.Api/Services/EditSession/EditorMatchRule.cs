using MediaEngine.Domain;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The one rule for what "matched" means in the editor (universal editor v6 D10). The Match tab's own read (<see cref="EditorCurrentMatchService"/>)
/// and the navigation read behind the family dropdowns and the header (<c>MediaEditorNavigationReadService</c>) both take their answer from here, so
/// they can never disagree about which identifier keys count, where an item's identifiers are read from, or what an episode inherits from its show.
/// <list type="bullet">
/// <item>An item's own values are its Work's, with its representative file's (its asset's) filling what the Work lacks; a container (a show, a
/// season, an album) has no file of its own, so its values are never filled from the file the launch carries.</item>
/// <item>The first file of a Work is the one with the lowest asset id (<see cref="MediaEngine.Storage.EditorFirstFileRule"/>).</item>
/// <item>"Provider matched" is <see cref="FindProviderSource"/> finding a source: a provider identifier (or an ISBN) on the item itself, plus the show's
/// own id for a season or show (never for an episode, which has lost its match when its own id is gone).</item>
/// <item>"Carries an identity" (the family dropdown's mark) is wider: any key in <see cref="IdentityKeys"/> on the item or on the show, album or Series
/// it sits under. A Wikidata id alone makes an item carry an identity but not a provider match.</item>
/// </list>
/// </summary>
internal static class EditorMatchRule
{
    public const string TvdbProvider = "tvdb";

    private const string ContainerWorkKind = "parent";

    /// <summary>The identifier keys that make an item matched to a provider's record.</summary>
    public static readonly IReadOnlyList<string> ProviderIdKeys =
    [
        BridgeIdKeys.TvdbEpisodeId, BridgeIdKeys.TvdbSeasonId, BridgeIdKeys.TvdbId, BridgeIdKeys.TmdbId,
        BridgeIdKeys.MusicBrainzReleaseGroupId, BridgeIdKeys.MusicBrainzRecordingId, BridgeIdKeys.MusicBrainzReleaseId,
        BridgeIdKeys.ComicVineId, BridgeIdKeys.OpenLibraryId, BridgeIdKeys.AppleBooksId,
    ];

    /// <summary>An ISBN names a book without being a store's id; it still makes the item matched to "ISBN", the last source tried.</summary>
    public static readonly IReadOnlyList<string> IsbnKeys = [BridgeIdKeys.Isbn13, BridgeIdKeys.Isbn10, BridgeIdKeys.Isbn];

    /// <summary>Every key that decides <see cref="FindProviderSource"/>.</summary>
    public static readonly IReadOnlyList<string> ProviderMatchKeys = [.. ProviderIdKeys, .. IsbnKeys];

    /// <summary>The keys the family dropdown has always counted as "carries an identity": the provider ones, two older spellings and the Wikidata id.</summary>
    public static readonly IReadOnlyList<string> IdentityKeys = [.. ProviderIdKeys, "comicvine_issue_id", "openlibrary_work_id", BridgeIdKeys.WikidataQid];

    /// <summary>A container (a show, a season, an album) has no file of its own: the asset a launch carries there is some child's.</summary>
    public static bool IsContainer(string? workKind) => string.Equals(workKind, ContainerWorkKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>An item's own values: the Work's, with the representative asset's filling what the Work does not have (never for a container).</summary>
    public static IReadOnlyDictionary<string, string> OwnValues(
        IReadOnlyDictionary<string, string> work, IReadOnlyDictionary<string, string>? asset, string? workKind)
    {
        if (asset is null || asset.Count == 0 || IsContainer(workKind))
        {
            return work;
        }

        var merged = new Dictionary<string, string>(work, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in asset)
        {
            merged.TryAdd(key, value);
        }

        return merged;
    }

    public sealed record ProviderSource(
        string ProviderId, string ProviderName, string ItemId, string Kind, string? Url, bool CanLookUp);

    /// <summary>
    /// The provider id the Engine stored for this item at its own level: an episode by its episode id, a season by its season id, a show by its
    /// show id. Anything else is read from what the Engine stored. Null when the item has no provider match.
    /// </summary>
    public static ProviderSource? FindProviderSource(
        string mediaType, bool isTv, bool isEpisode, IReadOnlyDictionary<string, string> own, IReadOnlyDictionary<string, string> root)
    {
        if (isTv)
        {
            if (Value(own, BridgeIdKeys.TvdbEpisodeId) is { } episode)
            {
                return new ProviderSource(TvdbProvider, "TheTVDB", episode, "Episode", OutsideLinks.TvdbEpisode(episode), true);
            }

            if (Value(own, BridgeIdKeys.TvdbSeasonId) is { } season)
            {
                return new ProviderSource(TvdbProvider, "TheTVDB", season, "Season", OutsideLinks.TvdbSeason(season), true);
            }

            // An episode that has lost its own match (Remove match) is not matched to its show: the show's id is the show's, so the episode
            // reads "Not matched yet" instead of borrowing it. A season keeps showing the show it belongs to.
            if (!isEpisode && (Value(own, BridgeIdKeys.TvdbId) ?? Value(root, BridgeIdKeys.TvdbId)) is { } show)
            {
                return new ProviderSource(TvdbProvider, "TheTVDB", show, "Show", OutsideLinks.TvdbSeries(show), true);
            }

            if ((Value(own, BridgeIdKeys.TmdbId) ?? (isEpisode ? null : Value(root, BridgeIdKeys.TmdbId))) is { } tmdbShow)
            {
                return new ProviderSource("tmdb", "TMDB", tmdbShow, "Show", OutsideLinks.Tmdb(tmdbShow, "TV"), false);
            }

            return null;
        }

        if (Value(own, BridgeIdKeys.TmdbId) is { } tmdb)
        {
            return new ProviderSource("tmdb", "TMDB", tmdb, "Film", OutsideLinks.Tmdb(tmdb, mediaType), false);
        }

        if (Value(own, BridgeIdKeys.MusicBrainzReleaseGroupId) is { } group)
        {
            return new ProviderSource("musicbrainz", "MusicBrainz", group, "Album", OutsideLinks.MusicBrainz("release-group", group), false);
        }

        if (Value(own, BridgeIdKeys.MusicBrainzRecordingId) is { } recording)
        {
            return new ProviderSource("musicbrainz", "MusicBrainz", recording, "Song", OutsideLinks.MusicBrainz("recording", recording), false);
        }

        if (Value(own, BridgeIdKeys.MusicBrainzReleaseId) is { } release)
        {
            return new ProviderSource("musicbrainz", "MusicBrainz", release, "Album", OutsideLinks.MusicBrainz("release", release), false);
        }

        if (Value(own, BridgeIdKeys.ComicVineId) is { } issue)
        {
            return new ProviderSource("comicvine", "Comic Vine", issue, "Issue", OutsideLinks.ComicVineIssue(issue), false);
        }

        if (Value(own, BridgeIdKeys.OpenLibraryId) is { } openLibrary)
        {
            return new ProviderSource("openlibrary", "Open Library", openLibrary, "Book", OutsideLinks.OpenLibraryWork(openLibrary), false);
        }

        if (Value(own, BridgeIdKeys.AppleBooksId) is { } apple)
        {
            return new ProviderSource("apple_books", "Apple Books", apple, "Book", OutsideLinks.AppleBooks(apple), false);
        }

        var isbn = Value(own, BridgeIdKeys.Isbn13) ?? Value(own, BridgeIdKeys.Isbn10) ?? Value(own, BridgeIdKeys.Isbn);
        return isbn is null ? null : new ProviderSource("isbn", "ISBN", isbn, "Book", null, false);
    }

    public static string? Value(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
