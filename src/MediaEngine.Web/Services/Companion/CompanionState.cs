using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Companion;

/// <summary>
/// The phone companion's state for one browser: which of the active profile's devices it follows (or the place it holds by
/// hand), and the reads and writes that change it. It holds no playback of its own and only ever sees the active profile's
/// own devices, because the Engine decides that from the caller's sign-in. The companion's own device id is a random value
/// kept in this browser, so two phones on one profile follow independently.
/// </summary>
public sealed class CompanionState(
    IEngineApiClient api,
    ICompanionDeviceIdStore deviceIds,
    IActiveProfileAccessor activeProfile)
{
    private string? _deviceId;
    private Guid? _profileId;

    /// <summary>The profile the Engine last answered for; used to save the spoiler preference to the same profile.</summary>
    public Guid? ProfileId => _profileId ?? activeProfile.ProfileId;

    /// <summary>The active profile's own devices that are playing or paused right now; null when the Engine could not be reached.</summary>
    public async Task<CompanionDeviceListDto?> LoadDevicesAsync(CancellationToken ct = default)
    {
        var devices = await api.GetCompanionDevicesAsync(activeProfile.ProfileId, ct).ConfigureAwait(false);
        Remember(devices?.ProfileId);
        return devices;
    }

    /// <summary>This browser's current choice; null when the Engine could not be reached.</summary>
    public async Task<CompanionSessionDto?> LoadSessionAsync(CancellationToken ct = default)
    {
        var session = await api.GetCompanionSessionAsync(await DeviceIdAsync().ConfigureAwait(false), activeProfile.ProfileId, ct)
            .ConfigureAwait(false);
        Remember(session?.ProfileId);
        return session;
    }

    public async Task<CompanionSessionDto?> FollowAsync(string deviceId, CancellationToken ct = default)
    {
        var session = await api.FollowCompanionDeviceAsync(
            new CompanionFollowRequestDto
            {
                ProfileId = activeProfile.ProfileId,
                CompanionDeviceId = await DeviceIdAsync().ConfigureAwait(false),
                DeviceId = deviceId,
            },
            ct).ConfigureAwait(false);
        Remember(session?.ProfileId);
        return session;
    }

    /// <summary>Holds a place chosen by hand: an episode (its own Work) or a chapter of a book or audiobook.</summary>
    public async Task<CompanionSessionDto?> HoldAsync(Guid workId, int? chapterIndex = null, CancellationToken ct = default)
    {
        var session = await api.SetCompanionManualPositionAsync(
            new CompanionManualPositionRequestDto
            {
                ProfileId = activeProfile.ProfileId,
                CompanionDeviceId = await DeviceIdAsync().ConfigureAwait(false),
                WorkId = workId,
                ChapterIndex = chapterIndex,
            },
            ct).ConfigureAwait(false);
        Remember(session?.ProfileId);
        return session;
    }

    public async Task<bool> ClearAsync(CancellationToken ct = default) =>
        await api.ClearCompanionSessionAsync(await DeviceIdAsync().ConfigureAwait(false), activeProfile.ProfileId, ct)
            .ConfigureAwait(false);

    /// <summary>What the companion shows for a Work at a position; null when it cannot be read or the profile may not see it.</summary>
    public Task<CompanionContextDto?> LoadContextAsync(CompanionPlace place, CancellationToken ct = default) =>
        api.GetCompanionContextAsync(place.WorkId, place.ChapterIndex, place.PositionSeconds, activeProfile.ProfileId, ct);

    /// <summary>
    /// Saves the profile's Spoilers preference, the very same setting as Settings > Preferences > Playback. Returns the saved
    /// level, or null when it could not be saved.
    /// </summary>
    public async Task<string?> SaveSpoilerLevelAsync(string level, CancellationToken ct = default)
    {
        if (ProfileId is not { } profileId)
        {
            return null;
        }

        var saved = await api.SaveSpoilerLevelAsync(profileId, level, ct).ConfigureAwait(false);
        return saved?.SpoilerLevel;
    }

    private async ValueTask<string> DeviceIdAsync() => _deviceId ??= await deviceIds.GetAsync().ConfigureAwait(false);

    private void Remember(Guid? profileId)
    {
        if (profileId is { } id && id != Guid.Empty)
        {
            _profileId = id;
        }
    }
}

/// <summary>
/// The place a companion session holds: which Work, and which chapter of it. Following a playing device places it by the
/// device's position within the audiobook's chapters; a place set by hand is exactly what the person chose.
/// </summary>
public readonly record struct CompanionPlace(Guid WorkId, int? ChapterIndex, string Mode, string? DeviceLabel, double? PositionSeconds = null)
{
    /// <summary>The place a session holds, or null when it holds none (no choice, or a followed device that is no longer live).</summary>
    public static CompanionPlace? From(CompanionSessionDto? session)
    {
        if (session is null)
        {
            return null;
        }

        if (session.Mode == CompanionModes.Follow && session.Followed is { WorkId: { } workId } followed && session.IsFollowedDeviceLive)
        {
            return new CompanionPlace(workId, null, CompanionModes.Follow, DeviceLabelOf(followed), followed.PositionSeconds);
        }

        return session.Mode == CompanionModes.Manual && session.Manual is { } manual
            ? new CompanionPlace(manual.WorkId, manual.ChapterIndex, CompanionModes.Manual, null)
            : null;
    }

    /// <summary>"Living Room TV", or the client's own name when the device has none.</summary>
    public static string DeviceLabelOf(CompanionLiveDeviceDto device) =>
        !string.IsNullOrWhiteSpace(device.DeviceName) ? device.DeviceName.Trim()
        : !string.IsNullOrWhiteSpace(device.Client) ? char.ToUpperInvariant(device.Client[0]) + device.Client[1..]
        : "another device";
}
