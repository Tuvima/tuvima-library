using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Lets one device on a profile follow where another of the same profile's devices is playing, or hold a position set
/// by hand. Only the caller's own profile is ever visible, and every item is checked against the caller's catalogue
/// scope. A resource outside that scope surfaces as <c>PlayerResourceDeniedException</c> (a 404 on the wire); invalid
/// input surfaces as <see cref="ArgumentException"/> (a 400).
/// </summary>
public interface ICompanionSessionService
{
    /// <summary>The caller's devices that are playing or paused right now, newest first.</summary>
    Task<CompanionDeviceListDto> GetDevicesAsync(Guid? profileId, CancellationToken ct = default);

    /// <summary>The companion device's current choice; mode "none" when it has not made one.</summary>
    Task<CompanionSessionDto> GetSessionAsync(Guid? profileId, string? companionDeviceId, CancellationToken ct = default);

    Task<CompanionSessionDto> FollowAsync(CompanionFollowRequestDto request, CancellationToken ct = default);

    Task<CompanionSessionDto> SetManualAsync(CompanionManualPositionRequestDto request, CancellationToken ct = default);

    Task ClearAsync(Guid? profileId, string? companionDeviceId, CancellationToken ct = default);
}
