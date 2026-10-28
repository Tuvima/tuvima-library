using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves a details change the way the pencil does: presentation overrides on the item's work
/// (<c>works.display_overrides_json</c>), never provider facts. Undo puts the previous overrides back.
/// </summary>
public sealed class DetailsEditSessionChangeHandler(
    IItemCanonicalRepository itemCanonicalRepository,
    ISystemActivityRepository activityRepository) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.Details;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (change.Fields.Count == 0)
        {
            return EditSessionReasons.NoFields;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in change.Fields)
        {
            var key = field.Key.Trim();
            if (!DetailDisplayOverrideCatalog.TryValidateValue(key, field.Value, out _, out var error))
            {
                return error ?? $"'{key}' cannot be changed here.";
            }

            if (!seen.Add(key))
            {
                return EditSessionReasons.DuplicateField(key);
            }
        }

        var state = await itemCanonicalRepository.LoadDisplayOverridesAsync(change.EntityId, ct).ConfigureAwait(false);
        return state.WorkExists ? null : EditSessionReasons.Gone;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var state = await itemCanonicalRepository.LoadDisplayOverridesAsync(change.EntityId, ct).ConfigureAwait(false);
        if (!state.WorkExists)
        {
            throw new InvalidOperationException(EditSessionReasons.Gone);
        }

        var previous = new Dictionary<string, string>(state.Values, StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(state.Values, StringComparer.OrdinalIgnoreCase);
        var updates = change.Fields.ToDictionary(
            field => field.Key.Trim(),
            field => field.Value ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        var updatedKeys = DetailDisplayOverrideCatalog.ApplyChanges(current, updates);

        if (!await itemCanonicalRepository.SaveDisplayOverridesAsync(change.EntityId, current, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(EditSessionReasons.Gone);
        }

        await activityRepository.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = change.EntityId,
            EntityType = "Work",
            Detail = $"Edit session saved display overrides for {updatedKeys.Count} field(s): {string.Join(", ", updatedKeys.Distinct(StringComparer.OrdinalIgnoreCase))}.",
        }, ct).ConfigureAwait(false);

        return async undoCt =>
        {
            await itemCanonicalRepository.SaveDisplayOverridesAsync(change.EntityId, previous, undoCt).ConfigureAwait(false);
        };
    }
}
