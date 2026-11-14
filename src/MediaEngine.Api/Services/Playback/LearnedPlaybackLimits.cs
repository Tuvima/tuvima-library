using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// What the Engine learns when a player reports it could not play a title in a rung (playback-path spec D13): the kind of file
/// (video codec, container, 10-bit) and the rung that failed. The next start of the same kind of file begins one rung lower.
/// Pure rules only; the places that keep the list (a paired device's record, a browser session) call these.
/// </summary>
public static class LearnedPlaybackLimits
{
    /// <summary>Most limits kept per device; the oldest are dropped first.</summary>
    public const int MaximumPerDevice = 20;

    /// <summary>True when a player's report names a rung the ladder knows.</summary>
    public static bool IsKnownRung(string? rung) =>
        rung is PlaybackRungs.Direct or PlaybackRungs.Repackage or PlaybackRungs.ConvertSound or PlaybackRungs.EncodePicture;

    /// <summary>The kind of file in a stored inspection, or null when the file has no known video codec.</summary>
    public static (string VideoCodec, string Container, bool TenBit)? KindOf(string extension, MediaProbeResult? probe)
    {
        var video = PlaybackCapabilitiesService.NormalizeCodecName(probe?.VideoCodec);
        if (video.Length == 0)
        {
            return null;
        }

        var tenBit = probe?.PixelFormat?.Contains("10", StringComparison.Ordinal) == true;
        return (video, extension.TrimStart('.').ToLowerInvariant(), tenBit);
    }

    /// <summary>Remembers a failure: a second failure of the same kind keeps the heavier rung.</summary>
    public static IReadOnlyList<LearnedPlaybackLimitDto> Learn(
        IReadOnlyList<LearnedPlaybackLimitDto> existing,
        string failedRung,
        string videoCodec,
        string container,
        bool tenBit,
        DateTimeOffset now)
    {
        var kept = new List<LearnedPlaybackLimitDto>(existing);
        var index = kept.FindIndex(limit => Matches(limit, videoCodec, container, tenBit));
        if (index >= 0)
        {
            if (PlaybackLadder.Rank(kept[index].FailedRung) >= PlaybackLadder.Rank(failedRung))
            {
                return existing;
            }

            kept.RemoveAt(index);
        }

        kept.Add(new LearnedPlaybackLimitDto
        {
            VideoCodec = videoCodec,
            Container = container,
            TenBit = tenBit,
            FailedRung = failedRung,
            Summary = SummaryFor(failedRung, videoCodec, container, tenBit),
            LearnedAt = now,
        });
        return kept.OrderByDescending(limit => limit.LearnedAt).Take(MaximumPerDevice).ToArray();
    }

    /// <summary>The lightest rung the next start of this kind of file may use, or null when nothing was learned about it.</summary>
    public static string? MinimumRung(IReadOnlyList<LearnedPlaybackLimitDto> limits, string extension, MediaProbeResult? probe)
    {
        if (limits.Count == 0 || KindOf(extension, probe) is not { } kind)
        {
            return null;
        }

        var match = limits.FirstOrDefault(limit => Matches(limit, kind.VideoCodec, kind.Container, kind.TenBit));
        return match is null ? null : PlaybackLadder.NextRung(match.FailedRung);
    }

    public static string SummaryFor(string failedRung, string videoCodec, string container, bool tenBit)
    {
        var kind = $"{DisplayCodec(videoCodec)}{(tenBit ? " 10-bit" : string.Empty)}";
        return failedRung == PlaybackRungs.Direct
            ? $"cannot play {kind} {container.ToUpperInvariant()} as it is"
            : $"cannot play {kind} from a copied stream";
    }

    public static string DisplayCodec(string codec) => codec switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg2video" => "MPEG-2",
        _ => codec.ToUpperInvariant(),
    };

    private static bool Matches(LearnedPlaybackLimitDto limit, string videoCodec, string container, bool tenBit) =>
        string.Equals(limit.VideoCodec, videoCodec, StringComparison.OrdinalIgnoreCase)
        && string.Equals(limit.Container, container, StringComparison.OrdinalIgnoreCase)
        && limit.TenBit == tenBit;
}
