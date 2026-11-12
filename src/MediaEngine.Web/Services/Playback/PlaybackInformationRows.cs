using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Formatting;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Playback;

/// <summary>One line of the Playback information view: a label and what it says about the file that is playing.</summary>
public sealed record PlaybackInformationRow(string Key, string Label, string Value);

/// <summary>A headed group of lines in the Playback information view (File, Video, Audio, Subtitles, How it is playing).</summary>
public sealed record PlaybackInformationSection(string Key, string Heading, IReadOnlyList<PlaybackInformationRow> Rows);

/// <summary>
/// The subtitle track that is showing: its label (null when subtitles are Off) and where it came from, as the track list knows it. <paramref name="Provider"/>
/// is null for a track inside the file, "embedded" the same, a file beside the video ("sidecar", "local", "external"), the person's own file ("user", "upload",
/// "manual") or the name of a subtitle source such as "subdl".
/// </summary>
public sealed record PlaybackInformationSubtitle(string? Label, string? Provider = null);

/// <summary>What the manifest cannot say: the audio and subtitle tracks in use, and the two facts only the browser knows (buffer ahead and the browser itself).</summary>
public sealed record PlaybackInformationContext(
    int? AudioTrackIndex = null,
    PlaybackInformationSubtitle? Subtitle = null,
    int? BufferAheadSeconds = null,
    string? ClientName = null);

