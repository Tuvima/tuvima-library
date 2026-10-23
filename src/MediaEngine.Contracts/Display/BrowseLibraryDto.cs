using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Display;

/// <summary>
/// One library the signed-in profile may browse, for the browse tabs of <c>GET /api/v1/display/browse/libraries</c>. Only libraries the
/// profile is granted appear (an administrator sees every one); the Vault and View areas never do.
/// </summary>
public sealed record BrowseLibraryDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind_key")] string KindKey,
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("has_folders")] bool HasFolders);
