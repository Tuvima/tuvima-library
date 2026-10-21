using System.Globalization;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- Phone companion (/api/v1/companion) ------------------------------------

    public Task<CompanionDeviceListDto?> GetCompanionDevicesAsync(Guid? profileId = null, CancellationToken ct = default) =>
        GetAsync<CompanionDeviceListDto>(
            "GET /api/v1/companion/devices",
            "/api/v1/companion/devices",
            new Dictionary<string, string?> { ["profileId"] = profileId?.ToString("D") },
            ct: ct);

    public Task<CompanionSessionDto?> GetCompanionSessionAsync(string companionDeviceId, Guid? profileId = null, CancellationToken ct = default) =>
        GetAsync<CompanionSessionDto>(
            "GET /api/v1/companion/session",
            "/api/v1/companion/session",
            new Dictionary<string, string?>
            {
                ["profileId"] = profileId?.ToString("D"),
                ["companionDeviceId"] = companionDeviceId,
            },
            ct: ct);

    public Task<CompanionSessionDto?> FollowCompanionDeviceAsync(CompanionFollowRequestDto request, CancellationToken ct = default) =>
        PutAsync<CompanionFollowRequestDto, CompanionSessionDto>(
            "PUT /api/v1/companion/session/follow", "/api/v1/companion/session/follow", request, ct: ct);

    public Task<CompanionSessionDto?> SetCompanionManualPositionAsync(CompanionManualPositionRequestDto request, CancellationToken ct = default) =>
        PutAsync<CompanionManualPositionRequestDto, CompanionSessionDto>(
            "PUT /api/v1/companion/session/manual", "/api/v1/companion/session/manual", request, ct: ct);

    public Task<bool> ClearCompanionSessionAsync(string companionDeviceId, Guid? profileId = null, CancellationToken ct = default) =>
        DeleteAsync(
            "DELETE /api/v1/companion/session",
            BuildEndpointPath(
                "/api/v1/companion/session",
                new Dictionary<string, string?>
                {
                    ["profileId"] = profileId?.ToString("D"),
                    ["companionDeviceId"] = companionDeviceId,
                }),
            ct: ct);

    public Task<CompanionContextDto?> GetCompanionContextAsync(Guid workId, int? chapterIndex = null, double? positionSeconds = null, Guid? profileId = null, CancellationToken ct = default) =>
        GetAsync<CompanionContextDto>(
            "GET /api/v1/companion/context",
            "/api/v1/companion/context",
            new Dictionary<string, string?>
            {
                ["workId"] = workId.ToString("D"),
                ["chapterIndex"] = chapterIndex?.ToString(CultureInfo.InvariantCulture),
                ["positionSeconds"] = positionSeconds?.ToString("0.###", CultureInfo.InvariantCulture),
                ["profileId"] = profileId?.ToString("D"),
            },
            logAsWarning: false,
            ct: ct);
}
