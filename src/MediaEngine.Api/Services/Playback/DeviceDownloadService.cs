using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

public enum DeviceDownloadOutcome
{
    Ok,

    /// <summary>The caller is not the paired app of a live device of its own account (a Dashboard session, another account's token or a service credential).</summary>
    Forbidden,

    /// <summary>Unknown, someone else's, not ready, expired or not downloadable by this profile: all the same answer, so ids cannot be probed.</summary>
    NotFound,

    /// <summary>The device already holds <see cref="DeviceDownloadRepository.MaxRowsPerDevice"/> entries.</summary>
    TooMany,

    /// <summary>The report names a kind the Engine does not accept (including the reserved <c>book</c> kind) or leaves out the id its kind needs.</summary>
    BadRequest,

    /// <summary>The token does not hold the photo (View) scope that keeping a photo needs.</summary>
    PhotoScopeMissing,
}

public sealed record DeviceDownloadListResult(DeviceDownloadListDto? Downloads, DeviceDownloadOutcome Outcome = DeviceDownloadOutcome.Forbidden);

/// <summary>
/// The list of offline variants and kept photos a paired app says it has stored. The device always comes from the app's
/// own token; the list is scoped to that device and its active profile, so one device never sees another's. A variant
/// entry can only be added for a ready, unexpired variant that this profile and device prepared and may download (the
/// same catalogue access the download route applies), and its size is clamped to the variant's size. A photo entry can
/// only be added for a photo or video the active person may read through the photo routes (<see cref="IPhotoDownloadAccess"/>,
/// the View Mine scope), and a listed photo the person can no longer read is left off the list.
/// </summary>
public sealed class DeviceDownloadService(
    IClientAuthorizationRepository devices,
    DeviceDownloadRepository downloads,
    TimeProvider clock,
    IPhotoDownloadAccess? photoAccess = null)
{
    public async Task<DeviceDownloadListResult> ListAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        if (await ResolveAsync(authority, ct).ConfigureAwait(false) is not { } owner)
        {
            return new(null);
        }

        var list = await downloads.ListAsync(owner.DeviceId, owner.ProfileId, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (list.Any(entry => entry.PhotoItemId is not null))
        {
            // A photo whose access is gone (shared Gallery withdrawn, document rules, removed from the index) is not on the
            // list, so the app purges its local copy. A file that is only missing from disk for now does not hide a photo.
            // Without the photo scope nothing is judged readable. All kept photos are decided in one bulk pass.
            var visibleIds = await VisiblePhotosAsync(
                authority, list.Where(entry => entry.PhotoItemId is not null).Select(entry => entry.PhotoItemId!.Value).ToList(), ct)
                .ConfigureAwait(false);
            list = list.Where(entry => entry.PhotoItemId is not { } itemId || visibleIds.Contains(itemId)).ToList();
        }

        return new(new DeviceDownloadListDto { Downloads = list }, DeviceDownloadOutcome.Ok);
    }

    private async Task<IReadOnlySet<Guid>> VisiblePhotosAsync(
        RequestAuthority authority, IReadOnlyCollection<Guid> itemIds, CancellationToken ct)
    {
        if (photoAccess is null || itemIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var result = await photoAccess.EvaluateManyAsync(authority, itemIds, ct).ConfigureAwait(false);
        return result.ScopeMissing ? new HashSet<Guid>() : result.Visible;
    }

    /// <summary>
    /// Makes room on a full device: removes this person's kept photos that the list no longer shows them (so they do not
    /// count towards the limit any more). Returns whether anything was removed.
    /// </summary>
    private async Task<bool> PruneHiddenPhotosAsync(RequestAuthority authority, DeviceOwner owner, CancellationToken ct)
    {
        var kept = await downloads.ListPhotoItemIdsAsync(owner.DeviceId, owner.ProfileId, ct).ConfigureAwait(false);
        if (kept.Count == 0)
        {
            return false;
        }

        var visible = await VisiblePhotosAsync(authority, kept, ct).ConfigureAwait(false);
        var hidden = kept.Where(id => !visible.Contains(id)).ToList();
        return hidden.Count > 0
            && await downloads.DeletePhotosAsync(owner.DeviceId, owner.ProfileId, hidden, ct).ConfigureAwait(false) > 0;
    }

    /// <param name="mayDownloadAsset">The catalogue check for the asset (the download route's access check); false reads as not found.</param>
    public async Task<DeviceDownloadOutcome> UpsertAsync(
        RequestAuthority authority,
        UpsertDeviceDownloadRequest request,
        Func<Guid, CancellationToken, ValueTask<bool>> mayDownloadAsset,
        CancellationToken ct = default)
    {
        if (await ResolveAsync(authority, ct).ConfigureAwait(false) is not { } owner)
        {
            return DeviceDownloadOutcome.Forbidden;
        }

        var kind = string.IsNullOrWhiteSpace(request.Kind) ? DeviceDownloadKinds.Variant : request.Kind.Trim().ToLowerInvariant();
        if (kind == DeviceDownloadKinds.Photo)
        {
            return await UpsertPhotoAsync(authority, owner, request, ct).ConfigureAwait(false);
        }

        if (kind != DeviceDownloadKinds.Variant)
        {
            // "book" is reserved for later; it is refused like any kind this Engine does not know.
            return DeviceDownloadOutcome.BadRequest;
        }

        var now = clock.GetUtcNow();
        if (request.AssetId == Guid.Empty || request.VariantId == Guid.Empty ||
            !await mayDownloadAsset(request.AssetId, ct).ConfigureAwait(false) ||
            await downloads.FindOwnedReadyVariantAsync(request.AssetId, request.VariantId, owner.ProfileId, owner.DeviceId, now, ct)
                .ConfigureAwait(false) is not { } variant)
        {
            return DeviceDownloadOutcome.NotFound;
        }

        // A device cannot claim more than the variant holds, and a stored time later than now is the server's now.
        var storedBytes = Math.Max(0, request.StoredBytes);
        if (variant.FileSizeBytes is { } size)
        {
            storedBytes = Math.Min(storedBytes, Math.Max(0, size));
        }

        var storedAt = request.StoredAt > now ? now : request.StoredAt;
        if (await downloads.UpsertAsync(
                owner.DeviceId, owner.ProfileId, request.AssetId, request.VariantId, storedBytes, storedAt, now, ct)
            .ConfigureAwait(false))
        {
            return DeviceDownloadOutcome.Ok;
        }

        // Full: kept photos the list no longer shows do not count, so clear those and try once more.
        return await PruneHiddenPhotosAsync(authority, owner, ct).ConfigureAwait(false)
            && await downloads.UpsertAsync(
                owner.DeviceId, owner.ProfileId, request.AssetId, request.VariantId, storedBytes, storedAt, now, ct)
            .ConfigureAwait(false)
            ? DeviceDownloadOutcome.Ok
            : DeviceDownloadOutcome.TooMany;
    }

    private async Task<DeviceDownloadOutcome> UpsertPhotoAsync(
        RequestAuthority authority, DeviceOwner owner, UpsertDeviceDownloadRequest request, CancellationToken ct)
    {
        if (request.PhotoItemId is not { } itemId || itemId == Guid.Empty)
        {
            return DeviceDownloadOutcome.BadRequest;
        }

        // Same rules as the photo preview route: a photo the person cannot see, a document and an unknown id are all not found.
        var access = photoAccess is null
            ? PhotoKeepAccess.NotFound
            : await photoAccess.EvaluateAsync(authority, itemId, ct).ConfigureAwait(false);
        if (access != PhotoKeepAccess.Allowed)
        {
            // A file that is not on disk right now cannot have been downloaded (the preview route answers 404 too).
            return access == PhotoKeepAccess.ScopeMissing ? DeviceDownloadOutcome.PhotoScopeMissing : DeviceDownloadOutcome.NotFound;
        }

        var now = clock.GetUtcNow();
        var storedAt = request.StoredAt > now ? now : request.StoredAt;
        // A preview is a few hundred kilobytes; a device cannot claim an absurd size.
        var storedBytes = Math.Clamp(request.StoredBytes, 0, MaxPhotoStoredBytes);
        if (await downloads.UpsertPhotoAsync(owner.DeviceId, owner.ProfileId, itemId, storedBytes, storedAt, now, ct)
            .ConfigureAwait(false))
        {
            return DeviceDownloadOutcome.Ok;
        }

        // Full: kept photos the list no longer shows do not count, so clear those and try once more.
        return await PruneHiddenPhotosAsync(authority, owner, ct).ConfigureAwait(false)
            && await downloads.UpsertPhotoAsync(owner.DeviceId, owner.ProfileId, itemId, storedBytes, storedAt, now, ct)
            .ConfigureAwait(false)
            ? DeviceDownloadOutcome.Ok
            : DeviceDownloadOutcome.TooMany;
    }

    /// <summary>The most a device may report for one kept photo (256 MB; a 2048 px preview is well under 10 MB).</summary>
    public const long MaxPhotoStoredBytes = 256L * 1024 * 1024;

    public async Task<DeviceDownloadOutcome> RemovePhotoAsync(RequestAuthority authority, Guid itemId, CancellationToken ct = default)
    {
        if (await ResolveAsync(authority, ct).ConfigureAwait(false) is not { } owner)
        {
            return DeviceDownloadOutcome.Forbidden;
        }

        return await downloads.DeletePhotoAsync(owner.DeviceId, owner.ProfileId, itemId, ct).ConfigureAwait(false)
            ? DeviceDownloadOutcome.Ok
            : DeviceDownloadOutcome.NotFound;
    }

    public async Task<DeviceDownloadOutcome> RemoveAsync(RequestAuthority authority, Guid variantId, CancellationToken ct = default)
    {
        if (await ResolveAsync(authority, ct).ConfigureAwait(false) is not { } owner)
        {
            return DeviceDownloadOutcome.Forbidden;
        }

        return await downloads.DeleteAsync(owner.DeviceId, owner.ProfileId, variantId, ct).ConfigureAwait(false)
            ? DeviceDownloadOutcome.Ok
            : DeviceDownloadOutcome.NotFound;
    }

    private readonly record struct DeviceOwner(Guid DeviceId, Guid ProfileId);

    /// <summary>The live paired device and active profile of the token's own account, or null for anyone else.</summary>
    private async Task<DeviceOwner?> ResolveAsync(RequestAuthority authority, CancellationToken ct)
    {
        if (!PhoneBackupProfileService.IsPairedApp(authority) || authority.ActiveProfileId is not { } profileId)
        {
            return null;
        }

        var device = await devices.GetDeviceAsync(authority.DeviceId!.Value, ct).ConfigureAwait(false);
        return device is { IsActive: true } && device.AccountId == authority.AccountId && device.ProfileId == profileId
            ? new DeviceOwner(device.Id, profileId)
            : null;
    }
}
