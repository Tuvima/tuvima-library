using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Playback;

/// <summary>The ids of the "Video quality and devices" options (playback-path spec D15). Every client reads these from the Engine's list.</summary>
public static class VideoOptionIds
{
    public const string VideoQuality = "video_quality";
    public const string MobileDataQuality = "mobile_data_quality";
    public const string ConvertOnlyWhenNeeded = "convert_only_when_needed";
    public const string AllowHdr = "allow_hdr";
    public const string MaxResolution = "max_resolution";
    public const string HardwareDecoding = "hardware_decoding";
    public const string SurroundSound = "surround_sound";
    public const string DeviceAbilities = "device_abilities";
    public const string CheckAgain = "check_again";
}

/// <summary>Where an option is kept: <c>profile</c> follows the person to every device, <c>device</c> stays on one screen.</summary>
public static class VideoOptionScopes
{
    public const string Profile = "profile";
    public const string Device = "device";
}

/// <summary>How a client draws an option: a <c>choice</c> row, an on/off <c>toggle</c>, a read-only <c>summary</c> line or an <c>action</c> button.</summary>
public static class VideoOptionKinds
{
    public const string Choice = "choice";
    public const string Toggle = "toggle";
    public const string Summary = "summary";
    public const string Action = "action";
}

/// <summary>One choice of a <see cref="VideoOptionDto"/>: the stored value and the words every client shows.</summary>
public sealed class VideoOptionChoiceDto
{
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
}

/// <summary>
/// One row of "Video quality and devices" (playback-path spec D15): the Engine owns the list, the wording and the defaults, and serves
/// each row with its saved value so the Dashboard, Android, Apple, Roku and TV web all draw the same rows.
/// </summary>
public sealed class VideoOptionDto
{
    /// <summary>One of <see cref="VideoOptionIds"/>.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;

    /// <summary>One of <see cref="VideoOptionKinds"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = VideoOptionKinds.Choice;

    /// <summary>One of <see cref="VideoOptionScopes"/>.</summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = VideoOptionScopes.Profile;

    /// <summary>The choices of a choice row, in the order to show them; empty for the other kinds. A toggle stores <c>on</c> or <c>off</c>.</summary>
    [JsonPropertyName("choices")] public IReadOnlyList<VideoOptionChoiceDto> Choices { get; init; } = [];

    /// <summary>The saved value (a choice value, or <c>on</c>/<c>off</c>); for a summary, the line to show; empty for an action.</summary>
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;

    /// <summary>The value used until someone chooses; everything defaults to automatic.</summary>
    [JsonPropertyName("default_value")] public string DefaultValue { get; init; } = string.Empty;

    /// <summary>A short plain sentence that explains the row, or null.</summary>
    [JsonPropertyName("help")] public string? Help { get; init; }
}

/// <summary>The full "Video quality and devices" list with this person's and this device's saved values (playback-path spec D15).</summary>
public sealed class VideoPlaybackOptionsDto
{
    [JsonPropertyName("title")] public string Title { get; init; } = "Video quality and devices";

    /// <summary>Every row, in the order to draw them; the same ids in the same order for every client.</summary>
    [JsonPropertyName("options")] public IReadOnlyList<VideoOptionDto> Options { get; init; } = [];

    /// <summary>What was learned for this device (for example "Learned: cannot play HEVC 10-bit"); empty when nothing was.</summary>
    [JsonPropertyName("learned")] public IReadOnlyList<string> Learned { get; init; } = [];
}

/// <summary>Saves one option's value. A device option applies to the calling paired device, or to the browser named by <see cref="Browser"/>.</summary>
public sealed class SetVideoOptionRequest
{
    /// <summary>One of <see cref="VideoOptionIds"/>.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;

    /// <summary>The browser session key for the Dashboard's browser (as in the browser report); paired devices leave it empty.</summary>
    [JsonPropertyName("browser")] public string? Browser { get; init; }
}
