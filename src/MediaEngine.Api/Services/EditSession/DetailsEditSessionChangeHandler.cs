using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
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
    EditSubjectDirectory? subjects = null,
    IMetadataClaimRepository? claimRepository = null,
    ICanonicalValueRepository? canonicalRepository = null) : IEditSessionChangeHandler
{
    /// <summary>
    /// The Explicit mark (hides a video or a series of videos from every content-limited profile) rides on a details change under this
    /// key, with "true" or "false". It is saved as the item's own user-locked claim, exactly as the Explicit action always did.
    /// </summary>
    public const string ExplicitKey = MetadataFieldConstants.Explicit;

    private static bool IsExplicitKey(string key) => string.Equals(key.Trim(), ExplicitKey, StringComparison.OrdinalIgnoreCase);

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
            if (IsExplicitKey(key))
            {
                if (!seen.Add(key))
                {
                    return EditSessionReasons.DuplicateField(key);
                }

                if (await CheckExplicitAsync(change.EntityId, field.Value, ct).ConfigureAwait(false) is { } explicitReason)
                {
                    return explicitReason;
                }

                continue;
            }

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

        var explicitField = change.Fields.FirstOrDefault(field => IsExplicitKey(field.Key));
        var otherFields = change.Fields.Where(field => !IsExplicitKey(field.Key)).ToList();
        Func<CancellationToken, Task>? undoExplicit = null;
        if (explicitField is not null)
        {
            undoExplicit = await ApplyExplicitAsync(change.EntityId, explicitField.Value, ct).ConfigureAwait(false);
            if (otherFields.Count == 0)
            {
                return undoExplicit;
            }
        }

        var previous = new Dictionary<string, string>(state.Values, StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(state.Values, StringComparer.OrdinalIgnoreCase);
        var updates = otherFields.ToDictionary(
            field => field.Key.Trim(),
            field => field.Value ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        var updatedKeys = DetailDisplayOverrideCatalog.ApplyChanges(current, updates);

        if (!await itemCanonicalRepository.SaveDisplayOverridesAsync(change.EntityId, current, ct).ConfigureAwait(false))
        {
            if (undoExplicit is not null)
            {
                await undoExplicit(ct).ConfigureAwait(false);
            }

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
            if (undoExplicit is not null)
            {
                await undoExplicit(undoCt).ConfigureAwait(false);
            }
        };
    }

    // Only a video or a series of videos can be marked; the id may be a file, so it is resolved to its work first.
    private async Task<string?> CheckExplicitAsync(Guid entityId, string? value, CancellationToken ct)
    {
        if (claimRepository is null || canonicalRepository is null)
        {
            return "The Explicit mark cannot be saved here.";
        }

        if (!bool.TryParse(value?.Trim(), out _))
        {
            return "Explicit must be on or off.";
        }

        var workId = await itemCanonicalRepository.ResolveWorkIdForAssetAsync(entityId, ct).ConfigureAwait(false) ?? entityId;
        if (!string.Equals(await itemCanonicalRepository.GetWorkMediaTypeAsync(workId, ct).ConfigureAwait(false), nameof(MediaType.Videos), StringComparison.OrdinalIgnoreCase))
        {
            return "Only a video or a series of videos can be marked Explicit.";
        }

        return null;
    }

    private async Task<Func<CancellationToken, Task>> ApplyExplicitAsync(Guid entityId, string? value, CancellationToken ct)
    {
        var workId = await itemCanonicalRepository.ResolveWorkIdForAssetAsync(entityId, ct).ConfigureAwait(false) ?? entityId;
        var wasExplicit = (await canonicalRepository!.GetByEntityAsync(workId, ct).ConfigureAwait(false)).Any(v =>
            string.Equals(v.Key, ExplicitKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Value, "true", StringComparison.OrdinalIgnoreCase));
        var isExplicit = bool.Parse(value!.Trim());
        await WriteExplicitAsync(workId, isExplicit, ct).ConfigureAwait(false);
        await activityRepository.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = workId,
            EntityType = "Work",
            Detail = isExplicit ? "Marked Explicit." : "Explicit mark removed.",
        }, ct).ConfigureAwait(false);
        return undoCt => WriteExplicitAsync(workId, wasExplicit, undoCt);
    }

    private async Task WriteExplicitAsync(Guid workId, bool isExplicit, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var text = isExplicit ? "true" : "false";
        await claimRepository!.InsertBatchAsync(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = workId,
                ProviderId = WellKnownProviders.UserManual,
                ClaimKey = ExplicitKey,
                ClaimValue = text,
                ClaimedAt = now,
                Confidence = 1.0,
                IsUserLocked = true,
            },
        ], ct).ConfigureAwait(false);
        await canonicalRepository!.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = workId,
                Key = ExplicitKey,
                Value = text,
                LastScoredAt = now,
                WinningProviderId = WellKnownProviders.UserManual,
            },
        ], ct).ConfigureAwait(false);
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
