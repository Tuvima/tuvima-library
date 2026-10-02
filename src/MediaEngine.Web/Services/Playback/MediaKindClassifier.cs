using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

public static class MediaKindClassifier
{
    public static PlaybackExperience Classify(string? mediaType)
    {
        var value = (mediaType ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return PlaybackExperience.Music;
        }

        if (value.Contains("audiobook", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "M4B", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackExperience.Audiobook;
        }

        if (value.Contains("movie", StringComparison.OrdinalIgnoreCase)
            || value.Contains("video", StringComparison.OrdinalIgnoreCase)
            || value.Contains("tv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "MP4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "MKV", StringComparison.OrdinalIgnoreCase))
        {
            return PlaybackExperience.Video;
        }

        return PlaybackExperience.Music;
    }

    public static bool IsAudiobook(string? mediaType) => Classify(mediaType) == PlaybackExperience.Audiobook;

    public static bool IsMusic(string? mediaType) => Classify(mediaType) == PlaybackExperience.Music;

    public static bool IsVideo(string? mediaType) => Classify(mediaType) == PlaybackExperience.Video;

    public static bool TryClassifyKnown(string? mediaType, out PlaybackExperience experience)
    {
        var value = (mediaType ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            experience = default;
            return false;
        }

        if (IsAudiobook(value))
        {
            experience = PlaybackExperience.Audiobook;
            return true;
        }

        if (IsVideo(value))
        {
            experience = PlaybackExperience.Video;
            return true;
        }

        if (value.Contains("music", StringComparison.OrdinalIgnoreCase)
            || value.Contains("song", StringComparison.OrdinalIgnoreCase)
            || value.Contains("track", StringComparison.OrdinalIgnoreCase))
        {
            experience = PlaybackExperience.Music;
            return true;
        }

        experience = default;
        return false;
    }

    public static string ToPlayerExperienceString(PlaybackExperience experience) => experience switch
    {
        PlaybackExperience.Audiobook => PlayerExperienceModes.Audiobook,
        PlaybackExperience.Video => PlayerExperienceModes.Video,
        _ => PlayerExperienceModes.Music,
    };

    public static PlaybackExperience FromPlayerExperienceString(string? experience) => experience switch
    {
        var value when string.Equals(value, PlayerExperienceModes.Audiobook, StringComparison.OrdinalIgnoreCase) => PlaybackExperience.Audiobook,
        var value when string.Equals(value, PlayerExperienceModes.Video, StringComparison.OrdinalIgnoreCase) => PlaybackExperience.Video,
        _ => PlaybackExperience.Music,
    };
}
