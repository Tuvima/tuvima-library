using MediaEngine.Contracts.Display;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<DisplayRecentPageDto?> GetDisplayRecentAsync(string type = "all", string? cursor = null, int limit = 18, CancellationToken ct = default) => Task.FromResult<DisplayRecentPageDto?>(null);
}
