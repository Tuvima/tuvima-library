using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.BatchEdit;

/// <summary>
/// <see cref="ILibraryBatchEditService"/> over the pencil's own write path (<c>works.display_overrides_json</c>).
/// <para>
/// One Apply per batch, written item by item: each item is saved with one write, which is atomic on its own, and its outcome
/// is recorded before the next item starts. There is deliberately no transaction around the whole batch: a single item that
/// cannot take the change (an inherited value, a write error) must not undo the items that did, because the person asked for
/// those and the failing one is handed to Needs Review instead.
/// </para>
/// <para>
/// Once the writing loop starts it does not watch the request's cancellation token: a caller that gives up mid-batch must not
/// leave it half applied with no record, so the loop finishes and logs one summary line.
/// </para>
/// </summary>
public sealed class LibraryBatchEditService(
    ILibraryCurationReadService curationReadService,
    IItemCanonicalRepository itemCanonicalRepository,
    IReviewQueueRepository reviewQueueRepository,
    ISystemActivityRepository activityRepository,
    ILogger<LibraryBatchEditService> logger) : ILibraryBatchEditService
{
    private const int MaxDistinctValuesShown = 5;
    private const string InheritedReason = "This value comes from its parent (show, season or album). Change it there.";

    public async Task<LibraryBatchEditPreview> PreviewAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyCollection<string> fieldKeys,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(fieldKeys);
        ArgumentNullException.ThrowIfNull(refusals);

        var ids = entityIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var fields = BatchEditFieldCatalog.All
            .Where(field => fieldKeys.Count == 0 || fieldKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var items = await curationReadService.LoadBatchEditItemsAsync(
            ids.Where(id => !refusals.ContainsKey(id)).ToList(),
            fields.Select(field => field.Key).ToList(),
            ct).ConfigureAwait(false);
        var missingReasons = await ReasonsForMissingAsync(ids, refusals, items, ct).ConfigureAwait(false);

        var unavailable = new List<LibraryBatchLockedItem>();
        var available = new List<BatchEditItemRead>();
        foreach (var id in ids)
        {
            if (refusals.TryGetValue(id, out var refusal))
            {
                unavailable.Add(new LibraryBatchLockedItem { EntityId = id, Reason = refusal });
            }
            else if (items.TryGetValue(id, out var item))
            {
                available.Add(item);
            }
            else
            {
                unavailable.Add(new LibraryBatchLockedItem { EntityId = id, Reason = missingReasons[id] });
            }
        }

        var states = fields.Select(field => BuildFieldState(field, available)).ToList();
        return new LibraryBatchEditPreview
        {
            AffectedCount = available.Count,
            Fields = states,
            UnavailableItems = unavailable,
        };
    }

    public async Task<LibraryBatchEditResult> ApplyAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyList<BatchEditChange> changes,
        IReadOnlyDictionary<Guid, string> refusals,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(refusals);

        var ids = entityIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var items = await curationReadService.LoadBatchEditItemsAsync(
            ids.Where(id => !refusals.ContainsKey(id)).ToList(),
            changes.Select(change => change.Field.Key).ToList(),
            ct).ConfigureAwait(false);
        var missingReasons = await ReasonsForMissingAsync(ids, refusals, items, ct).ConfigureAwait(false);

        // From here on the batch is being written: it runs to the end whatever the caller does.
        var write = CancellationToken.None;
        var results = new List<LibraryBatchItemResult>(ids.Count);
        var byWork = new Dictionary<Guid, LibraryBatchItemResult>();
        foreach (var id in ids)
        {
            if (refusals.TryGetValue(id, out var refusal))
            {
                results.Add(Refused(id, refusal));
                continue;
            }

            if (!items.TryGetValue(id, out var item))
            {
                results.Add(Refused(id, missingReasons[id]));
                continue;
            }

            // An item and its file can both be selected; the work is only written once.
            if (byWork.TryGetValue(item.WorkId, out var already))
            {
                results.Add(new LibraryBatchItemResult
                {
                    EntityId = id,
                    Outcome = already.Outcome,
                    Reason = already.Reason,
                    FieldsNotApplied = [.. already.FieldsNotApplied],
                    ReviewId = already.ReviewId,
                });
                continue;
            }

            var result = await ApplyToItemAsync(item, changes, write).ConfigureAwait(false);
            byWork[item.WorkId] = result;
            results.Add(result);
        }

        var summary = new LibraryBatchEditResult
        {
            UpdatedCount = CountOf(results, LibraryBatchItemOutcomes.Updated),
            UnchangedCount = CountOf(results, LibraryBatchItemOutcomes.Unchanged),
            SentToReviewCount = CountOf(results, LibraryBatchItemOutcomes.SentToReview),
            RefusedCount = CountOf(results, LibraryBatchItemOutcomes.Refused),
            Items = results,
        };
        logger.LogInformation(
            "Batch edit of {Items} item(s) on {Fields}: {Updated} updated, {Unchanged} unchanged, {Review} sent to Needs Review, {Refused} refused.",
            results.Count,
            string.Join(", ", changes.Select(change => change.Field.Key)),
            summary.UpdatedCount,
            summary.UnchangedCount,
            summary.SentToReviewCount,
            summary.RefusedCount);
        return summary;
    }

    private static int CountOf(IReadOnlyList<LibraryBatchItemResult> results, string outcome) =>
        results.Count(result => result.Outcome == outcome);

    /// <summary>Why each selected id that is neither refused nor found is left out: a different kind of record, or gone.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ReasonsForMissingAsync(
        IReadOnlyList<Guid> ids,
        IReadOnlyDictionary<Guid, string> refusals,
        IReadOnlyDictionary<Guid, BatchEditItemRead> items,
        CancellationToken ct)
    {
        var missing = ids.Where(id => !refusals.ContainsKey(id) && !items.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var notTitles = await curationReadService.FindNonTitleIdsAsync(missing, ct).ConfigureAwait(false);
        return missing.ToDictionary(
            id => id,
            id => notTitles.Contains(id) ? LibraryBatchEditReasons.NotATitle : LibraryBatchEditReasons.Gone);
    }

    private async Task<LibraryBatchItemResult> ApplyToItemAsync(
        BatchEditItemRead item,
        IReadOnlyList<BatchEditChange> changes,
        CancellationToken ct)
    {
        // A change a pencil would refuse on this item fails the whole item: its fields are applied together or not at all.
        var blocked = new List<(BatchEditField Field, string Reason)>();
        foreach (var change in changes)
        {
            if (LockedReason(item, change.Field) is { } reason)
            {
                blocked.Add((change.Field, reason));
            }
        }

        if (blocked.Count > 0)
        {
            return await SendToReviewAsync(item, changes, blocked, ct).ConfigureAwait(false);
        }

        IReadOnlyList<string> updatedKeys;
        try
        {
            var state = await itemCanonicalRepository.LoadDisplayOverridesAsync(item.WorkId, ct).ConfigureAwait(false);
            if (!state.WorkExists)
            {
                return Refused(item.RequestedEntityId, LibraryBatchEditReasons.Gone);
            }

            var current = state.Values;
            var before = Snapshot(current);
            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var emptied = new List<BatchEditField>();
            foreach (var change in changes)
            {
                var stored = ValueToStore(item, current, change, blocked, emptied);
                if (stored is null)
                {
                    continue;
                }

                if (!DetailDisplayOverrideCatalog.TryValidateValue(change.Field.Key, stored, out _, out var invalid))
                {
                    blocked.Add((change.Field, invalid ?? "The new value is not allowed."));
                    continue;
                }

                updates[change.Field.Key] = stored;
            }

            // A genre list that would end up empty can't be saved as empty (that means "use the provider's"), so nothing is written.
            if (emptied.Count > 0)
            {
                return new LibraryBatchItemResult
                {
                    EntityId = item.RequestedEntityId,
                    Outcome = LibraryBatchItemOutcomes.Refused,
                    Reason = string.Join(" ", emptied.Select(field => LibraryBatchEditReasons.EmptiedList(field.Label))),
                    FieldsNotApplied = changes.Select(change => change.Field.Key).ToList(),
                };
            }

            if (blocked.Count > 0)
            {
                return await SendToReviewAsync(item, changes, blocked, ct).ConfigureAwait(false);
            }

            updatedKeys = updates.Count == 0 ? [] : DetailDisplayOverrideCatalog.ApplyChanges(current, updates);
            if (string.Equals(before, Snapshot(current), StringComparison.Ordinal))
            {
                return new LibraryBatchItemResult
                {
                    EntityId = item.RequestedEntityId,
                    Outcome = LibraryBatchItemOutcomes.Unchanged,
                };
            }

            if (!await itemCanonicalRepository.SaveDisplayOverridesAsync(item.WorkId, current, ct).ConfigureAwait(false))
            {
                return Refused(item.RequestedEntityId, LibraryBatchEditReasons.Gone);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Batch edit could not save work {WorkId}; sending it to Needs Review.", item.WorkId);
            return await SendToReviewAsync(
                item,
                changes,
                changes.Select(change => (change.Field, "It could not be saved. Try again from its own page.")).ToList(),
                ct).ConfigureAwait(false);
        }

        await LogActivityAsync(item, updatedKeys, ct).ConfigureAwait(false);
        return new LibraryBatchItemResult
        {
            EntityId = item.RequestedEntityId,
            Outcome = LibraryBatchItemOutcomes.Updated,
        };
    }

    /// <summary>The override map as one comparable string, so an item that needs no change is not written or logged.</summary>
    private static string Snapshot(IReadOnlyDictionary<string, string> overrides) =>
        string.Join(
            "\n",
            overrides.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key.ToLowerInvariant()}={pair.Value}"));

    /// <summary>
    /// The override text one change leads to for one item (a list operation starts from what the item shows now), or
    /// <c>null</c> when the item already shows the result. A result the field's limits reject is added to
    /// <paramref name="blocked"/>; a genre list that would be left empty is added to <paramref name="emptied"/>.
    /// </summary>
    private static string? ValueToStore(
        BatchEditItemRead item,
        IReadOnlyDictionary<string, string> current,
        BatchEditChange change,
        List<(BatchEditField Field, string Reason)> blocked,
        List<BatchEditField> emptied)
    {
        if (change.Field.Kind == BatchEditFieldKind.Scalar)
        {
            return change.Operation == BatchEditFieldCatalog.OpSet ? change.Value ?? string.Empty : string.Empty;
        }

        var shown = current.TryGetValue(change.Field.Key, out var stored)
            ? BatchEditFieldCatalog.ParseList(stored)
            : item.Fields.TryGetValue(change.Field.Key, out var field) ? field.OwnValues : [];
        var result = BatchEditFieldCatalog.ApplyListOperation(shown, change.Operation, change.Values);
        if (BatchEditFieldCatalog.SameItems(shown, result))
        {
            return null;
        }

        if (!BatchEditFieldCatalog.TryNormalizeList(change.Field, result, out var normalized, out var error))
        {
            blocked.Add((change.Field, error ?? "The new list is not allowed."));
            return null;
        }

        if (normalized.Count == 0 && !BatchEditFieldCatalog.CanBeEmptied(change.Field))
        {
            emptied.Add(change.Field);
            return null;
        }

        return BatchEditFieldCatalog.ToOverrideValue(change.Field, normalized);
    }

    /// <summary>Why a pencil would refuse this field on this item, or <c>null</c> when it is editable.</summary>
    private static string? LockedReason(BatchEditItemRead item, BatchEditField field)
    {
        if (!field.IsEditable)
        {
            return field.LockedReason;
        }

        if (!item.Fields.TryGetValue(field.Key, out var read))
        {
            return null;
        }

        var hasOwnValue = !string.IsNullOrWhiteSpace(read.OverrideValue)
                          || (read.OverrideValue is not null && string.Equals(field.Key, "custom_tags", StringComparison.OrdinalIgnoreCase))
                          || read.OwnValues.Count > 0;
        return !hasOwnValue
               && read.InheritedValues.Count > 0
               && BatchEditFieldCatalog.IsInherited(item.MediaType, item.WorkKind, item.HasParentWork, field.Key)
            ? InheritedReason
            : null;
    }

    /// <summary>
    /// Hands an item that could not take the batch to Needs Review as a <c>BatchEditFailed</c> row on the work that was being
    /// edited (a show, season or album as readily as an episode), which has its own trigger so it never merges into a pipeline
    /// row. Nothing was changed on the item, so every requested field is listed as not applied and the reason names the ones
    /// that blocked it.
    /// </summary>
    private async Task<LibraryBatchItemResult> SendToReviewAsync(
        BatchEditItemRead item,
        IReadOnlyList<BatchEditChange> changes,
        IReadOnlyList<(BatchEditField Field, string Reason)> blocked,
        CancellationToken ct)
    {
        var reason = string.Join(" ", blocked.Select(entry => $"{entry.Field.Label}: {entry.Reason}"));
        var keys = changes.Select(change => change.Field.Key).ToList();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var name = string.IsNullOrWhiteSpace(item.Title) ? "this item" : $"\"{item.Title}\"";
            var reviewId = await reviewQueueRepository.InsertAsync(new ReviewQueueEntry
            {
                Id = Guid.NewGuid(),
                EntityId = item.WorkId,
                EntityType = "Work",
                Trigger = ReviewTrigger.BatchEditFailed,
                Detail = $"A batch edit did not change {name}. {reason} Not changed: {string.Join(", ", changes.Select(change => change.Field.Label))}.",
                CreatedAt = now,
                ReviewReadyAt = now,
                AutomationCompletedAt = now,
            }, ct).ConfigureAwait(false);
            return new LibraryBatchItemResult
            {
                EntityId = item.RequestedEntityId,
                Outcome = LibraryBatchItemOutcomes.SentToReview,
                Reason = reason,
                FieldsNotApplied = keys,
                ReviewId = reviewId,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Batch edit could not send work {WorkId} to Needs Review.", item.WorkId);
            return new LibraryBatchItemResult
            {
                EntityId = item.RequestedEntityId,
                Outcome = LibraryBatchItemOutcomes.Refused,
                Reason = $"{reason} It could not be sent to Needs Review.",
                FieldsNotApplied = keys,
            };
        }
    }

    private async Task LogActivityAsync(BatchEditItemRead item, IReadOnlyList<string> updatedKeys, CancellationToken ct)
    {
        try
        {
            await activityRepository.LogAsync(new SystemActivityEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                ActionType = SystemActionType.MetadataManualOverride,
                EntityId = item.WorkId,
                EntityType = "Work",
                CollectionName = item.Title,
                Detail = $"Batch edit saved display overrides for {updatedKeys.Count} field(s): {string.Join(", ", updatedKeys.Distinct(StringComparer.OrdinalIgnoreCase))}.",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The change is saved; a missing activity line must not turn a finished item into a failure.
            logger.LogWarning(ex, "Batch edit saved work {WorkId} but could not record the activity entry.", item.WorkId);
        }
    }

    private static LibraryBatchItemResult Refused(Guid entityId, string reason) => new()
    {
        EntityId = entityId,
        Outcome = LibraryBatchItemOutcomes.Refused,
        Reason = reason,
    };

    private static LibraryBatchFieldState BuildFieldState(BatchEditField field, IReadOnlyList<BatchEditItemRead> items)
    {
        var locked = new List<LibraryBatchLockedItem>();
        var perItem = new List<IReadOnlyList<string>>(items.Count);
        foreach (var item in items)
        {
            var reason = LockedReason(item, field);
            if (reason is not null)
            {
                locked.Add(new LibraryBatchLockedItem { EntityId = item.RequestedEntityId, Reason = reason });
            }

            perItem.Add(EffectiveValues(item, field));
        }

        var isList = field.Kind == BatchEditFieldKind.List;
        var state = new LibraryBatchFieldState
        {
            Key = field.Key,
            Label = field.Label,
            Kind = isList ? LibraryBatchFieldKinds.List : LibraryBatchFieldKinds.Scalar,
            Operations = [.. field.Operations],
            Editable = field.IsEditable && locked.Count == 0,
            LockedItems = locked,
            EmptyCount = perItem.Count(values => values.Count == 0),
        };

        if (perItem.Count == 0)
        {
            return state;
        }

        var agree = perItem.All(values => BatchEditFieldCatalog.SameItems(values, perItem[0]));
        if (agree)
        {
            return isList
                ? Copy(state, isMixed: false, value: null, values: perItem[0], distinct: [], distinctCount: 0)
                : Copy(state, isMixed: false, value: perItem[0].FirstOrDefault(), values: [], distinct: [], distinctCount: 0);
        }

        // Count how many items carry each distinct value, keeping the first spelling seen.
        var counts = new Dictionary<string, (string Value, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in perItem.SelectMany(values => values.Distinct(StringComparer.OrdinalIgnoreCase)))
        {
            counts[value] = counts.TryGetValue(value, out var seen) ? (seen.Value, seen.Count + 1) : (value, 1);
        }

        var ordered = counts.Values
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Copy(
            state,
            isMixed: true,
            value: null,
            values: [],
            distinct: ordered.Take(MaxDistinctValuesShown)
                .Select(entry => new LibraryBatchDistinctValue { Value = entry.Value, Count = entry.Count })
                .ToList(),
            distinctCount: ordered.Count);
    }

    private static LibraryBatchFieldState Copy(
        LibraryBatchFieldState state,
        bool isMixed,
        string? value,
        IReadOnlyList<string> values,
        List<LibraryBatchDistinctValue> distinct,
        int distinctCount) => new()
        {
            Key = state.Key,
            Label = state.Label,
            Kind = state.Kind,
            Operations = state.Operations,
            Editable = state.Editable,
            LockedItems = state.LockedItems,
            EmptyCount = state.EmptyCount,
            IsMixed = isMixed,
            Value = value,
            Values = [.. values],
            DistinctValues = distinct,
            DistinctCount = distinctCount,
        };

    /// <summary>What the detail page shows for the field now: the override, else the item's own data, else its parent's.</summary>
    private static IReadOnlyList<string> EffectiveValues(BatchEditItemRead item, BatchEditField field)
    {
        if (!item.Fields.TryGetValue(field.Key, out var read))
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(read.OverrideValue) || (read.OverrideValue is not null && field.Kind == BatchEditFieldKind.List))
        {
            return field.Kind == BatchEditFieldKind.List
                ? BatchEditFieldCatalog.ParseList(read.OverrideValue)
                : [read.OverrideValue!.Trim()];
        }

        return read.OwnValues.Count > 0 ? read.OwnValues : read.InheritedValues;
    }
}
