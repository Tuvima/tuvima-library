using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Services;

/// <summary>
/// The one place that builds web addresses for outside sources (TheTVDB, TMDB,
/// MusicBrainz, Wikidata, Comic Vine and friends). Every method returns <c>null</c>
/// when the identifier is missing or malformed, so a link that cannot open is never
/// shown. Provider config templates are expanded through <see cref="Expand"/> so the
/// same validation applies to them.
/// </summary>
public static partial class OutsideLinks
{
    // TheTVDB's own redirect-by-number address. Its website names shows by a short
    // slug, so number-only paths such as /series/{id} are unreliable. WP1 of the
    // outside-source-links spec verifies this on a real machine; if a kind fails,
    // change it here (and in config/providers/tvdb.json, which a test keeps in step).
    private const string TvdbBase = "https://thetvdb.com/dereferrer";

    public static string? TvdbSeries(string? id) => Numeric(id) is { } n ? $"{TvdbBase}/series/{n}" : null;

    public static string? TvdbSeason(string? id) => Numeric(id) is { } n ? $"{TvdbBase}/season/{n}" : null;

    public static string? TvdbEpisode(string? id) => Numeric(id) is { } n ? $"{TvdbBase}/episode/{n}" : null;

    public static string? TvdbPerson(string? id) => Numeric(id) is { } n ? $"{TvdbBase}/people/{n}" : null;

    /// <summary>TheTVDB page for a provider scope: <c>season</c>, <c>episode</c>, otherwise the series.</summary>
    public static string? TvdbForScope(string? scope, string? id) => scope?.Trim().ToLowerInvariant() switch
    {
        "season" => TvdbSeason(id),
        "episode" => TvdbEpisode(id),
        _ => TvdbSeries(id),
    };

    public static string? Tmdb(string? id, string? mediaType)
        => Numeric(id) is { } n
            ? $"https://www.themoviedb.org/{(IsTv(mediaType) ? "tv" : "movie")}/{n}"
            : null;

    public static string? Imdb(string? id)
        => id is not null && ImdbRegex().IsMatch(id.Trim()) ? $"https://www.imdb.com/title/{id.Trim().ToLowerInvariant()}" : null;

    /// <param name="kind"><c>release</c>, <c>release-group</c>, <c>recording</c> or <c>artist</c>.</param>
    public static string? MusicBrainz(string kind, string? mbid)
        => kind is "release" or "release-group" or "recording" or "artist" or "work"
           && Guid.TryParse(mbid?.Trim(), out var guid)
            ? $"https://musicbrainz.org/{kind}/{guid:D}"
            : null;

    public static string? Wikidata(string? qid)
        => qid is not null && QidRegex().IsMatch(qid.Trim())
            ? $"https://www.wikidata.org/wiki/{qid.Trim().ToUpperInvariant()}"
            : null;

    /// <summary>Wikipedia (or any other) article address, accepted only when it is plain http(s).</summary>
    public static string? WebPage(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : null;

    public static string? ComicVineIssue(string? id)
        => ComicVineNumber(id) is { } n ? $"https://comicvine.gamespot.com/issue/4000-{n}/" : null;

    public static string? ComicVineVolume(string? id)
        => ComicVineNumber(id) is { } n ? $"https://comicvine.gamespot.com/volume/4050-{n}/" : null;

    public static string? OpenLibraryWork(string? id)
        => id is not null && OpenLibraryRegex().IsMatch(id.Trim()) ? $"https://openlibrary.org/works/{id.Trim()}" : null;

    public static string? AppleBooks(string? id)
        => Numeric(id) is { } n ? $"https://books.apple.com/book/id{n}" : null;

    /// <summary>
    /// Expands a provider-config link template for one identifier. Identifier keys that
    /// have a known shape are validated first (TheTVDB ids must be numbers, a MusicBrainz
    /// id a GUID, a Wikidata id <c>Q123</c>), and a bad value yields <c>null</c>.
    /// </summary>
    public static string? Expand(string template, string identifierKey, string rawValue, string? mediaType = null)
    {
        var value = rawValue?.Trim() ?? string.Empty;
        if (value.Length == 0 || string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var isAbsoluteWeb = Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https";
        if (!isAbsoluteWeb && !IsValidIdentifier(identifierKey, ref value))
        {
            return null;
        }

        var templateValue = isAbsoluteWeb ? value : Uri.EscapeDataString(value);
        var url = template
            .Replace($"{{{identifierKey}}}", templateValue, StringComparison.OrdinalIgnoreCase)
            .Replace("{value}", templateValue, StringComparison.OrdinalIgnoreCase)
            .Replace("{media_type}", IsTv(mediaType) ? "tv" : "movie", StringComparison.OrdinalIgnoreCase);

        return Uri.TryCreate(url, UriKind.Absolute, out var resolved) && resolved.Scheme is "http" or "https"
            ? resolved.ToString()
            : null;
    }

    private static bool IsValidIdentifier(string key, ref string value)
    {
        switch (key.ToLowerInvariant())
        {
            case BridgeIdKeys.TvdbId or BridgeIdKeys.TvdbSeasonId or BridgeIdKeys.TvdbEpisodeId or BridgeIdKeys.TvdbPersonId
                or BridgeIdKeys.TmdbId or "tmdb_movie_id" or "tmdb_tv_id" or BridgeIdKeys.AppleBooksId:
                return value.All(char.IsAsciiDigit);
            case BridgeIdKeys.WikidataQid:
                if (!QidRegex().IsMatch(value))
                {
                    return false;
                }

                value = value.ToUpperInvariant();
                return true;
            case BridgeIdKeys.ImdbId:
                return ImdbRegex().IsMatch(value);
            case BridgeIdKeys.MusicBrainzId or BridgeIdKeys.MusicBrainzArtistId or BridgeIdKeys.MusicBrainzWorkId
                or BridgeIdKeys.MusicBrainzReleaseId or BridgeIdKeys.MusicBrainzRecordingId or BridgeIdKeys.MusicBrainzReleaseGroupId:
                if (!Guid.TryParse(value, out var guid))
                {
                    return false;
                }

                value = guid.ToString("D");
                return true;
            case BridgeIdKeys.ComicVineId or BridgeIdKeys.ComicVineVolumeId or "comicvine_id":
                if (ComicVineNumber(value) is not { } number)
                {
                    return false;
                }

                value = number;
                return true;
            default:
                return true;
        }
    }

    private static string? Numeric(string? id)
    {
        var trimmed = id?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }

    // Comic Vine ids are sometimes stored as "123::Title"; only the number is linkable.
    private static string? ComicVineNumber(string? id)
    {
        var trimmed = id?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var delimiter = trimmed.IndexOf("::", StringComparison.Ordinal);
        return Numeric(delimiter >= 0 ? trimmed[..delimiter] : trimmed);
    }

    private static bool IsTv(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) && mediaType.Contains("TV", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^Q[1-9][0-9]*$", RegexOptions.IgnoreCase)]
    private static partial Regex QidRegex();

    [GeneratedRegex(@"^tt[0-9]{5,}$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbRegex();

    [GeneratedRegex(@"^OL[0-9]+[WMA]$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenLibraryRegex();
}