/// <summary>
/// Builds the Playback information view of Playback settings (video-player follow-ups D13, amending unified player v2 D14): sections of two-column lines
/// drawn from the playing file's playback manifest and the browser. A line with nothing to say is left out, and so is a section left with no lines.
/// The file is named without its folder.
/// </summary>
public static class PlaybackInformationRows
{
    public static IReadOnlyList<PlaybackInformationSection> Build(
        IStringLocalizer<SharedStrings> L, PlaybackManifestDto? manifest, bool video, PlaybackInformationContext? context = null)
    {
        if (manifest is null)
        {
            return [];
        }

        context ??= new();
        var technical = manifest.Technical;
        var sections = new List<PlaybackInformationSection>();

        void AddSection(string key, string heading, params PlaybackInformationRow?[] rows)
        {
            var kept = rows.OfType<PlaybackInformationRow>().ToList();
            if (kept.Count > 0)
            {
                sections.Add(new(key, heading, kept));
            }
        }

        PlaybackInformationRow? Row(string key, string label, string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : new(key, label, value.Trim());

        AddSection("file", L["PlaybackInformation_SectionFile"].Value,
            Row("name", L["PlaybackInformation_FileName"].Value, FileNameOnly(technical.FileName)),
            Row("format", L["PlaybackInformation_FileFormat"].Value, FormatAndSize(L, manifest)),
            Row("bitrate", L["PlaybackInformation_OverallBitrate"].Value,
                manifest.SourceBitrateKbps is > 0 and var bitrate ? Kilobits(L, bitrate) : null));

        if (video)
        {
            AddSection("video", L["PlaybackInformation_Video"].Value,
                Row("picture", L["PlaybackInformation_Picture"].Value, Picture(L, technical)),
                Row("frame-rate", L["PlaybackInformation_FrameRate"].Value,
                    technical.FrameRate is > 0 and var rate ? L["PlaybackInformation_FramesPerSecond", rate.ToString("0.###", CultureInfo.InvariantCulture)].Value : null),
                Row("color", L["PlaybackInformation_Color"].Value, technical.ColorFormat));
        }

        var audio = AudioInUse(manifest, context.AudioTrackIndex);
        AddSection("audio", L["PlaybackInformation_Audio"].Value,
            Row("track", L["PlaybackInformation_AudioTrack"].Value, AudioTrack(L, technical, audio)),
            Row("quality", L["PlaybackInformation_AudioQuality"].Value, AudioQuality(L, technical, audio)));

        if (video && context.Subtitle is { } subtitle)
        {
            AddSection("subtitles", L["PlaybackInformation_Subtitles"].Value,
                Row("showing", L["PlaybackInformation_SubtitlesShowing"].Value, SubtitleShowing(L, subtitle)));
        }

        var converting = manifest.RecommendedDelivery == PlaybackDeliveryModes.Hls;
        AddSection("playing", L["PlaybackInformation_SectionPlaying"].Value,
            Row("delivery", L["PlaybackInformation_Delivery"].Value, Delivery(L, manifest)),
            converting ? Row("why", L["PlaybackInformation_ConversionReason"].Value, manifest.ConversionReason) : null,
            Row("connection", L["PlaybackInformation_Connection"].Value, Connection(L, manifest)),
            Row("buffer", L["PlaybackInformation_Buffer"].Value,
                context.BufferAheadSeconds is { } ahead and >= 0 ? L["PlaybackInformation_BufferAhead", ahead.ToString("N0", CultureInfo.CurrentCulture)].Value : null),
            Row("playing-on", L["PlaybackInformation_PlayingOn"].Value, context.ClientName));

        return sections;
    }

    /// <summary>The list as plain text for Copy details: a line for each heading, then "Label: value" for each line, a blank line between sections.</summary>
    public static string ToPlainText(IReadOnlyList<PlaybackInformationSection> sections) =>
        string.Join("\n\n", sections.Select(section =>
            string.Join("\n", new[] { section.Heading }.Concat(section.Rows.Select(row => $"{row.Label}: {row.Value}")))));

    /// <summary>Only the name with its extension: the Engine sends no folder, and anything that looks like one is cut here too.</summary>
    internal static string? FileNameOnly(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var only = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(only) ? null : only;
    }

    private static string? FormatAndSize(IStringLocalizer<SharedStrings> L, PlaybackManifestDto manifest)
    {
        var container = manifest.Technical.Container;
        if (string.IsNullOrWhiteSpace(container))
        {
            container = manifest.SourceExtension;
        }

        var format = string.IsNullOrWhiteSpace(container) ? null : container.Trim().TrimStart('.').ToUpperInvariant();
        var size = manifest.Technical.FileSizeBytes is > 0 and var bytes ? Size(bytes) : null;
        return (format, size) switch
        {
            (null or "", null) => null,
            ({ } only, null) => only,
            (null or "", { } only) => only,
            ({ } name, { } both) => L["PlaybackInformation_Size", name, both].Value,
        };
    }

    /// <summary>Gigabytes with one decimal from one gigabyte up, otherwise whole megabytes.</summary>
    internal static string Size(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        const double gigabyte = megabyte * 1024d;
        return bytes >= gigabyte
            ? $"{(bytes / gigabyte).ToString("0.0", CultureInfo.InvariantCulture)} GB"
            : $"{Math.Max(1, Math.Round(bytes / megabyte)).ToString("0", CultureInfo.InvariantCulture)} MB";
    }

    private static string Kilobits(IStringLocalizer<SharedStrings> L, int kilobits) =>
        L["PlaybackInformation_KilobitsPerSecond", kilobits.ToString("N0", CultureInfo.CurrentCulture)].Value;

    private static string? Picture(IStringLocalizer<SharedStrings> L, PlaybackTechnicalInfoDto technical)
    {
        string? resolution = null;
        if (technical.QualityLabel is { } label)
        {
            resolution = technical.Width is > 0 && technical.Height is > 0
                ? L["PlaybackInformation_Resolution", label,
                    technical.Width.Value.ToString(CultureInfo.InvariantCulture), technical.Height.Value.ToString(CultureInfo.InvariantCulture)].Value
                : label;
        }

        var codec = VideoCodecName(technical.VideoCodec);
        if (codec is not null && !string.IsNullOrWhiteSpace(technical.VideoProfile))
        {
            codec = $"{codec} {technical.VideoProfile.Trim()}";
        }

        return (resolution, codec) switch
        {
            (null, null) => null,
            ({ } only, null) => only,
            (null, { } only) => only,
            ({ } first, { } second) => $"{first} · {second}",
        };
    }

    private static string? VideoCodecName(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return null;
        }

        return codec.Trim().ToLowerInvariant() switch
        {
            "hevc" or "h265" => "HEVC",
            "h264" or "avc" => "H.264",
            "mpeg2video" => "MPEG-2",
            "mpeg4" => "MPEG-4",
            "vc1" => "VC-1",
            var other => other.ToUpperInvariant(),
        };
    }

    private static string? AudioCodecName(string? codec) =>
        string.IsNullOrWhiteSpace(codec) ? null : codec.Trim().ToUpperInvariant();

