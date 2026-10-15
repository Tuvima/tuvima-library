using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Maps a profile's saved playback settings (Settings &gt; Playback) to the read-only preferences a paired app may see.
/// Only the watching and subtitle fields cross; listening, reading, sync and sidebar settings stay on the Dashboard.
/// </summary>
public static class NativePlaybackPreferencesMapper
{
    public static NativePlaybackPreferencesDto From(UserPlaybackSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new NativePlaybackPreferencesDto
        {
            AudioLanguage = settings.Subtitles.AudioLanguage,
            SubtitleLanguage = settings.Subtitles.DefaultSubtitleLanguage,
            SubtitleMode = ToSubtitleMode(settings.Subtitles.ForcedSubtitlesMode),
            SubtitleSize = ToSubtitleSize(settings.Subtitles.SubtitleSize),
            SubtitleBackground = settings.Subtitles.SubtitleBackground,
            SkipBackSeconds = settings.Watching.SkipBackSeconds,
            SkipForwardSeconds = settings.Watching.SkipForwardSeconds,
            AutoplayNextEpisode = settings.Watching.AutoplayNextEpisode,
            ResumePlayback = settings.General.ResumePlayback,
            DefaultPlaybackSpeed = settings.Watching.DefaultPlaybackSpeed,
        };
    }

    /// <summary>The subtitle size as one of the fixed values Small, Medium, Large or ExtraLarge (any case); anything else is Medium.</summary>
    public static string ToSubtitleSize(string? stored)
    {
        foreach (var size in new[] { PlaybackPreferenceValues.Small, PlaybackPreferenceValues.Medium, PlaybackPreferenceValues.Large, PlaybackPreferenceValues.ExtraLarge })
        {
            if (string.Equals(stored?.Trim(), size, StringComparison.OrdinalIgnoreCase))
            {
                return size;
            }
        }

        return PlaybackPreferenceValues.Medium;
    }

    /// <summary>The "Show subtitles" setting is stored as Auto, Always or Never; Never is shown to the person (and the apps) as Off.</summary>
    public static string ToSubtitleMode(string? stored)
    {
        if (string.Equals(stored, PlaybackPreferenceValues.Always, StringComparison.OrdinalIgnoreCase))
        {
            return NativeSubtitleModes.Always;
        }

        return string.Equals(stored, PlaybackPreferenceValues.Never, StringComparison.OrdinalIgnoreCase)
            ? NativeSubtitleModes.Off
            : NativeSubtitleModes.Auto;
    }
}
