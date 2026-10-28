using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves a details change the way the pencil does: presentation overrides on the item's work
/// (<c>works.display_overrides_json</c>), never provider facts. Undo puts the previous overrides back. An id that is not a library
/// title but a person, Series, curated collection or Universe (universal editor spec D17, D17a) is handed to that kind's
/// <see cref="IEditSubject"/>, which writes through the kind's own existing write path.
/// </summary>
public sealed class DetailsEditSessionChangeHandler(
    IItemCanonicalRepository itemCanonicalRepository,
    ISystemActivityRepository activityRepository,
    EditSubjectDirectory? subjects = null) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.Details;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (change.Fields.Count == 0)
        {
            return EditSessionReasons.NoFields;
        }

        if (await FindSubjectAsync(change.EntityId, ct).ConfigureAwait(false) is { } subject)
        {
            return CheckSubjectFields(subject, change);
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
        if (await FindSubjectAsync(change.EntityId, ct).ConfigureAwait(false) is { } subject)
        {
            return await ApplySubjectAsync(subject, change, ct).ConfigureAwait(false);
        }

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

    // A library title always wins: only an id that is no title is looked up as a person, Series, collection or Universe.
    private async Task<IEditSubject?> FindSubjectAsync(Guid entityId, CancellationToken ct)
    {
        if (subjects is null)
        {
            return null;
        }

        var state = await itemCanonicalRepository.LoadDisplayOverridesAsync(entityId, ct).ConfigureAwait(false);
        return state.WorkExists ? null : await subjects.FindAsync(entityId, ct).ConfigureAwait(false);
    }

    private static string? CheckSubjectFields(IEditSubject subject, EditSessionChange change)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in change.Fields)
        {
            var key = field.Key.Trim();
            if (subject.CheckField(key, field.Value) is { } reason)
            {
                return reason;
            }

            if (!seen.Add(key))
            {
                return EditSessionReasons.DuplicateField(key);
            }
        }

        return null;
    }

    private async Task<Func<CancellationToken, Task>?> ApplySubjectAsync(IEditSubject subject, EditSessionChange change, CancellationToken ct)
    {
        var undo = await subject.ApplyFieldsAsync(change.EntityId, change.Fields, ct).ConfigureAwait(false);
        await activityRepository.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = change.EntityId,
            EntityType = subject.ArtworkEntityType,
            Detail = $"Edit session saved {change.Fields.Count} {subject.Kind} field(s): {string.Join(", ", change.Fields.Select(field => field.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))}.",
        }, ct).ConfigureAwait(false);
        return undo;
    }
}
