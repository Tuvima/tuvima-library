using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Playback;

/// <summary>What the ladder needs to decide one start. Everything comes from stored facts and what the player reported.</summary>
/// <param name="Extension">The file's extension, with or without the dot.</param>
/// <param name="Profile">What this player can play (measured or fixed).</param>
/// <param name="Probe">The stored technical facts of the file; null when ingestion has not inspected it yet.</param>
/// <param name="ForceStreamableCopy">True when the player needs a prepared stream even for a file it could play as it is (an alternate audio track it cannot switch itself).</param>
/// <param name="ForceEncodePicture">True when the connection needs a smaller picture (remote quality cap or bandwidth budget).</param>
/// <param name="EncodeReason">The plain reason that goes with <paramref name="ForceEncodePicture"/>.</param>
/// <param name="MinimumRung">The lightest rung this start may use, when the device has failed a lighter one for this kind of file (playback-path spec D13); null for no limit.</param>
/// <param name="TenBitVideoCodecs">Video codecs the player reported it plays at 10 bits per color; null when the player did not measure it (a fixed list), so nothing is assumed.</param>
/// <param name="StereoOnly">True when the device chose "Stereo only" (playback-path spec D15): surround sound is never copied, so the sound is converted.</param>
/// <param name="StreamableCopyReason">The plain reason that goes with <paramref name="ForceStreamableCopy"/>; null uses the alternate-audio-track wording.</param>
public sealed record PlaybackLadderInput(
    string Extension,
    PlaybackProfileDto Profile,
    MediaProbeResult? Probe,
    bool ForceStreamableCopy = false,
    bool ForceEncodePicture = false,
    string? EncodeReason = null,
    string? MinimumRung = null,
    IReadOnlyList<string>? TenBitVideoCodecs = null,
    bool StereoOnly = false,
    string? StreamableCopyReason = null);

/// <summary>The ladder's answer for one start (playback-path spec D1 to D3).</summary>
/// <param name="Rung">One of <see cref="PlaybackRungs"/>.</param>
/// <param name="Reason">The plain reason, without the trailing copy/convert summary.</param>
/// <param name="CopyVideo">True when the picture is copied, never re-encoded.</param>
/// <param name="CopyAudioCodecs">Audio codecs the prepared stream keeps as they are; any other audio track is converted to AAC.</param>
public sealed record PlaybackDecision(
    string Rung,
    string Reason,
    bool CopyVideo,
    IReadOnlyList<string> CopyAudioCodecs)
{
    /// <summary>What happens to the picture and sound, for the log line and the reason text.</summary>
    public string Summary => Rung switch
    {
        PlaybackRungs.Direct => "played as it is",
        PlaybackRungs.Repackage => "video copied, audio copied",
        PlaybackRungs.ConvertSound => "video copied, audio converted",
        _ => "video encoded, audio converted",
    };

    /// <summary>The full reason shown in the log, the manifest and Playing now.</summary>
    public string FullReason => Rung == PlaybackRungs.Direct ? Reason : $"{Reason}; {Summary}";
}

/// <summary>
/// The decision ladder. The Engine alone decides how a title starts, and the first rung that works wins:
/// direct play, repackage (copy), convert the sound only, encode the picture. A rung is never skipped to "play it safe".
/// </summary>
public static class PlaybackLadder
{
    /// <summary>Audio the HLS segments (MPEG-TS) can carry without conversion.</summary>
    private static readonly string[] TransportAudio = ["aac", "mp3", "ac3", "eac3"];

    /// <summary>Lightest first: direct play, repackage, convert the sound, encode the picture.</summary>
    public static int Rank(string? rung) => rung switch
    {
        PlaybackRungs.Direct => 0,
        PlaybackRungs.Repackage => 1,
        PlaybackRungs.ConvertSound => 2,
        PlaybackRungs.EncodePicture => 3,
        _ => 0,
    };

