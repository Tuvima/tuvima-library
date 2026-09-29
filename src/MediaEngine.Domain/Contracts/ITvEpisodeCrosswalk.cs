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
    TmdbEpisodeIdentity? TmdbEpisode = null);

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
