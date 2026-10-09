using System.Text.Json.Serialization;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Dashboard view model for a user profile.
/// Maps from the Engine's <c>GET /profiles</c> response.
/// </summary>
public sealed record ProfileViewModel(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string AvatarColor,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("navigation_config")] string? NavigationConfig = null,
    [property: JsonPropertyName("avatar_image_url")] string? AvatarImageUrl = null,
    [property: JsonPropertyName("has_pin")] bool HasPin = false)
{
    /// <summary>Restricted ("child") profiles are tagged on the picker and never reach admin areas.</summary>
    [JsonIgnore]
    public bool IsRestricted => string.Equals(Role, "RestrictedProfile", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when this is the seed "Owner" profile
    /// that cannot be deleted.
    /// </summary>
    [JsonIgnore]
    public bool IsSeed => Id == new Guid("00000000-0000-0000-0000-000000000001");
}
