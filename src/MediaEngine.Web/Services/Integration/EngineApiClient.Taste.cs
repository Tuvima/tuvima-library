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
            $"/api/v1/taste/items/{Uri.EscapeDataString(group)}/{EscapeCatchAllValue(value)}",
            ct: ct);

    /// <summary>
    /// The Engine route takes the value as a catch-all, so a value such as "rhythm and blues/soul" travels as separate path segments;
    /// each segment is escaped on its own so a "/" in the name is never sent as an encoded slash the Engine would read literally.
    /// </summary>
    internal static string EscapeCatchAllValue(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
}
