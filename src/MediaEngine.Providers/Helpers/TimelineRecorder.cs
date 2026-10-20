using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Providers.Helpers;

/// <summary>
/// Records entity timeline events with standardized shapes.
/// Thin wrapper over the timeline repository — workers call these methods
/// instead of building event objects inline.
/// </summary>
public sealed class TimelineRecorder
{
    private readonly IEntityTimelineRepository _timelineRepo;

    public TimelineRecorder(IEntityTimelineRepository timelineRepo)
    {
        _timelineRepo = timelineRepo;
    }

    /// <summary>
    /// Records a successful retail provider match for an entity.
    /// </summary>
    /// <param name="entityId">The matched entity.</param>
    /// <param name="providerName">The provider that matched (e.g. "apple_books", "tmdb").</param>
    /// <param name="claimCount">Number of claims added by the provider.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordRetailMatchedAsync(
        Guid entityId,
        string providerName,
        int claimCount,
        CancellationToken ct = default)
    {
        await _timelineRepo.InsertEventAsync(new EntityEvent
        {
            EntityId = entityId,
            EntityType = "Work",
            EventType = "retail_matched",
            Stage = 1,
            Trigger = "ingestion",
            ProviderName = providerName,
            Detail = $"Retail match: {providerName} contributed {claimCount} claim(s)",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a failed retail match — no provider returned results.
    /// </summary>
    /// <param name="entityId">The entity that could not be matched.</param>
    /// <param name="titleHint">The file title used in the search attempt.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordRetailNoMatchAsync(
        Guid entityId,
        string? titleHint,
        CancellationToken ct = default)
    {
        await _timelineRepo.InsertEventAsync(new EntityEvent
        {
            EntityId = entityId,
            EntityType = "Work",
            EventType = "retail_no_match",
            Stage = 1,
            Trigger = "ingestion",
            Detail = string.IsNullOrWhiteSpace(titleHint)
                ? "No retail match found"
                : $"No retail match found for \"{titleHint}\"",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a successful Wikidata bridge resolution (QID found via bridge ID).
    /// </summary>
    /// <param name="entityId">The resolved entity.</param>
    /// <param name="qid">The resolved Wikidata QID (e.g. "Q83471").</param>
    /// <param name="bridgeType">The bridge ID type used (e.g. "isbn_13", "tmdb_id").</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordBridgeResolvedAsync(
        Guid entityId,
        string qid,
        string bridgeType,
        CancellationToken ct = default)
    {
        await _timelineRepo.InsertEventAsync(new EntityEvent
        {
            EntityId = entityId,
            EntityType = "Work",
            EventType = "wikidata_bridge_resolved",
            Stage = 2,
            Trigger = "ingestion",
            ResolvedQid = qid,
            BridgeIdType = bridgeType,
            Detail = $"{bridgeType} \u2192 {qid}",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a failed Wikidata bridge resolution — no QID found.
    /// </summary>
    /// <param name="entityId">The entity that could not be resolved.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordBridgeNoMatchAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        await _timelineRepo.InsertEventAsync(new EntityEvent
        {
            EntityId = entityId,
            EntityType = "Work",
            EventType = "wikidata_no_match",
            Stage = 2,
            Trigger = "ingestion",
            Detail = "No Wikidata entity found via bridge IDs or title search",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a QID resolved via title/text fallback search on Wikidata.
    /// </summary>
    /// <param name="entityId">The resolved entity.</param>
    /// <param name="qid">The resolved Wikidata QID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordTitleFallbackResolvedAsync(
        Guid entityId,
        string qid,
        CancellationToken ct = default)
    {
        await _timelineRepo.InsertEventAsync(new EntityEvent
        {
            EntityId = entityId,
            EntityType = "Work",
            EventType = "wikidata_title_resolved",
            Stage = 2,
            Trigger = "ingestion",
            ResolvedQid = qid,
            Detail = $"Title search \u2192 {qid}",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a QID resolved via AI disambiguation when multiple candidates were returned.
    /// </summary>
    /// <param name="entityId">The resolved entity.</param>
    /// <param name="qid">The QID selected by AI disambiguation.</param>
    /// <param name="confidence">The AI disambiguation confidence score.</param>
    /// <param name="reasoning">The AI's reasoning text for the selection.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordAiDisambiguatedAsync(
        Guid entityId,
        string qid,
        double confidence,
        string? reasoning,
        CancellationToken ct = default)
    {
        await _timelineRepo.InsertEventAsync(new EntityEvent
        {
            EntityId = entityId,
            EntityType = "Work",
            EventType = "wikidata_ai_disambiguated",
            Stage = 2,
            Trigger = "ai_disambiguation",
            ResolvedQid = qid,
            Confidence = confidence,
            Detail = string.IsNullOrWhiteSpace(reasoning)
                ? $"AI selected {qid} at {confidence:P0}"
                : $"AI selected {qid} at {confidence:P0}: {reasoning}",
        }, ct).ConfigureAwait(false);
    }
}
