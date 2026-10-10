using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Helpers;

/// <summary>
/// Creates review items with correct triggers and detail text.
/// Centralizes trigger selection so no worker can accidentally use the wrong trigger.
/// </summary>
public sealed class StageOutcomeFactory
{
    private readonly IMediaAssetRepository? _assets;
    private readonly IConfigurationLoader? _configuration;
    private readonly IReviewQueueRepository _reviewRepo;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly IEventPublisher _eventPublisher;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IIngestionBatchArtifactRepository? _artifactRepo;
    private readonly ILogger<StageOutcomeFactory> _logger;

    public StageOutcomeFactory(
        IReviewQueueRepository reviewRepo,
        ISystemActivityRepository activityRepo,
        IEventPublisher eventPublisher,
        ICanonicalValueRepository canonicalRepo,
        ILogger<StageOutcomeFactory> logger,
        IIngestionBatchArtifactRepository? artifactRepo = null,
        IMediaAssetRepository? assets = null,
        IConfigurationLoader? configuration = null)
    {
        _assets = assets;
        _configuration = configuration;
        _reviewRepo = reviewRepo;
        _activityRepo = activityRepo;
        _eventPublisher = eventPublisher;
        _canonicalRepo = canonicalRepo;
        _artifactRepo = artifactRepo;
        _logger = logger;
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.RetailMatchFailed"/> review item when
    /// no retail provider returned a match for the entity.
    /// </summary>
    /// <param name="entityId">The entity that failed retail matching.</param>
    /// <param name="mediaType">The media type label used in the detail message.</param>
    /// <param name="ingestionRunId">Optional ingestion run for activity correlation.</param>
    /// <param name="onBatchAdjust">Optional callback to shift batch counters (receives the ingestion run ID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The review entry ID, or <c>null</c> if a duplicate pending review already exists.</returns>
    public async Task<Guid?> CreateRetailFailedAsync(
        Guid entityId,
        string mediaType,
        Guid? ingestionRunId = null,
        Action<Guid?>? onBatchAdjust = null,
        CancellationToken ct = default)
    {
        // A missing catalogue match does not make a confidently classified linked file unusable.
        // Ambiguous matches and placeholder titles retain their separate review paths.
        var asset = _assets is null ? null : await _assets.FindByIdAsync(entityId, ct);
        var source = asset is null ? null : _configuration?.LoadLibraries().Libraries
            .SelectMany(l => l.Sources)
            .Where(s => string.Equals(s.ManagementMode, MediaEngine.Domain.Configuration.LibrarySourceManagementModes.ExistingLibrary, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(s => asset.FilePathRoot.StartsWith(Path.TrimEndingDirectorySeparator(s.Path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (source is not null && asset!.Status == AssetStatus.Normal)
        {
            var facts = (await _canonicalRepo.GetByEntityAsync(entityId, ct)).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            if (facts.TryGetValue("title", out var title) && !PlaceholderTitleDetector.IsPlaceholder(title)
                && !string.Equals(mediaType, "Other", StringComparison.OrdinalIgnoreCase))
            {
                await _activityRepo.LogAsync(new SystemActivityEntry
                {
                    ActionType = "LocalMetadataRetained",
                    EntityId = entityId,
                    EntityType = "MediaAsset",
                    IngestionRunId = ingestionRunId,
                    Detail = "Local media remains available; no provider match was found. Metadata is incomplete.",
                }, ct);
                return null;
            }
        }
        return await CreateCoreAsync(
            entityId,
            ReviewTrigger.RetailMatchFailed,
            0.0,
            $"Retail identification failed for this {mediaType} \u2014 no provider returned a match",
            ingestionRunId,
            onBatchAdjust,
            ct);
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.MovieMatchedAsTv"/> review item: a film file had no movie
    /// match, but TMDB lists it in its TV catalogue. The suggestion is stored in <c>candidates_json</c>
    /// and is never applied automatically. A suggestion the user already dismissed ("Keep as
    /// unmatched film") is not raised again.
    /// </summary>
    /// <returns>The review entry ID, or <c>null</c> if a pending or dismissed item already exists.</returns>
    public async Task<Guid?> CreateMovieMatchedAsTvAsync(
        Guid entityId,
        MovieTvSuggestionDto suggestion,
        Guid? ingestionRunId = null,
        Action<Guid?>? onBatchAdjust = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        var existing = await _reviewRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
        if (existing.Any(r => r.Trigger == ReviewTrigger.MovieMatchedAsTv
                              && r.Status == ReviewStatus.Dismissed))
        {
            _logger.LogDebug(
                "Movie-as-TV suggestion for entity {Id} was dismissed earlier — not raising it again",
                entityId);
            return null;
        }

        return await CreateCoreAsync(
            entityId,
            ReviewTrigger.MovieMatchedAsTv,
            0.0,
            DescribeMovieTvSuggestion(suggestion),
            ingestionRunId,
            onBatchAdjust,
            ct,
            candidatesJson: JsonSerializer.Serialize(new[] { suggestion })).ConfigureAwait(false);
    }

    /// <summary>
    /// The one-line suggestion shown to the user, for example
    /// <c>TMDB lists this as the miniseries 'Dr. Horrible's Sing-Along Blog' (2008, 3 parts). Move it to TV?</c>.
    /// </summary>
    public static string DescribeMovieTvSuggestion(MovieTvSuggestionDto suggestion)
    {
        var kind = string.IsNullOrWhiteSpace(suggestion.Type)
            ? "TV series"
            : suggestion.Type.Trim().ToLowerInvariant();
        var facts = new List<string>();
        if (suggestion.FirstAirYear is { } year)
        {
            facts.Add(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (suggestion.Episodes > 0)
        {
            facts.Add(suggestion.Episodes == 1 ? "1 part" : $"{suggestion.Episodes} parts");
        }

        var detail = facts.Count > 0 ? $" ({string.Join(", ", facts)})" : string.Empty;
        return $"TMDB lists this as the {kind} '{suggestion.Name}'{detail}. Move it to TV?";
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.RetailMatchAmbiguous"/> review item when
    /// the top retail candidate scored between the ambiguous and auto-accept thresholds.
    /// </summary>
    /// <param name="entityId">The entity with an ambiguous retail match.</param>
    /// <param name="mediaType">The media type label used in the detail message.</param>
    /// <param name="score">The retail match confidence score.</param>
    /// <param name="ingestionRunId">Optional ingestion run for activity correlation.</param>
    /// <param name="onBatchAdjust">Optional callback to shift batch counters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The review entry ID, or <c>null</c> if a duplicate pending review already exists.</returns>
    public Task<Guid?> CreateRetailAmbiguousAsync(
        Guid entityId,
        string mediaType,
        double score,
        Guid? ingestionRunId = null,
        Action<Guid?>? onBatchAdjust = null,
        CancellationToken ct = default)
    {
        return CreateProvisionalAsync(
            entityId,
            ReviewTrigger.RetailMatchAmbiguous,
            score,
            $"Retail match found with confidence {score:P0} \u2014 needs confirmation",
            ingestionRunId,
            ct);
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.WikidataBridgeFailed"/> review item when
    /// Stage 2 bridge resolution could not find a Wikidata entity.
    /// </summary>
    /// <param name="entityId">The entity that failed Wikidata bridge resolution.</param>
    /// <param name="detail">Human-readable detail explaining the failure.</param>
    /// <param name="ingestionRunId">Optional ingestion run for activity correlation.</param>
    /// <param name="onBatchAdjust">Optional callback to shift batch counters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The review entry ID, or <c>null</c> if a duplicate pending review already exists.</returns>
    public Task<Guid?> CreateWikidataBridgeFailedAsync(
        Guid entityId,
        string detail,
        Guid? ingestionRunId = null,
        Action<Guid?>? onBatchAdjust = null,
        CancellationToken ct = default)
    {
        return CreateCoreAsync(
            entityId,
            ReviewTrigger.WikidataBridgeFailed,
            0.0,
            detail,
            ingestionRunId,
            onBatchAdjust,
            ct);
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.MultipleQidMatches"/> review item when
    /// multiple Wikidata QID candidates were returned and could not be disambiguated.
    /// </summary>
    /// <param name="entityId">The entity with ambiguous QID candidates.</param>
    /// <param name="candidatesJson">Serialized JSON array of QID candidates.</param>
    /// <param name="ingestionRunId">Optional ingestion run for activity correlation.</param>
    /// <param name="onBatchAdjust">Optional callback to shift batch counters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The review entry ID, or <c>null</c> if a duplicate pending review already exists.</returns>
    public async Task<Guid?> CreateMultipleQidMatchesAsync(
        Guid entityId,
        string candidatesJson,
        Guid? ingestionRunId = null,
        Action<Guid?>? onBatchAdjust = null,
        CancellationToken ct = default)
    {
        // Dedup check.
        var existing = await _reviewRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
        if (existing.Any(r => r.Status == ReviewStatus.Pending
                              && r.Trigger == ReviewTrigger.MultipleQidMatches))
        {
            _logger.LogDebug(
                "Review item '{Trigger}' already exists for entity {Id} \u2014 skipping duplicate",
                ReviewTrigger.MultipleQidMatches, entityId);
            return null;
        }

        var entry = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.MultipleQidMatches,
            ConfidenceScore = 0.0,
            Detail = $"Multiple Wikidata QID candidates found \u2014 manual disambiguation required",
            CandidatesJson = candidatesJson,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        };

        await _reviewRepo.InsertAsync(entry, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Pipeline: entity {EntityId} sent to review \u2014 trigger={Trigger}",
            entityId, ReviewTrigger.MultipleQidMatches);

        onBatchAdjust?.Invoke(ingestionRunId);

        await LogActivityAndPublishAsync(entry, ingestionRunId, ct).ConfigureAwait(false);
        await RecordReviewArtifactAsync(entry, ingestionRunId, ct).ConfigureAwait(false);

        return entry.Id;
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.PlaceholderTitle"/> review item when
    /// the file's title looks like a placeholder ("Unknown", "Untitled", "Track 01")
    /// and there are no bridge IDs to fall back on.
    /// </summary>
    public Task<Guid?> CreatePlaceholderTitleAsync(
        Guid entityId,
        string? title,
        Guid? ingestionRunId = null,
        Action<Guid?>? onBatchAdjust = null,
        CancellationToken ct = default)
    {
        return CreateCoreAsync(
            entityId,
            ReviewTrigger.PlaceholderTitle,
            0.0,
            $"Title \"{title ?? "(blank)"}\" appears to be a placeholder with no ISBN, ASIN, or QID",
            ingestionRunId,
            onBatchAdjust,
            ct);
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.LowConfidence"/> review item when
    /// the entity's overall confidence falls below the auto-review threshold.
    /// </summary>
    /// <param name="entityId">The low-confidence entity.</param>
    /// <param name="confidence">The entity's overall confidence score.</param>
    /// <param name="ingestionRunId">Optional ingestion run for activity correlation.</param>
    /// <param name="onBatchAdjust">Optional callback to shift batch counters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The review entry ID, or <c>null</c> if a duplicate pending review already exists.</returns>
    public Task<Guid?> CreateLowConfidenceAsync(
        Guid entityId,
        double confidence,
        Guid? ingestionRunId = null,
        Action<Guid?>? onBatchAdjust = null,
        CancellationToken ct = default)
    {
        return CreateCoreAsync(
            entityId,
            ReviewTrigger.LowConfidence,
            confidence,
            $"Overall confidence {confidence:P0} below auto-accept threshold",
            ingestionRunId,
            onBatchAdjust,
            ct);
    }

    // ── Private ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a hidden review row while automation is still allowed to resolve
    /// the entity. Hidden rows do not emit activity, SignalR, batch artifacts, or
    /// review badges.
    /// </summary>
    public Task<Guid?> CreateProvisionalAsync(
        Guid entityId,
        string trigger,
        double? confidence,
        string detail,
        Guid? ingestionRunId = null,
        CancellationToken ct = default,
        string entityType = "MediaAsset",
        string? candidatesJson = null)
        => CreateCoreAsync(
            entityId,
            trigger,
            confidence,
            detail,
            ingestionRunId,
            onBatchAdjust: null,
            ct,
            reviewReady: false,
            entityType,
            candidatesJson);

    /// <summary>
    /// Promotes hidden review rows to visible review work and announces each row
    /// exactly once.
    /// </summary>
    public async Task<IReadOnlyList<ReviewQueueEntry>> PromoteProvisionalAsync(
        Guid entityId,
        Guid? ingestionRunId = null,
        CancellationToken ct = default)
    {
        var promoted = await _reviewRepo.PromotePendingReadyByEntityAsync(entityId, ct)
            .ConfigureAwait(false);

        foreach (var entry in promoted)
        {
            await LogActivityAndPublishAsync(entry, ingestionRunId, ct).ConfigureAwait(false);
            await RecordReviewArtifactAsync(entry, ingestionRunId, ct).ConfigureAwait(false);
        }

        return promoted;
    }

    /// <summary>
    /// Shared implementation: dedup check, insert, and optional user-visible publication.
    /// </summary>
    private async Task<Guid?> CreateCoreAsync(
        Guid entityId,
        string trigger,
        double? confidence,
        string detail,
        Guid? ingestionRunId,
        Action<Guid?>? onBatchAdjust,
        CancellationToken ct,
        bool reviewReady = true,
        string entityType = "MediaAsset",
        string? candidatesJson = null)
    {
        // Dedup: skip if a pending review with the same trigger already exists.
        var existing = await _reviewRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
        if (existing.Any(r => r.Status == ReviewStatus.Pending && r.Trigger == trigger))
        {
            _logger.LogDebug(
                "Review item '{Trigger}' already exists for entity {Id} \u2014 skipping duplicate",
                trigger, entityId);
            return null;
        }

        // Supersession: a pipeline trigger (RetailMatchFailed,
        // WikidataBridgeFailed, etc.) is the authoritative explanation for why
        // an entity is in review. Resolve any older asset-level LowConfidence
        // review for the same entity so the user only sees one row instead of
        // two for the same underlying problem.
        if (!string.Equals(trigger, ReviewTrigger.LowConfidence, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var stale in existing.Where(r =>
                r.Status == ReviewStatus.Pending
                && r.ReviewReadyAt is null
                && string.Equals(r.Trigger, ReviewTrigger.LowConfidence, StringComparison.OrdinalIgnoreCase)))
            {
                await _reviewRepo
                    .UpdateStatusAsync(stale.Id, ReviewStatus.Resolved, "system:superseded", ct)
                    .ConfigureAwait(false);
                _logger.LogDebug(
                    "Resolved stale LowConfidence review {Id} for entity {EntityId} \u2014 superseded by {Trigger}",
                    stale.Id, entityId, trigger);
            }
        }

        var entry = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = entityType,
            Trigger = trigger,
            ConfidenceScore = confidence,
            Detail = detail,
            CandidatesJson = candidatesJson,
            ReviewReadyAt = reviewReady ? DateTimeOffset.UtcNow : null,
            AutomationCompletedAt = reviewReady ? DateTimeOffset.UtcNow : null,
        };

        await _reviewRepo.InsertAsync(entry, ct).ConfigureAwait(false);

        if (!reviewReady)
        {
            _logger.LogDebug(
                "Pipeline: hidden provisional review parked for entity {EntityId} \u2014 trigger={Trigger}",
                entityId, trigger);
            return entry.Id;
        }

        _logger.LogInformation(
            "Pipeline: entity {EntityId} sent to review \u2014 trigger={Trigger}, confidence={Score:P0}",
            entityId, trigger, confidence);

        onBatchAdjust?.Invoke(ingestionRunId);

        await LogActivityAndPublishAsync(entry, ingestionRunId, ct).ConfigureAwait(false);
        await RecordReviewArtifactAsync(entry, ingestionRunId, ct).ConfigureAwait(false);

        return entry.Id;
    }

    /// <summary>
    /// Logs a <see cref="SystemActionType.ReviewItemCreated"/> activity entry and
    /// publishes the <see cref="SignalREvents.ReviewItemCreated"/> event.
    /// </summary>
    private async Task LogActivityAndPublishAsync(
        ReviewQueueEntry entry,
        Guid? ingestionRunId,
        CancellationToken ct)
    {
        await _activityRepo.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.ReviewItemCreated,
            EntityId = entry.EntityId,
            Detail = $"Review item created: {entry.Trigger}",
            IngestionRunId = ingestionRunId,
        }, ct).ConfigureAwait(false);

        // Resolve title for the SignalR event payload.
        string? titleText = null;
        try
        {
            var canonicals = await _canonicalRepo.GetByEntityAsync(entry.EntityId, ct)
                .ConfigureAwait(false);
            titleText = canonicals
                .FirstOrDefault(c => c.Key == MetadataFieldConstants.Title)?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve title for review event {ReviewId}", entry.Id);
        }

        await _eventPublisher.PublishAsync(
            SignalREvents.ReviewItemCreated,
            new ReviewItemCreatedEvent(entry.Id, entry.EntityId, entry.Trigger, titleText),
            ct).ConfigureAwait(false);
    }

    private async Task RecordReviewArtifactAsync(
        ReviewQueueEntry entry,
        Guid? ingestionRunId,
        CancellationToken ct)
    {
        if (_artifactRepo is null)
        {
            return;
        }

        await _artifactRepo.RecordAsync(
            ingestionRunId,
            "review_item",
            entry.Id,
            entry.EntityId,
            entry.EntityType,
            "created",
            entry.Trigger,
            providerId: null,
            source: "review_queue",
            detailJson: JsonSerializer.Serialize(new
            {
                trigger = entry.Trigger,
                confidence = entry.ConfidenceScore,
                detail = entry.Detail,
            }),
            ct).ConfigureAwait(false);
    }
}
