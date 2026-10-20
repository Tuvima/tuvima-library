using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

/// <summary>Subtitle identities retain their movie, show and episode scopes.</summary>
public sealed record MovieSubtitleIdentity(string? ImdbId, string? TmdbMovieId);

/// <summary>Owned TVDB identity; its local placement is never used as TMDB placement.</summary>
public sealed record TvdbEpisodeIdentity(
    string ShowId,
    string EpisodeId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? Order = null);

/// <summary>A verified episode crosswalk, for subtitle acquisition only.</summary>
public sealed record TmdbEpisodeIdentity(
    string ShowId,
    string EpisodeId,
    int SeasonNumber,
    int EpisodeNumber,
    string Source = "tmdb_tvdb_external_id",
    DateTimeOffset? VerifiedAt = null);

public sealed record SubtitleLookupContext(
    MovieSubtitleIdentity? Movie = null,
    TvdbEpisodeIdentity? TvdbEpisode = null,
    TmdbEpisodeIdentity? TmdbEpisode = null)
{
    /// <summary>
    /// The identities a subtitle search may rely on, as flat values for the media type: a positive
    /// show or movie id and an IMDb id of the right shape for movies, with season and episode
    /// numbers for episodes. Identities that fail those checks are left out, so a search that
    /// needs them is not made.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToVerifiedValues(MediaType mediaType)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mediaType == MediaType.TV)
        {
            if (TmdbEpisode is { SeasonNumber: >= 0, EpisodeNumber: >= 1 } episode && TryPositiveId(episode.ShowId, out var showId))
            {
                values[BridgeIdKeys.TmdbId] = showId;
                values["season_number"] = episode.SeasonNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                values["episode_number"] = episode.EpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else if (mediaType == MediaType.Movies && Movie is { } movie)
        {
            if (TryPositiveId(movie.TmdbMovieId, out var movieId))
            {
                values[BridgeIdKeys.TmdbId] = movieId;
            }

            if (IsImdbId(movie.ImdbId))
            {
                values[BridgeIdKeys.ImdbId] = movie.ImdbId!;
            }
        }

        return values;
    }

    private static bool TryPositiveId(string? value, out string id)
    {
        id = value?.Trim() ?? string.Empty;
        return long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number > 0;
    }

    private static bool IsImdbId(string? value) => value is { Length: >= 3 and <= 12 }
        && value.StartsWith("tt", StringComparison.Ordinal) && value[2..].All(char.IsAsciiDigit);
}

public interface ITvEpisodeCrosswalk
{
    /// <summary>
    /// Returns TMDB's position only when direct external IDs prove both show and
    /// episode identity. Missing, ambiguous or conflicting links return null.
    /// </summary>
    Task<TmdbEpisodeIdentity?> ResolveAsync(
        TvdbEpisodeIdentity identity,
        string? expectedTmdbShowId = null,
        CancellationToken ct = default);
}
