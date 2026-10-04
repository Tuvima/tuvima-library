using MediaEngine.Contracts.Display;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<DisplayRecentPageDto?> GetDisplayRecentAsync(string type = "all", string? cursor = null, int limit = 18, CancellationToken ct = default)
    {
        var page = await GetAsync<DisplayRecentPageDto>("GET /api/v1/display/recent", "/api/v1/display/recent",
            new Dictionary<string, string?> { ["type"] = type, ["cursor"] = cursor, ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct: ct);
        return page is null ? null : page with { Items = page.Items.Select(item => item.Catalogue is { } card ? item with { Catalogue = NormalizeDisplayCard(card) } : item).ToList() };
    }
}