    /// <summary>The audio track in use: the one the caller says, else the default track, else the first.</summary>
    private static PlaybackTrackDto? AudioInUse(PlaybackManifestDto manifest, int? selectedIndex) =>
        (selectedIndex is { } index ? manifest.AudioTracks.FirstOrDefault(track => track.Index == index) : null)
        ?? manifest.AudioTracks.FirstOrDefault(track => track.IsDefault)
        ?? manifest.AudioTracks.FirstOrDefault();

    private static string? AudioTrack(IStringLocalizer<SharedStrings> L, PlaybackTechnicalInfoDto technical, PlaybackTrackDto? track)
    {
        var channels = track?.Channels ?? technical.Channels;
        var parts = new[]
        {
            DisplayFormat.FormatLanguageName(track?.Language),
            AudioCodecName(track?.Codec ?? technical.AudioCodec),
            channels is > 0 ? Channels(L, channels.Value) : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Channels(IStringLocalizer<SharedStrings> L, int channels) => channels switch
    {
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        _ => L["PlaybackInformation_Channels", channels].Value,
    };

    private static string? AudioQuality(IStringLocalizer<SharedStrings> L, PlaybackTechnicalInfoDto technical, PlaybackTrackDto? track)
    {
        var parts = new List<string>();
        if (technical.SampleRateHz is > 0 and var hertz)
        {
            parts.Add(L["PlaybackInformation_Kilohertz", (hertz / 1000d).ToString("0.#", CultureInfo.InvariantCulture)].Value);
        }

        if (technical.BitDepth is > 0 and var depth)
        {
            parts.Add(L["PlaybackInformation_BitDepth", depth].Value);
        }

        var kbps = technical.AudioBitrateKbps is > 0 ? technical.AudioBitrateKbps : track?.BitrateKbps;
        if (kbps is > 0)
        {
            parts.Add(Kilobits(L, kbps.Value));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? SubtitleShowing(IStringLocalizer<SharedStrings> L, PlaybackInformationSubtitle subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle.Label))
        {
            return L["Common_Off"].Value;
        }

        var source = SubtitleSource(L, subtitle.Provider);
        return source is null ? subtitle.Label.Trim() : L["PlaybackInformation_SubtitleFrom", subtitle.Label.Trim(), source].Value;
    }

    /// <summary>Where a subtitle came from, in words. A track with no provider is inside the file; a named source is shown as its name.</summary>
    internal static string? SubtitleSource(IStringLocalizer<SharedStrings> L, string? provider)
    {
        var name = provider?.Trim();
        if (string.IsNullOrEmpty(name) || name.Equals("embedded", StringComparison.OrdinalIgnoreCase))
        {
            return L["PlaybackInformation_SubtitleSourceFile"].Value;
        }

        return name.ToLowerInvariant() switch
        {
            "sidecar" or "local" or "external" or "file" => L["PlaybackInformation_SubtitleSourceBeside"].Value,
            "user" or "upload" or "uploaded" or "manual" or "yours" => L["PlaybackInformation_SubtitleSourceYours"].Value,
            "subdl" => "SubDL",
            _ => char.ToUpperInvariant(name[0]) + name[1..],
        };
    }

    private static string Delivery(IStringLocalizer<SharedStrings> L, PlaybackManifestDto manifest) => manifest.RecommendedDelivery switch
    {
        PlaybackDeliveryModes.Hls => L["PlaybackInformation_DeliveryConverted"].Value,
        PlaybackDeliveryModes.OfflineVariant => L["PlaybackInformation_DeliveryDownloaded"].Value,
        _ => L["PlaybackInformation_DeliveryDirect"].Value,
    };

    private static string Connection(IStringLocalizer<SharedStrings> L, PlaybackManifestDto manifest)
    {
        var path = manifest.Connection.ConnectionPath == PlaybackConnectionPaths.Local
            ? L["PlaybackInformation_ConnectionLocal"].Value
            : L["PlaybackInformation_ConnectionRemote"].Value;
        return manifest.Connection.EstimatedBandwidthMbps is > 0 and var megabits
            ? L["PlaybackInformation_ConnectionWithSpeed", path,
                L["PlaybackInformation_MegabitsPerSecond", megabits.ToString("0.#", CultureInfo.InvariantCulture)].Value].Value
            : path;
    }
}
