using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class CompanionSessionService : ICompanionSessionService
{
    private readonly PlayerDeviceStateRepository _deviceStates;
    private readonly CompanionSessionRepository _sessions;
    private readonly IClientAuthorizationRepository _clients;
    private readonly PlayerService _player;
    private readonly PlayerCatalogueScope _scope;
    private readonly TimeProvider _time;

    public CompanionSessionService(
        PlayerDeviceStateRepository deviceStates,
        CompanionSessionRepository sessions,
        IClientAuthorizationRepository clients,
        PlayerService player,
        PlayerCatalogueScope scope,
        TimeProvider? time = null)
    {
        _deviceStates = deviceStates;
        _sessions = sessions;
        _clients = clients;
        _player = player;
        _scope = scope;
        _time = time ?? TimeProvider.System;
    }

    public async Task<CompanionDeviceListDto> GetDevicesAsync(Guid? profileId, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId, ct);
        var now = _time.GetUtcNow();
        var devices = await LiveDevicesAsync(profile, now, ct);
        return new CompanionDeviceListDto { ProfileId = profile, Devices = devices, ObservedAt = now };
    }

    public async Task<CompanionSessionDto> GetSessionAsync(Guid? profileId, string? companionDeviceId, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId, ct);
        return await BuildSessionAsync(profile, RequireDeviceId(companionDeviceId, nameof(companionDeviceId)), ct);
    }

    public async Task<CompanionSessionDto> FollowAsync(CompanionFollowRequestDto request, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(request.ProfileId, ct);
        var companion = RequireDeviceId(request.CompanionDeviceId, nameof(request.CompanionDeviceId));
        var followed = RequireDeviceId(request.DeviceId, nameof(request.DeviceId));

        // Only a device of the caller's own profile can be followed: one that has reported playback for it, or one
        // of its paired devices. Anything else looks exactly like a device that does not exist.
        var known = await _deviceStates.GetAsync(profile, followed, ct) is not null ||
            (await ClientDeviceNamesAsync(profile, ct)).ContainsKey(followed);
        if (!known)
        {
            throw new PlayerResourceDeniedException();
        }

        await _sessions.UpsertFollowAsync(profile, companion, followed, _time.GetUtcNow(), ct);
        return await BuildSessionAsync(profile, companion, ct);
    }

    public async Task<CompanionSessionDto> SetManualAsync(CompanionManualPositionRequestDto request, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(request.ProfileId, ct);
        var companion = RequireDeviceId(request.CompanionDeviceId, nameof(request.CompanionDeviceId));
        if (request.ChapterIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ChapterIndex), request.ChapterIndex, "Chapter must be zero or more.");
        }

        if (request.PositionSeconds is { } position && (!double.IsFinite(position) || position < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(request.PositionSeconds), position, "Position must be zero or more.");
        }

        if (await _scope.FindAssetAsync(request.WorkId, null, ct) is null)
        {
            throw new PlayerResourceDeniedException();
        }

        await _sessions.UpsertManualAsync(
            profile, companion, request.WorkId, request.ChapterIndex, request.PositionSeconds, _time.GetUtcNow(), ct);
        return await BuildSessionAsync(profile, companion, ct);
    }

    public async Task ClearAsync(Guid? profileId, string? companionDeviceId, CancellationToken ct = default)
    {
        var profile = await _scope.RequireProfileAsync(profileId, ct);
        await _sessions.DeleteAsync(profile, RequireDeviceId(companionDeviceId, nameof(companionDeviceId)), ct);
    }

    private async Task<CompanionSessionDto> BuildSessionAsync(Guid profile, string companionDeviceId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var record = await _sessions.GetAsync(profile, companionDeviceId, ct);
        if (record is null)
        {
            return new CompanionSessionDto
            {
                ProfileId = profile,
                CompanionDeviceId = companionDeviceId,
                Mode = CompanionModes.None,
                ObservedAt = now,
            };
        }

        if (record.Mode == CompanionModes.Follow && record.FollowedDeviceId is { } followedId)
        {
            var row = await _deviceStates.GetAsync(profile, followedId, ct);
            var followed = row is not null && IsLive(row, now) && await IsVisibleAsync(row, ct)
                ? await ToLiveDeviceAsync(row, await ClientDeviceNamesAsync(profile, ct), profile, ct)
                : null;
            return new CompanionSessionDto
            {
                ProfileId = profile,
                CompanionDeviceId = companionDeviceId,
                Mode = CompanionModes.Follow,
                FollowedDeviceId = followedId,
                Followed = followed,
                IsFollowedDeviceLive = followed is not null,
                UpdatedAt = record.UpdatedAt,
                ObservedAt = now,
            };
        }

        CompanionManualPositionDto? manual = null;
        if (record.ManualWorkId is { } workId && await _scope.FindAssetAsync(workId, null, ct) is not null)
        {
            manual = new CompanionManualPositionDto
            {
                WorkId = workId,
                ChapterIndex = record.ManualChapterIndex,
                PositionSeconds = record.ManualPositionSeconds,
                Item = await ResolveItemAsync(workId, null, profile, ct),
            };
        }

        return new CompanionSessionDto
        {
            ProfileId = profile,
            CompanionDeviceId = companionDeviceId,
            Mode = CompanionModes.Manual,
            Manual = manual,
            UpdatedAt = record.UpdatedAt,
            ObservedAt = now,
        };
    }

    private async Task<IReadOnlyList<CompanionLiveDeviceDto>> LiveDevicesAsync(Guid profile, DateTimeOffset now, CancellationToken ct)
    {
        var live = new List<PlayerDeviceStateRecord>();
        foreach (var row in await _deviceStates.ListAsync(profile, ct))
        {
            if (IsLive(row, now) && await IsVisibleAsync(row, ct))
            {
                live.Add(row);
            }
        }

        if (live.Count == 0)
        {
            return [];
        }

        var names = await ClientDeviceNamesAsync(profile, ct);
        var devices = new List<CompanionLiveDeviceDto>(live.Count);
        foreach (var row in live.OrderByDescending(candidate => candidate.UpdatedAt))
        {
            devices.Add(await ToLiveDeviceAsync(row, names, profile, ct));
        }

        return devices;
    }

    /// <summary>Playing or paused, and heard from within the same window that keeps the shared player live.</summary>
    private static bool IsLive(PlayerDeviceStateRecord row, DateTimeOffset now) =>
        row.PlaybackState is PlayerPlaybackStates.Playing or PlayerPlaybackStates.Paused &&
        now - row.UpdatedAt <= PlayerService.StaleSessionWindow;

    private async Task<bool> IsVisibleAsync(PlayerDeviceStateRecord row, CancellationToken ct) =>
        (await _scope.AssetIdsAsync(ct)).Contains(row.AssetId);

    private async Task<CompanionLiveDeviceDto> ToLiveDeviceAsync(
        PlayerDeviceStateRecord row,
        IReadOnlyDictionary<string, string> names,
        Guid profile,
        CancellationToken ct)
    {
        var item = row.WorkId is { } workId ? await ResolveItemAsync(workId, row.AssetId, profile, ct) : null;
        return new CompanionLiveDeviceDto
        {
            DeviceId = row.DeviceId,
            DeviceName = names.GetValueOrDefault(row.DeviceId),
            Client = row.Client,
            PlaybackState = row.PlaybackState,
            PositionSeconds = row.PositionSeconds,
            DurationSeconds = row.DurationSeconds,
            WorkId = row.WorkId,
            AssetId = row.AssetId,
            Item = item is null
                ? null
                : item with { PositionSeconds = row.PositionSeconds, DurationSeconds = row.DurationSeconds ?? item.DurationSeconds },
            UpdatedAt = row.UpdatedAt,
        };
    }

    /// <summary>The playable item for a work, or null when it is outside the caller's scope or not playable media.</summary>
    private async Task<PlayerQueueItemDto?> ResolveItemAsync(Guid workId, Guid? assetId, Guid profile, CancellationToken ct) =>
        await _scope.FindAssetAsync(workId, assetId, ct) is null
            ? null
            : await _player.ResolvePlayableWorkAsync(workId, assetId, profile, ct);

    /// <summary>The profile's active paired devices by id (the form a paired app reports as its device id) and name.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ClientDeviceNamesAsync(Guid profile, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in await _clients.GetDevicesAsync(profile, ct))
        {
            if (device.IsActive)
            {
                names[device.Id.ToString("D")] = device.DeviceName;
            }
        }

        return names;
    }

    private static string RequireDeviceId(string? deviceId, string parameterName) =>
        string.IsNullOrWhiteSpace(deviceId)
            ? throw new ArgumentException("A device id is required.", parameterName)
            : deviceId.Trim();
}