    /// <summary>The rung after <paramref name="failedRung"/>; the heaviest rung stays itself.</summary>
    public static string NextRung(string failedRung) => Rank(failedRung) switch
    {
        0 => PlaybackRungs.Repackage,
        1 => PlaybackRungs.ConvertSound,
        _ => PlaybackRungs.EncodePicture,
    };

    public static PlaybackDecision Decide(PlaybackLadderInput input) => ApplyStereoOnly(input, DecideCore(input));

    /// <summary>
    /// "Stereo only" never copies surround: a start that would have kept multi-channel sound as it is converts the sound instead (or
    /// encodes the picture when it could not be copied either). Stereo sound is left alone.
    /// </summary>
    private static PlaybackDecision ApplyStereoOnly(PlaybackLadderInput input, PlaybackDecision decision)
    {
        if (!input.StereoOnly || input.Probe is null || input.Probe.Channels is <= 2)
        {
            return decision;
        }

        const string why = "Stereo only is on for this device";
        if (decision.Rung is PlaybackRungs.ConvertSound or PlaybackRungs.EncodePicture)
        {
            // The sound is already converted here, or the picture is encoded; no sound is copied either way.
            return decision with { CopyAudioCodecs = [] };
        }

        var video = PlaybackCapabilitiesService.NormalizeCodecName(input.Probe.VideoCodec);
        var tenBit = input.Probe.PixelFormat?.Contains("10", StringComparison.Ordinal) == true;
        return CanCopyPicture(video, tenBit, input.Profile, input.TenBitVideoCodecs)
            ? new PlaybackDecision(PlaybackRungs.ConvertSound, why, CopyVideo: true, [])
            : new PlaybackDecision(PlaybackRungs.EncodePicture, why, CopyVideo: false, []);
    }

    private static PlaybackDecision DecideCore(PlaybackLadderInput input)
    {
        var decision = DecideFirstRung(input);
        if (input.MinimumRung is null || Rank(decision.Rung) >= Rank(input.MinimumRung))
        {
            return decision;
        }

        // The device failed a lighter rung for this kind of file, so start at the next one it can still use.
        var profile = input.Profile;
        var player = profile.DisplayName;
        var video = PlaybackCapabilitiesService.NormalizeCodecName(input.Probe?.VideoCodec);
        var tenBit = input.Probe?.PixelFormat?.Contains("10", StringComparison.Ordinal) == true;
        var copyAudio = CopyAudioFor(profile);
        var learned = $"Last time this kind of file would not play {(decision.Rung == PlaybackRungs.Direct ? "as it is" : "from a copied stream")} on {player}";
        var copyable = decision.Rung != PlaybackRungs.EncodePicture && CanCopyPicture(video, tenBit, profile, input.TenBitVideoCodecs);
        return copyable && input.MinimumRung != PlaybackRungs.EncodePicture
            ? new PlaybackDecision(input.MinimumRung, learned, CopyVideo: true, copyAudio)
            : new PlaybackDecision(PlaybackRungs.EncodePicture, learned, CopyVideo: false, copyAudio);
    }

