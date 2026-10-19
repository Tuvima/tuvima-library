using MediaEngine.Contracts.Activity;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<PersonalActivityRecentDto?> GetPersonalActivityRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        var recent = await GetAsync<PersonalActivityRecentDto>(
            "GET /api/v1/activity/recent",
            "/api/v1/activity/recent",
            new Dictionary<string, string?> { ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ct: ct);
        return recent is null
            ? null
            : new PersonalActivityRecentDto([.. recent.Tiles.Select(PersonalActivityArtworkPath.ForBrowser)]);
    }
}
