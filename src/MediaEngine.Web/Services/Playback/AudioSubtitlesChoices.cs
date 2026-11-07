using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What an audio track is for. Commentary and audio description are listed after the ordinary tracks (unified player v2 D12).</summary>
public enum AudioTrackPurpose
{
    Main,
    AudioDescription,
    Commentary,
}

/// <summary>
/// One row of the Audio column. <see cref="Switchable"/> is false for a track the app cannot switch to (the row is listed with
/// <see cref="AudioSubtitlesChoices.NotSwitchableHelper"/> and cannot be chosen).
/// </summary>
public sealed record AudioChoice(int Index, string Label, string? Detail, AudioTrackPurpose Purpose, bool Selected, bool Switchable);

/// <summary>One row of the Subtitles column. A null <see cref="Key"/> is the Off row.</summary>
public sealed record SubtitleChoice(string? Key, string Label, bool Selected);

/// <summary>Which of the two independent choices a <see cref="TrackSwitchRequest"/> changes.</summary>
public enum TrackSwitchKind
{
    Audio,
    Subtitles,
}

/// <summary>
/// A choice made in Audio &amp; subtitles, with the playback position the player is at when it is made. Changing either track must keep the
/// picture where it is (acceptance A9: within one second), so the host receives the second to resume from with every request.
/// <see cref="CaptionKey"/> is the chosen subtitle track, or null for Off; <see cref="AudioIndex"/> is the chosen audio track.
/// </summary>
public sealed record TrackSwitchRequest(TrackSwitchKind Kind, double ResumeSeconds, int AudioIndex = 0, string? CaptionKey = null);

/// <summary>
/// Builds the rows of the Audio &amp; subtitles panel (unified player v2 D12, mockup screen 3) from the file's track data: the language name,
/// then the format and channels ("Dolby Atmos · 7.1"), commentary and audio description labelled and sorted last, subtitle labels kept
/// ("English · SDH", "English · Forced").
/// </summary>
public static class AudioSubtitlesChoices
{
    public const string NotSwitchableHelper = "Not switchable in this browser";

    /// <summary>The audio rows, ordinary tracks first, then audio description, then commentary (each group keeps the file's order).</summary>
    public static IReadOnlyList<AudioChoice> BuildAudio(IReadOnlyList<PlaybackTrackDto> tracks, int? selectedIndex, bool canSwitch)
    {
        if (tracks.Count == 0)
        {
            return [];
        }

        var current = selectedIndex is { } chosen && chosen >= 0 && chosen < tracks.Count
            ? chosen
            : Math.Max(0, tracks.ToList().FindIndex(track => track.IsDefault));
        return tracks
            .Select((track, index) =>
            {
                var purpose = PurposeOf(track);
                var isCurrent = index == current;
                return new AudioChoice(index, AudioLabel(track, index, purpose), AudioDetail(track, purpose), purpose,
                    isCurrent, Switchable: tracks.Count > 1 && canSwitch);
            })
            .OrderBy(choice => (int)choice.Purpose)
            .ThenBy(choice => choice.Index)
            .ToList();
    }

    public static AudioTrackPurpose PurposeOf(PlaybackTrackDto track)
    {
        var name = track.DisplayName ?? string.Empty;
        if (name.Contains("commentary", StringComparison.OrdinalIgnoreCase))
        {
            return AudioTrackPurpose.Commentary;
        }

        return name.Contains("audio description", StringComparison.OrdinalIgnoreCase)
            || name.Contains("descriptive", StringComparison.OrdinalIgnoreCase)
            || name.Contains("described", StringComparison.OrdinalIgnoreCase)
                ? AudioTrackPurpose.AudioDescription
                : AudioTrackPurpose.Main;
    }

    private static string AudioLabel(PlaybackTrackDto track, int index, AudioTrackPurpose purpose)
    {
        var language = DisplayFormat.FormatLanguageName(track.Language);
        return purpose switch
        {
            AudioTrackPurpose.Commentary => (track.DisplayName ?? string.Empty).Contains("director", StringComparison.OrdinalIgnoreCase)
                ? "Director commentary"
                : "Commentary",
            AudioTrackPurpose.AudioDescription => language is null ? "Audio description" : $"{language} · Audio description",
            _ => language ?? $"Audio {index + 1}",
        };
    }

    private static string? AudioDetail(PlaybackTrackDto track, AudioTrackPurpose purpose)
    {
        var language = purpose == AudioTrackPurpose.Commentary ? DisplayFormat.FormatLanguageName(track.Language) : null;
        var parts = new[] { language, Facts(track) }.Where(part => !string.IsNullOrWhiteSpace(part));
        var joined = string.Join(" · ", parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>"Dolby Atmos · 7.1", "Dolby Digital · 5.1", "Stereo · 2.0"; null when the file says nothing about the track.</summary>
    private static string? Facts(PlaybackTrackDto track)
    {
        var codec = CodecName(track);
        var channels = ChannelLayout(track.Channels);
        if (codec is null && channels is null)
        {
            var raw = track.Codec?.Trim();
            return string.IsNullOrWhiteSpace(raw) ? null : raw.ToUpperInvariant();
        }

        return string.Join(" · ", new[] { codec, channels }.Where(part => part is not null));
    }

    // Only the surround families get a name; for a plain stereo or mono track the channel layout alone says what it is.
    private static string? CodecName(PlaybackTrackDto track)
    {
        if ((track.DisplayName ?? string.Empty).Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            return "Dolby Atmos";
        }

        return track.Codec?.Trim().ToLowerInvariant() switch
        {
            "ac3" or "ac-3" => "Dolby Digital",
            "eac3" or "e-ac-3" or "ec3" => "Dolby Digital Plus",
            "truehd" or "mlp" => "Dolby TrueHD",
            "dts" => "DTS",
            "dts-hd" or "dtshd" or "dts-hd ma" or "dts_hd" => "DTS-HD",
            _ => null,
        };
    }

    private static string? ChannelLayout(int? channels) => channels switch
    {
        1 => "Mono · 1.0",
        2 => "Stereo · 2.0",
        6 => "5.1",
        8 => "7.1",
        > 2 => $"{channels} channels",
        _ => null,
    };

    /// <summary>
    /// A subtitle row's name with the kind kept: "English", "English · SDH", "English · Forced". An unknown language falls back to
    /// whatever the player named the track.
    /// </summary>
    public static string SubtitleLabel(string? language, string? fallback, bool isForced, bool isHearingImpaired)
    {
        var name = DisplayFormat.FormatLanguageName(language);
        if (name is null)
        {
            name = string.IsNullOrWhiteSpace(fallback) ? "Subtitles" : fallback.Trim();
        }

        return isHearingImpaired ? $"{name} · SDH" : isForced ? $"{name} · Forced" : name;
    }

    /// <summary>True when a track's own name says it is for the deaf and hard of hearing.</summary>
    public static bool NamesHearingImpaired(string? displayName) =>
        displayName is { Length: > 0 }
        && (displayName.Contains("SDH", StringComparison.OrdinalIgnoreCase) || displayName.Contains("hearing", StringComparison.OrdinalIgnoreCase));
}
