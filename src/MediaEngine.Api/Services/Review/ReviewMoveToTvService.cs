using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Review;

/// <summary>
/// "Move to TV": a film file that TMDB lists as a short TV series (for example a web miniseries)
/// becomes a TV item. The change is logical only. The file is never moved, renamed or written: the asset
/// is re-filed under the matched show, its library becomes the TV library, user-locked claims record the
/// decision, and identity re-runs as TV (TMDB/TVDB, then Wikidata). When TMDB lists the show as one
/// season whose parts are an unbroken run 1..N (N within the per-file limit), the file sits on Season 1
/// Episode 1 and covers every part; otherwise it stays in Season 0, Episode 1 (Specials) and the reason
/// is shown to the user.
/// Everything that changes the data store commits in one transaction; only the identity re-queue,
/// history entry and notification follow the commit.
/// </summary>
public sealed class ReviewMoveToTvService(
    IReviewQueueRepository reviews,
    IMediaAssetRepository assets,
    ITvReassignmentRepository reassignments,
    ITmdbSeasonEpisodeCatalogue seasonEpisodes,
    ICanonicalValueRepository canonicals,
    IConfigurationLoader configuration,
    IHydrationPipelineService pipeline,
    ISystemActivityRepository activity,
    IEventPublisher publisher,
    ILogger<ReviewMoveToTvService> logger) : IReviewMoveToTvService
{
    public async Task<ReviewMoveToTvResult> MoveAsync(
        Guid reviewItemId, string resolvedBy, CancellationToken ct = default)
    {
        var item = await reviews.GetByIdAsync(reviewItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new(ReviewMoveToTvOutcome.ReviewItemNotFound, $"Review item '{reviewItemId}' not found.");
        }

        if (item.Status != ReviewStatus.Pending)
        {
            return new(ReviewMoveToTvOutcome.NotPending, "Review item is not pending.");
        }

        if (!string.Equals(item.Trigger, ReviewTrigger.MovieMatchedAsTv, StringComparison.Ordinal))
        {
            return new(ReviewMoveToTvOutcome.WrongTrigger,
                "Only a 'Found as a TV title' review item can be moved to TV.");
        }

        var suggestion = ReadSuggestion(item.CandidatesJson);
        if (suggestion is null)
        {
            return new(ReviewMoveToTvOutcome.SuggestionMissing,
                "This review item has no TMDB TV suggestion to apply.");
        }

        var asset = await assets.FindByIdAsync(item.EntityId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return new(ReviewMoveToTvOutcome.AssetMissing, "The media file for this review item is no longer in the library.");
        }

        var tvLibrary = configuration.LoadLibraries().Libraries.FirstOrDefault(IsTvLibrary);
        if (tvLibrary is null)
        {
            return new(ReviewMoveToTvOutcome.NoTvLibrary, "No TV library is configured to move this item into.");
        }

        var existing = (await canonicals.GetByEntityAsync(asset.Id, ct).ConfigureAwait(false))
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        existing.TryGetValue(MetadataFieldConstants.Title, out var fileTitle);

        var target = await ChoosePlacementAsync(suggestion, ct).ConfigureAwait(false);
        var hostEpisode = target.Episodes[0];

        // One all-or-nothing change: the show/season/episode hierarchy, the edition move, the empty
        // movie Work removal, the library reassignment, the user's locked claims and the review
        // resolution commit together. A failure rolls everything back and the item stays pending.
        var now = DateTimeOffset.UtcNow;
        TvReassignment placement;
        try
        {
            placement = await reassignments
                .MoveToTvShowAsync(
                    new TvMoveRequest(
                        asset.Id,
                        suggestion.Name,
                        fileTitle,
                        target.Season,
                        target.Episodes,
                        tvLibrary.Id,
                        reviewItemId,
                        resolvedBy,
                        reassignment => BuildDecision(asset.Id, reassignment, suggestion, target, now)),
                    ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Move to TV refused for asset {AssetId}", asset.Id);
            return new(ReviewMoveToTvOutcome.CannotReassign, ex.Message);
        }

        // Identity re-runs as TV with the new show/season/episode hints.
        existing[MetadataFieldConstants.MediaTypeField] = nameof(MediaType.TV);
        existing[MetadataFieldConstants.ShowName] = suggestion.Name;
        existing[MetadataFieldConstants.Series] = suggestion.Name;
        existing[MetadataFieldConstants.SeasonNumber] = target.Season.ToString(CultureInfo.InvariantCulture);
        existing[MetadataFieldConstants.EpisodeNumber] = hostEpisode.ToString(CultureInfo.InvariantCulture);
        existing[BridgeIdKeys.TmdbId] = suggestion.TmdbTvId;
        await pipeline.EnqueueAsync(new HarvestRequest
        {
            EntityId = asset.Id,
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.TV,
            Hints = existing,
        }, ct).ConfigureAwait(false);

        await activity.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.ReviewItemResolved,
            EntityId = asset.Id,
            ChangesJson = JsonSerializer.Serialize(new
            {
                title = fileTitle,
                media_type = nameof(MediaType.TV),
                entity_id = asset.Id.ToString(),
                action = "moved_to_tv",
                show_name = suggestion.Name,
                tmdb_tv_id = suggestion.TmdbTvId,
                season_number = target.Season,
                episode_number = hostEpisode,
                episode_numbers = target.Episodes,
                placement_reason = target.Reason,
                tv_library_id = tvLibrary.Id,
            }),
            Detail = DescribePlacement(suggestion.Name, target),
        }, ct).ConfigureAwait(false);

        await publisher.PublishAsync(
            SignalREvents.ReviewItemResolved,
            new ReviewItemResolvedSupplementaryEvent(reviewItemId, asset.Id, "Resolved"),
            ct).ConfigureAwait(false);

        return new(
            ReviewMoveToTvOutcome.Moved,
            Response: new ReviewMoveToTvResponse(
                true,
                reviewItemId,
                asset.Id,
                suggestion.Name,
                tvLibrary.Id,
                target.Season,
                target.Episodes,
                target.Reason));
    }

    /// <summary>Where the file is filed and, when it is a special, why.</summary>
    private sealed record MovePlacement(int Season, IReadOnlyList<int> Episodes, string? Reason);

    /// <summary>
    /// Season 1 with every part TMDB lists when the show is one season of an unbroken run within the
    /// per-file limit; otherwise Specials, with the plain-English reason.
    /// </summary>
    private async Task<MovePlacement> ChoosePlacementAsync(MovieTvSuggestionDto suggestion, CancellationToken ct)
    {
        IReadOnlySet<int>? seasonOne = null;
        if (suggestion.Seasons == 1)
        {
            seasonOne = await seasonEpisodes
                .GetSeasonEpisodeNumbersAsync(suggestion.TmdbTvId, 1, ct)
                .ConfigureAwait(false);
        }

        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(suggestion.Seasons, seasonOne);
        return decision.Kind == EpisodeRangeKind.Clean
            ? new MovePlacement(1, decision.Episodes, null)
            : new MovePlacement(TvSpecialPlacement.SeasonNumber, [TvSpecialPlacement.EpisodeNumber], decision.Reason);
    }

    private static string DescribePlacement(string showName, MovePlacement target)
    {
        if (target.Reason is not null)
        {
            return $"Moved to TV as '{showName}' Specials (Season {TvSpecialPlacement.SeasonNumber}, "
                   + $"Episode {TvSpecialPlacement.EpisodeNumber}). {target.Reason} The file was not moved.";
        }

        var episodes = target.Episodes.Count == 1
            ? $"Episode {target.Episodes[0]}"
            : $"Episodes {target.Episodes[0]}\u2013{target.Episodes[^1]}";
        return $"Moved to TV as '{showName}' Season {target.Season}, {episodes}. The file was not moved.";
    }

    /// <summary>
    /// Describes the user's decision as locked claims plus the matching current values: on the asset for
    /// what describes the file (media type, season, episode, show for retail hints) and on the show Work
    /// for what describes the container (name, TMDB id, year, overview). Pure; the repository writes the
    /// result inside the same transaction as the re-filing.
    /// </summary>
    private static TvMoveDecision BuildDecision(
        Guid assetId,
        TvReassignment placement,
        MovieTvSuggestionDto suggestion,
        MovePlacement target,
        DateTimeOffset now)
    {
        var locked = new List<MetadataClaim>();
        var values = new List<CanonicalValue>();

        void Locked(Guid entityId, string key, string value)
        {
            locked.Add(new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                ProviderId = WellKnownProviders.UserManual,
                ClaimKey = key,
                ClaimValue = value,
                Confidence = 1.0,
                ClaimedAt = now,
                IsUserLocked = true,
            });
            Value(entityId, key, value);
        }

        void Value(Guid entityId, string key, string value) =>
            values.Add(new CanonicalValue
            {
                EntityId = entityId,
                Key = key,
                Value = value,
                LastScoredAt = now,
            });

        var season = target.Season.ToString(CultureInfo.InvariantCulture);
        var episode = target.Episodes[0].ToString(CultureInfo.InvariantCulture);

        // The file itself.
        Locked(assetId, MetadataFieldConstants.MediaTypeField, nameof(MediaType.TV));
        Locked(assetId, MetadataFieldConstants.ShowName, suggestion.Name);
        Locked(assetId, MetadataFieldConstants.Series, suggestion.Name);
        Locked(assetId, MetadataFieldConstants.SeasonNumber, season);
        Locked(assetId, MetadataFieldConstants.EpisodeNumber, episode);
        Locked(assetId, BridgeIdKeys.TmdbId, suggestion.TmdbTvId);

        // The episode Work and the show Work.
        Locked(placement.EpisodeWorkId, MetadataFieldConstants.MediaTypeField, nameof(MediaType.TV));
        Locked(placement.ShowWorkId, MetadataFieldConstants.MediaTypeField, nameof(MediaType.TV));
        Locked(placement.ShowWorkId, MetadataFieldConstants.ShowName, suggestion.Name);
        Locked(placement.ShowWorkId, BridgeIdKeys.TmdbId, suggestion.TmdbTvId);

        // TMDB's own description of the show: ordinary provider claims, so later providers can outrank them.
        var tmdbClaims = new List<(string Key, string Value)> { (MetadataFieldConstants.Title, suggestion.Name) };
        if (suggestion.FirstAirYear is { } year)
        {
            tmdbClaims.Add((MetadataFieldConstants.Year, year.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(suggestion.Overview))
        {
            tmdbClaims.Add((MetadataFieldConstants.Description, suggestion.Overview));
        }

        foreach (var (key, value) in tmdbClaims)
        {
            locked.Add(new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = placement.ShowWorkId,
                ProviderId = WellKnownProviders.Tmdb,
                ClaimKey = key,
                ClaimValue = value,
                Confidence = 0.9,
                ClaimedAt = now,
                IsUserLocked = false,
            });
            Value(placement.ShowWorkId, key, value);
        }

        return new TvMoveDecision(
            locked,
            values,
            [
                new BridgeIdEntry
                {
                    EntityId = placement.ShowWorkId,
                    IdType = BridgeIdKeys.TmdbId,
                    IdValue = suggestion.TmdbTvId,
                    ProviderId = WellKnownProviders.Tmdb.ToString(),
                },
            ]);
    }

    private static MovieTvSuggestionDto? ReadSuggestion(string? candidatesJson)
    {
        if (string.IsNullOrWhiteSpace(candidatesJson))
        {
            return null;
        }

        try
        {
            var suggestion = JsonSerializer.Deserialize<List<MovieTvSuggestionDto>>(candidatesJson)?.FirstOrDefault();
            return suggestion is not null
                && !string.IsNullOrWhiteSpace(suggestion.Name)
                && !string.IsNullOrWhiteSpace(suggestion.TmdbTvId)
                    ? suggestion
                    : null;
        }
        catch (JsonException)
        {
            // Unreadable candidates behave like a missing suggestion; the caller reports it to the user.
            return null;
        }
    }

    private static bool IsTvLibrary(Domain.Configuration.LibraryFolderConfig library) =>
        string.Equals(library.Category, nameof(MediaType.TV), StringComparison.OrdinalIgnoreCase)
        || library.MediaTypes.Any(type => string.Equals(type, nameof(MediaType.TV), StringComparison.OrdinalIgnoreCase));
}
