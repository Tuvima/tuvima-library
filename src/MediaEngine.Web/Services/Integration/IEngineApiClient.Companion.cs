using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/companion/devices - the active profile's own devices that are playing or paused right now. Null when the Engine could not be reached.</summary>
    Task<CompanionDeviceListDto?> GetCompanionDevicesAsync(Guid? profileId = null, CancellationToken ct = default);

    /// <summary>GET /api/v1/companion/session - this companion's choice (following a device, a manual position, or none). Null when the Engine could not be reached.</summary>
    Task<CompanionSessionDto?> GetCompanionSessionAsync(string companionDeviceId, Guid? profileId = null, CancellationToken ct = default);

    /// <summary>PUT /api/v1/companion/session/follow - follow one of the profile's own devices.</summary>
    Task<CompanionSessionDto?> FollowCompanionDeviceAsync(CompanionFollowRequestDto request, CancellationToken ct = default);

    /// <summary>PUT /api/v1/companion/session/manual - hold a place chosen by hand (an episode, a chapter).</summary>
    Task<CompanionSessionDto?> SetCompanionManualPositionAsync(CompanionManualPositionRequestDto request, CancellationToken ct = default);

    /// <summary>DELETE /api/v1/companion/session - stop following and forget any manual place.</summary>
    Task<bool> ClearCompanionSessionAsync(string companionDeviceId, Guid? profileId = null, CancellationToken ct = default);

    /// <summary>GET /api/v1/companion/context - what the companion shows for one work at one position. Null when it cannot be read or the profile may not see the work.</summary>
    Task<CompanionContextDto?> GetCompanionContextAsync(Guid workId, int? chapterIndex = null, double? positionSeconds = null, Guid? profileId = null, CancellationToken ct = default);
}
