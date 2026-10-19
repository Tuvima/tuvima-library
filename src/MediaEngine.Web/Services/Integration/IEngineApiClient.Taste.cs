using MediaEngine.Contracts.Taste;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/taste - the taste of the people a scope covers (<c>me</c>, <c>profile:{id}</c>, <c>household:{id}</c> or <c>everyone</c>; null means me), with their own removals and additions applied. Null when the Engine refuses the scope or cannot be reached.</summary>
    Task<TasteViewDto?> GetPersonalTasteAsync(string? scope = null, CancellationToken ct = default);

    /// <summary>POST /api/v1/taste/items - add a genre, mood or era to the signed-in person's own taste. False when the Engine refuses it.</summary>
    Task<bool> AddPersonalTasteItemAsync(string group, string value, CancellationToken ct = default);

    /// <summary>DELETE /api/v1/taste/items/{group}/{value} - hide a genre, mood or era from the signed-in person's own taste. False when the Engine refuses it.</summary>
    Task<bool> RemovePersonalTasteItemAsync(string group, string value, CancellationToken ct = default);
}