    private static string[] CopyAudioFor(PlaybackProfileDto profile) => profile.SupportedAudioCodecs
        .Select(PlaybackCapabilitiesService.NormalizeCodecName)
        .Where(codec => TransportAudio.Contains(codec, StringComparer.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// A picture can be copied into a stream when a streamable copy can carry the codec and the player plays it. 10-bit H.264 is not played
    /// by browsers or most TVs unless the player measured that it does.
    /// </summary>
    private static bool CanCopyPicture(string video, bool tenBit, PlaybackProfileDto profile, IReadOnlyList<string>? tenBitCodecs)
    {
        if (video is not ("h264" or "hevc"))
        {
            return false;
        }

        if (video == "hevc" && !profile.SupportedVideoCodecs.Contains("hevc", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!tenBit)
        {
            return true;
        }

        return tenBitCodecs is not null
            ? tenBitCodecs.Contains(video, StringComparer.OrdinalIgnoreCase)
            : video == "hevc";
    }

    private static PlaybackDecision DecideFirstRung(PlaybackLadderInput input)
    {
        var profile = input.Profile;
        var container = input.Extension.TrimStart('.').ToLowerInvariant();
        var video = PlaybackCapabilitiesService.NormalizeCodecName(input.Probe?.VideoCodec);
        var audio = PlaybackCapabilitiesService.NormalizeCodecName(input.Probe?.AudioCodec);
        var height = input.Probe?.Height;
        var player = profile.DisplayName;

        var copyAudio = CopyAudioFor(profile);
        var tenBit = input.Probe?.PixelFormat?.Contains("10", StringComparison.Ordinal) == true;

        var containerOk = profile.SupportedContainers.Contains(container, StringComparer.OrdinalIgnoreCase);
        var videoListed = video.Length == 0 || profile.SupportedVideoCodecs.Count == 0
            || profile.SupportedVideoCodecs.Contains(video, StringComparer.OrdinalIgnoreCase);
        var sizeOk = !(profile.MaxHeight.HasValue && height > profile.MaxHeight.Value);

        // A player that measured itself says which codecs it plays at 10 bits; a codec it did not list there is not played as it is.
        var tenBitOk = !tenBit || input.TenBitVideoCodecs is null || video.Length == 0
            || input.TenBitVideoCodecs.Contains(video, StringComparer.OrdinalIgnoreCase);
        var pictureOk = videoListed && sizeOk && tenBitOk;
        var audioOk = audio.Length == 0 || profile.SupportedAudioCodecs.Count == 0
            || profile.SupportedAudioCodecs.Contains(audio, StringComparer.OrdinalIgnoreCase);

        if (input.ForceEncodePicture)
        {
            return new PlaybackDecision(
                PlaybackRungs.EncodePicture,
                input.EncodeReason ?? "The connection needs a smaller picture",
                CopyVideo: false,
                copyAudio);
        }

        if (containerOk && pictureOk && audioOk && !input.ForceStreamableCopy)
        {
            return new PlaybackDecision(PlaybackRungs.Direct, "The file already plays in this player", CopyVideo: false, []);
        }

        // The picture can be copied into a stream when this player accepts the codec and size, and the codec is one a
        // streamable copy can carry. 10-bit H.264 is not played by browsers or most TVs, so it is encoded instead.
        var copyablePicture = pictureOk && CanCopyPicture(video, tenBit, profile, input.TenBitVideoCodecs);
        if (copyablePicture)
        {
            var audioKept = audio.Length == 0 || (audioOk && TransportAudio.Contains(audio, StringComparer.OrdinalIgnoreCase));
            if (audioKept)
            {
                var why = input.ForceStreamableCopy && containerOk
                    ? input.StreamableCopyReason ?? "An alternate audio track needs a streamable copy"
                    : $"{container.ToUpperInvariant()} is not a {player} container";
                return new PlaybackDecision(PlaybackRungs.Repackage, why, CopyVideo: true, copyAudio);
            }

            return new PlaybackDecision(
                PlaybackRungs.ConvertSound,
                $"{audio.ToUpperInvariant()} sound cannot be played in {player}",
                CopyVideo: true,
                copyAudio);
        }

        var cause = !videoListed
            ? $"{player} does not play {(video.Length == 0 ? "this" : video.ToUpperInvariant())} video"
            : !sizeOk
                ? $"{player} is limited to {profile.MaxHeight}p"
                : tenBit
                    ? $"{player} cannot play 10-bit {video.ToUpperInvariant()} video"
                    : $"{video.ToUpperInvariant()} video cannot be copied into a stream";
        return new PlaybackDecision(PlaybackRungs.EncodePicture, cause, CopyVideo: false, copyAudio);
    }
}
