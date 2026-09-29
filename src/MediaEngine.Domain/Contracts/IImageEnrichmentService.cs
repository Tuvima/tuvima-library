using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Orchestrates Stage 3 TMDB artwork enrichment for movie and TV works.
/// </summary>
public interface IImageEnrichmentService
{
    Task<ProviderArtworkDiscovery> DiscoverArtworkAsync(Guid assetId, string scope, string role, CancellationToken ct = default) =>
        Task.FromResult(new ProviderArtworkDiscovery([], "This provider does not support artwork discovery."));
    /// <summary>
    /// Downloads managed movie/show, season, and logo artwork from TMDB.
    /// </summary>
    /// <param name="assetId">The media asset ID used for artwork storage and stream routes.</param>
    /// <param name="workQid">The work's confirmed Wikidata QID when available.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ImageEnrichmentResult> EnrichWorkImagesAsync(Guid assetId, string? workQid, CancellationToken ct = default);

    /// <summary>
    /// Explicitly rechecks provider artwork, bypassing ingestion-time completed
    /// markers. This is reserved for the administrator artwork editor.
    /// </summary>
    Task<ImageEnrichmentResult> RefreshWorkImagesAsync(Guid assetId, string? workQid, CancellationToken ct = default) =>
        EnrichWorkImagesAsync(assetId, workQid, ct);

    Task<(bool Changed, string Message)> RefreshTvEpisodeStillAsync(
        Guid episodeWorkId, string showId, int seasonNumber, int episodeNumber,
        string? stillPath, CancellationToken ct = default);

    Task<(bool Changed, string Message)> RefreshTvSeasonArtworkAsync(
        Guid seasonWorkId, string showId, int seasonNumber, CancellationToken ct = default);
}
