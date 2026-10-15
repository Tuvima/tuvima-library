using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Playback;

/// <summary>
/// What a paired app may read, read-only, of the watching and subtitle preferences of the profile its device is using now.
/// Built from the profile's saved playback settings (Settings &gt; Playback); the app never writes them. Field names are
/// frozen: the native apps are built against this exact list.
/// </summary>
public sealed class NativePlaybackPreferencesDto
{
    /// <summary>The audio track to start with, as a language name such as <c>English</c> (not a code).</summary>
    [JsonPropertyName("audioLanguage")] public string AudioLanguage { get; init; } = "English";

    /// <summary>The subtitle language to pick when subtitles show, as a language name such as <c>English</c> (not a code).</summary>
    [JsonPropertyName("subtitleLanguage")] public string SubtitleLanguage { get; init; } = "English";

    /// <summary>
    /// When subtitles show: <see cref="NativeSubtitleModes.Auto"/>, <see cref="NativeSubtitleModes.Always"/> or
    /// <see cref="NativeSubtitleModes.Off"/>. This is the profile's "Show subtitles" setting (stored as Auto, Always or Never).
    /// </summary>
    [JsonPropertyName("subtitleMode")] public string SubtitleMode { get; init; } = NativeSubtitleModes.Auto;

    /// <summary>The profile's subtitle size: <c>Small</c>, <c>Medium</c>, <c>Large</c> or <c>ExtraLarge</c>.</summary>
    [JsonPropertyName("subtitleSize")] public string SubtitleSize { get; init; } = PlaybackPreferenceValues.Medium;

    /// <summary>True when subtitles sit on a dark backing.</summary>
    [JsonPropertyName("subtitleBackground")] public bool SubtitleBackground { get; init; } = true;

    /// <summary>Seconds the skip-back control jumps when watching.</summary>
    [JsonPropertyName("skipBackSeconds")] public int SkipBackSeconds { get; init; } = 10;

    /// <summary>Seconds the skip-forward control jumps when watching.</summary>
    [JsonPropertyName("skipForwardSeconds")] public int SkipForwardSeconds { get; init; } = 30;

    /// <summary>True when the next episode starts on its own.</summary>
    [JsonPropertyName("autoplayNextEpisode")] public bool AutoplayNextEpisode { get; init; } = true;

    /// <summary>True when playback resumes where the person left off.</summary>
    [JsonPropertyName("resumePlayback")] public bool ResumePlayback { get; init; } = true;

    /// <summary>The speed video starts at; 1.0 is normal speed.</summary>
    [JsonPropertyName("defaultPlaybackSpeed")] public decimal DefaultPlaybackSpeed { get; init; } = 1.0m;
}

/// <summary>Values of <see cref="NativePlaybackPreferencesDto.SubtitleMode"/>.</summary>
public static class NativeSubtitleModes
{
    /// <summary>Show subtitles only when the audio is not in the subtitle language.</summary>
    public const string Auto = "Auto";

    /// <summary>Always show subtitles.</summary>
    public const string Always = "Always";

    /// <summary>Never show subtitles.</summary>
    public const string Off = "Off";
}
