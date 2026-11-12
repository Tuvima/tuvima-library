using System.Text.Json;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Review;

/// <inheritdoc cref="IReviewChooseService"/>
public sealed class ReviewChooseService(
    IReviewQueueReadService reviewReads,
    IReviewQueueRepository reviews,
    IReviewChoiceApplier applier,
    ICanonicalValueRepository canonicals,
    ISystemActivityRepository activity,
    IEventPublisher publisher,
    ILogger<ReviewChooseService> logger) : IReviewChooseService
{
    // One choice at a time per review item: a second press (a double tap, or a phone and a computer at once) waits, then finds the item no
    // longer pending and is refused, so a choice is never applied twice. The Engine is one process, so a lock in memory is enough.
    private static readonly SemaphoreSlim[] Gates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    public async Task<ReviewChooseResult> ChooseAsync(Guid reviewItemId, string choiceId, string resolvedBy, CancellationToken ct = default)
    {
        var gate = Gates[(reviewItemId.GetHashCode() & int.MaxValue) % Gates.Length];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ChooseLockedAsync(reviewItemId, choiceId, resolvedBy, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ReviewChooseResult> ChooseLockedAsync(Guid reviewItemId, string choiceId, string resolvedBy, CancellationToken ct)
    {
        // Read the way the Needs Review list reads, so the choices offered here are exactly the ones the list showed.
        var item = await reviewReads.GetByIdAsync(reviewItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new(ReviewChooseOutcome.ReviewItemNotFound, $"Review item '{reviewItemId}' not found.");
        }

        if (!string.Equals(item.Status, ReviewStatus.Pending, StringComparison.Ordinal))
        {
            return new(ReviewChooseOutcome.NotPending, "Review item is not pending.");
        }

        var option = NeedsReviewChoices.Read(item.Trigger, item.CandidatesJson, item.MediaType, logger)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, choiceId, StringComparison.Ordinal));
        if (option is null)
        {
            return new(ReviewChooseOutcome.UnknownChoice, "That choice is not one this item offers.");
        }

        ReviewChoiceApplyResult applied;
        switch (option.Kind)
        {
            case NeedsReviewChoiceKinds.Retail:
                applied = await applier.ApplyRetailMatchAsync(
                    item.EntityId, BuildRetailRequest(item, option, logger), ct).ConfigureAwait(false);
                break;
            case NeedsReviewChoiceKinds.Series:
                applied = await applier.PlaceOnSeriesAsync(item.EntityId, option.CollectionId!.Value, ct).ConfigureAwait(false);
                break;
            case NeedsReviewChoiceKinds.MediaType:
                applied = await applier.ReclassifyAsync(item.EntityId, option.MediaType!.Value, ct).ConfigureAwait(false);
                break;
            default:
                return new(ReviewChooseOutcome.UnknownChoice, "That choice is not one this item offers.");
        }

        if (!applied.Succeeded)
        {
            return new(ReviewChooseOutcome.ApplyFailed, applied.Message, applied.Status);
        }

        // Each write normally closes the question itself (the retail match through review_item_id, the Series choice and the type change by
        // their trigger); this makes sure the row has left the list whichever way the write went.
        var after = await reviews.GetByIdAsync(reviewItemId, ct).ConfigureAwait(false);
        if (after is not null && string.Equals(after.Status, ReviewStatus.Pending, StringComparison.Ordinal))
        {
            await reviews.UpdateStatusAsync(reviewItemId, ReviewStatus.Resolved, resolvedBy, ct).ConfigureAwait(false);
        }

        await PublishAsync(item, option, ct).ConfigureAwait(false);
        return new(
            ReviewChooseOutcome.Chosen,
            Response: new ReviewChooseResponse(chosen: true, review_item_id: reviewItemId, choice_id: option.Id, kind: option.Kind));
    }

    /// <summary>
    /// The editor's Match request for a retail choice: the item-level target for the kind of item (a film, a book, an audiobook, a comic
    /// issue, a song), the stored candidate's title, year and creator, its bridge ids, and this review item so the write resolves it.
    /// </summary>
    internal static ReplaceRetailMatchRequestDto BuildRetailRequest(ReviewItemDto item, ReviewChoiceOption option, ILogger? logger)
    {
        var candidate = option.Retail!;
        var target = NeedsReviewChoices.RetailTargetFor(item.MediaType)!.Value;
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = candidate.Title.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(candidate.Year))
        {
            var year = candidate.Year.Trim();
            required[MetadataFieldConstants.Year] = year.Length >= 4 && year[..4].All(char.IsDigit) ? year[..4] : year;
        }

        if (!string.IsNullOrWhiteSpace(candidate.Creator))
        {
            var creatorKey = target.FieldGroup switch
            {
                "movie_identity" => MetadataFieldConstants.Director,
                "track" => MetadataFieldConstants.Artist,
                _ => MetadataFieldConstants.Author,
            };
            required[creatorKey] = candidate.Creator.Trim();
        }

        return new ReplaceRetailMatchRequestDto
        {
            TargetKind = target.TargetKind,
            TargetFieldGroup = target.FieldGroup,
            TargetScopeId = item.EntityId.ToString("D"),
            ProviderId = candidate.ProviderId.ToString("D"),
            ProviderName = candidate.ProviderName,
            ProviderItemId = candidate.ProviderItemId ?? string.Empty,
            CoverUrl = candidate.ImageUrl,
            RequiredFields = required,
            BridgeIds = ReadBridgeIds(candidate.BridgeIdsJson, logger),
            ReviewItemId = item.Id,
        };
    }

    private static Dictionary<string, string> ReadBridgeIds(string? bridgeIdsJson, ILogger? logger)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(bridgeIdsJson))
        {
            return ids;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(bridgeIdsJson) ?? [];
            foreach (var (key, value) in stored.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)))
            {
                ids[key] = value!;
            }
        }
        catch (JsonException ex)
        {
            // The match is still applied from its provider id; only the extra ids are missing.
            logger?.LogDebug(ex, "Bridge ids stored with a Needs Review candidate could not be read");
        }

        return ids;
    }

    // The same history entry and live update as resolving an item, so open Dashboards drop the row and the counts follow.
    private async Task PublishAsync(ReviewItemDto item, ReviewChoiceOption option, CancellationToken ct)
    {
        var values = (await canonicals.GetByEntityAsync(item.EntityId, ct).ConfigureAwait(false))
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        values.TryGetValue(MetadataFieldConstants.Title, out var title);
        if (string.IsNullOrWhiteSpace(title))
        {
            values.TryGetValue("file_name", out title);
        }

        values.TryGetValue(MetadataFieldConstants.MediaTypeField, out var mediaType);
        if (!values.TryGetValue(MetadataFieldConstants.CoverUrl, out var cover))
        {
            values.TryGetValue(MetadataFieldConstants.Cover, out cover);
        }

        await activity.LogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.ReviewItemResolved,
            EntityId = item.EntityId,
            ChangesJson = JsonSerializer.Serialize(new
            {
                title,
                media_type = mediaType,
                entity_id = item.EntityId.ToString(),
                action = "resolved",
                choice = option.Title,
                cover_url = cover,
            }),
            Detail = $"Review resolved by choosing '{option.Title}'.",
        }, ct).ConfigureAwait(false);

        await publisher.PublishAsync(
            SignalREvents.ReviewItemResolved,
            new ReviewItemResolvedSupplementaryEvent(item.Id, item.EntityId, "Resolved"),
            ct).ConfigureAwait(false);
    }
}
