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
}

public sealed record DeviceDownloadListResult(DeviceDownloadListDto? Downloads, DeviceDownloadOutcome Outcome = DeviceDownloadOutcome.Forbidden);

/// <summary>
/// The list of offline variants a paired app says it has stored. The device always comes from the app's own token;
/// the list is scoped to that device and its active profile, so one device never sees another's. An entry can only be
/// added for a ready, unexpired variant that this profile and device prepared and may download (the same catalogue
/// access the download route applies), and its size is clamped to the variant's size.
/// </summary>
public sealed class DeviceDownloadService(
    IClientAuthorizationRepository devices,
    DeviceDownloadRepository downloads,
    TimeProvider clock)
{
    public async Task<DeviceDownloadListResult> ListAsync(RequestAuthority authority, CancellationToken ct = default)
    {
        if (await ResolveAsync(authority, ct).ConfigureAwait(false) is not { } owner)
        {
            return new(null);
        }

        var list = await downloads.ListAsync(owner.DeviceId, owner.ProfileId, clock.GetUtcNow(), ct).ConfigureAwait(false);
        return new(new DeviceDownloadListDto { Downloads = list }, DeviceDownloadOutcome.Ok);
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
        return await downloads.UpsertAsync(
                owner.DeviceId, owner.ProfileId, request.AssetId, request.VariantId, storedBytes, storedAt, now, ct)
            .ConfigureAwait(false)
            ? DeviceDownloadOutcome.Ok
            : DeviceDownloadOutcome.TooMany;
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
