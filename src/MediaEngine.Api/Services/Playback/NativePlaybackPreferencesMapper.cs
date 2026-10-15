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
            SubtitleSize = settings.Subtitles.SubtitleSize,
            SubtitleBackground = settings.Subtitles.SubtitleBackground,
            SkipBackSeconds = settings.Watching.SkipBackSeconds,
            SkipForwardSeconds = settings.Watching.SkipForwardSeconds,
            AutoplayNextEpisode = settings.Watching.AutoplayNextEpisode,
            ResumePlayback = settings.General.ResumePlayback,
            DefaultPlaybackSpeed = settings.Watching.DefaultPlaybackSpeed,
        };
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
