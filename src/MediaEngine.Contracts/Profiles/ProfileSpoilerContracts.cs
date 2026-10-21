using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Profiles;

/// <summary>
/// How much of a story a profile wants characters to reveal. Stored with the profile's other preferences and read only by
/// the person themselves: <c>up_to_progress</c> ("Up to where I am", the default) or <c>everything</c>.
/// </summary>
public sealed class ProfileSpoilerPreferenceDto
{
    public const string UpToProgress = "up_to_progress";
    public const string Everything = "everything";

    [JsonPropertyName("profile_id")]
    public Guid ProfileId { get; init; }

    /// <summary><c>up_to_progress</c> or <c>everything</c>.</summary>
    [JsonPropertyName("spoiler_level")]
    public string SpoilerLevel { get; init; } = UpToProgress;
}

/// <summary>Chooses the signed-in profile's spoiler level.</summary>
public sealed class SaveProfileSpoilerPreferenceRequest
{
    /// <summary><c>up_to_progress</c> or <c>everything</c>.</summary>
    [JsonPropertyName("spoiler_level")]
    public string SpoilerLevel { get; init; } = ProfileSpoilerPreferenceDto.UpToProgress;
}
