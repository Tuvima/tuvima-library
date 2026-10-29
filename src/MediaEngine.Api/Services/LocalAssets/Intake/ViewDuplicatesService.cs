using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// The duplicates cleanup job (Photo and video filing spec D16): finds exact copies already inside one personal space or the Shared
/// Library, keeps a report, and lets an administrator keep one copy so the rest go to Trash. It never removes anything on its own, and
/// an administrator only sees the Shared Library and their own space, so another person's private photos stay private.
/// </summary>
public sealed class ViewDuplicatesService(
    IViewIntakeRepository intake,
    ILocalAssetRepository assets,
    IConfigurationLoader configuration,
    TimeProvider time)
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private DateTimeOffset? _lastScanAt;

    /// <summary>Looks at every space and the Shared Library now; groups no longer found are dropped, and dismissed groups return only if they grew.</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var started = time.GetUtcNow();
            var found = await intake.FindDuplicateCandidatesAsync(ct).ConfigureAwait(false);
            foreach (var group in found)
            {
                await intake.UpsertDuplicateGroupAsync(group.LibraryId, group.ScopeKind, group.ContentHash, (int)group.Copies, started, ct)
                    .ConfigureAwait(false);
            }

            await intake.RemoveUnseenDuplicateGroupsAsync("personal", started, ct).ConfigureAwait(false);
            await intake.RemoveUnseenDuplicateGroupsAsync("shared", started, ct).ConfigureAwait(false);
            _lastScanAt = time.GetUtcNow();
            return found.Count;
        }
        finally
        {
            RunGate.Release();
        }
    }

    /// <summary>The open groups this person may act on.</summary>
    public async Task<ViewDuplicateReportDto> GetReportAsync(Guid profileId, CancellationToken ct = default)
    {
        var settings = configuration.LoadPhotoFiling();
        var mine = (await intake.GetOwnLibraryIdsAsync(profileId, ct).ConfigureAwait(false)).ToHashSet();
        var groups = new List<ViewDuplicateGroupDto>();
        foreach (var group in await intake.GetDuplicateGroupsAsync(null, null, false, ct).ConfigureAwait(false))
        {
            if (!mine.Contains(group.LibraryId))
            {
                continue;
            }

            var copies = await intake.GetDuplicateCopiesAsync(group.ScopeKind, group.LibraryId, group.ContentHash, ct).ConfigureAwait(false);
            if (copies.Count < 2)
            {
                continue;
            }

            groups.Add(new ViewDuplicateGroupDto(group.Id, group.ScopeKind,
                copies.Select(copy => new ViewDuplicateCopyDto(copy.ItemId, copy.FileName, copy.CapturedAt, copy.CreatedAt, copy.ByteSize)).ToList()));
        }

        return new ViewDuplicateReportDto(settings.DuplicateScanEnabled, Math.Clamp(settings.DuplicateScanHour, 0, 23), _lastScanAt, groups);
    }

    /// <summary>Keeps one copy and sends the others to Trash. Returns null when the group is not this person's to act on.</summary>
    public async Task<ViewDuplicateKeepResultDto?> KeepOneAsync(Guid profileId, Guid groupId, Guid keepItemId, CancellationToken ct = default)
    {
        var group = await OwnedGroupAsync(profileId, groupId, ct).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var copies = await intake.GetDuplicateCopiesAsync(group.ScopeKind, group.LibraryId, group.ContentHash, ct).ConfigureAwait(false);
        if (copies.All(copy => copy.ItemId != keepItemId))
        {
            throw new ArgumentException("Choose one of the copies in this group to keep.", nameof(keepItemId));
        }

        var moved = 0;
        foreach (var copy in copies.Where(copy => copy.ItemId != keepItemId))
        {
            if (await assets.SetLifecycleStateAsync(copy.ItemId, LocalAssetLifecycleState.Trashed, ct).ConfigureAwait(false))
            {
                moved++;
            }
        }

        await intake.DismissDuplicateGroupAsync(groupId, profileId, time.GetUtcNow(), ct).ConfigureAwait(false);
        return new ViewDuplicateKeepResultDto(moved);
    }

    /// <summary>Leaves the copies as they are; the group returns only if more copies turn up.</summary>
    public async Task<bool> DismissAsync(Guid profileId, Guid groupId, CancellationToken ct = default)
    {
        var group = await OwnedGroupAsync(profileId, groupId, ct).ConfigureAwait(false);
        return group is not null
            && await intake.DismissDuplicateGroupAsync(groupId, profileId, time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    private async Task<ViewDuplicateGroup?> OwnedGroupAsync(Guid profileId, Guid groupId, CancellationToken ct)
    {
        var group = await intake.GetDuplicateGroupAsync(groupId, ct).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var mine = await intake.GetOwnLibraryIdsAsync(profileId, ct).ConfigureAwait(false);
        return mine.Contains(group.LibraryId) ? group : null;
    }
}
