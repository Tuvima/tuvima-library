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
public sealed record PlaybackLadderInput(
    string Extension,
    PlaybackProfileDto Profile,
    MediaProbeResult? Probe,
    bool ForceStreamableCopy = false,
    bool ForceEncodePicture = false,
    string? EncodeReason = null);

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

    public static PlaybackDecision Decide(PlaybackLadderInput input)
    {
        var profile = input.Profile;
        var container = input.Extension.TrimStart('.').ToLowerInvariant();
        var video = PlaybackCapabilitiesService.NormalizeCodecName(input.Probe?.VideoCodec);
        var audio = PlaybackCapabilitiesService.NormalizeCodecName(input.Probe?.AudioCodec);
        var height = input.Probe?.Height;
        var player = profile.DisplayName;

        var copyAudio = profile.SupportedAudioCodecs
            .Select(PlaybackCapabilitiesService.NormalizeCodecName)
            .Where(codec => TransportAudio.Contains(codec, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var containerOk = profile.SupportedContainers.Contains(container, StringComparer.OrdinalIgnoreCase);
        var videoListed = video.Length == 0 || profile.SupportedVideoCodecs.Count == 0
            || profile.SupportedVideoCodecs.Contains(video, StringComparer.OrdinalIgnoreCase);
        var sizeOk = !(profile.MaxHeight.HasValue && height > profile.MaxHeight.Value);
        var pictureOk = videoListed && sizeOk;
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
        var tenBit = input.Probe?.PixelFormat?.Contains("10", StringComparison.Ordinal) == true;
        var copyablePicture = pictureOk
            && ((video == "h264" && !tenBit) || (video == "hevc" && profile.SupportedVideoCodecs.Contains("hevc", StringComparer.OrdinalIgnoreCase)));
        if (copyablePicture)
        {
            var audioKept = audio.Length == 0 || (audioOk && TransportAudio.Contains(audio, StringComparer.OrdinalIgnoreCase));
            if (audioKept)
            {
                var why = input.ForceStreamableCopy && containerOk
                    ? "An alternate audio track needs a streamable copy"
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
