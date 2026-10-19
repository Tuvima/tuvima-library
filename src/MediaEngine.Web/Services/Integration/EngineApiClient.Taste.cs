using MediaEngine.Contracts.Taste;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<TasteViewDto?> GetPersonalTasteAsync(string? scope = null, CancellationToken ct = default) =>
        GetAsync<TasteViewDto>(
            "GET /api/v1/taste",
            "/api/v1/taste",
            new Dictionary<string, string?> { ["scope"] = scope },
            ct: ct);

    public Task<bool> AddPersonalTasteItemAsync(string group, string value, CancellationToken ct = default) =>
        PostAsync(
            "POST /api/v1/taste/items",
            "/api/v1/taste/items",
            new AddTasteItemRequest(group, value),
            ct: ct);

    public Task<bool> RemovePersonalTasteItemAsync(string group, string value, CancellationToken ct = default) =>
        DeleteAsync(
            "DELETE /api/v1/taste/items",
            $"/api/v1/taste/items/{Uri.EscapeDataString(group)}/{Uri.EscapeDataString(value)}",
            ct: ct);
}
