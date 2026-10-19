using MediaEngine.Contracts.Activity;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/activity/recent - the signed-in person's latest activity for the Overview strip, music collapsed into sessions. Picture addresses are Dashboard addresses, ready for an image tag.</summary>
    Task<PersonalActivityRecentDto?> GetPersonalActivityRecentAsync(int limit = 20, CancellationToken ct = default);
}
