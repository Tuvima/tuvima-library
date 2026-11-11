using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Playback;

/// <summary>One line of the Playback information view: a label and what it says about the file that is playing.</summary>
public sealed record PlaybackInformationRow(string Key, string Label, string Value);

/// <summary>
/// Builds the Playback information view of Playback settings (unified player v2 D14): the existing technical summary (video, audio, subtitles)
/// drawn from the file's playback manifest, then the way it reaches this screen (delivery, connection, bandwidth). A line with nothing to say is
/// left out rather than shown empty.
/// </summary>
public static class PlaybackInformationRows
{
    public static IReadOnlyList<PlaybackInformationRow> Build(IStringLocalizer<SharedStrings> L, PlaybackManifestDto? manifest, bool video)
    {
        if (manifest is null)
        {
            return [];
        }

        var summary = Summarize(L, manifest);
        var rows = new List<PlaybackInformationRow>();
        void Add(string key, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new(key, label, value.Trim()));
            }
        }

        if (video)
        {
            Add("video", L["PlaybackInformation_Video"].Value, PlaybackTechnicalSummaryDisplay.VideoChip(summary));
            if (manifest.Technical.FrameRate is > 0 and var rate)
            {
                Add("frame-rate", L["PlaybackInformation_FrameRate"].Value, L["PlaybackInformation_FramesPerSecond", rate.ToString("0.###", CultureInfo.InvariantCulture)].Value);
            }
        }

        Add("audio", L["PlaybackInformation_Audio"].Value, PlaybackTechnicalSummaryDisplay.AudioChip(summary));
        if (video)
        {
            Add("subtitles", L["PlaybackInformation_Subtitles"].Value, PlaybackTechnicalSummaryDisplay.SubtitleChip(summary));
        }
        else
        {
            Add("quality", L["PlaybackInformation_Quality"].Value, AudioQuality(L, manifest.Technical));
        }

        Add("container", L["PlaybackInformation_Container"].Value, manifest.Technical.Container);
        Add("delivery", L["PlaybackInformation_Delivery"].Value, Delivery(L, manifest));
        Add("conversion", L["PlaybackInformation_ConversionReason"].Value, manifest.ConversionReason);
        Add("connection", L["PlaybackInformation_Connection"].Value, Connection(L, manifest.Connection.ConnectionPath));
        if (manifest.Connection.EstimatedBandwidthMbps is > 0 and var bandwidth)
        {
            Add("bandwidth", L["PlaybackInformation_Bandwidth"].Value, L["PlaybackInformation_MegabitsPerSecond", bandwidth.ToString("0.#", CultureInfo.InvariantCulture)].Value);
        }

        if (manifest.SourceBitrateKbps is > 0 and var bitrate)
        {
            Add("bitrate", L["PlaybackInformation_FileBitrate"].Value, L["PlaybackInformation_KilobitsPerSecond", bitrate.ToString(CultureInfo.InvariantCulture)].Value);
        }

        return rows;
    }

    /// <summary>The file's technical facts in the shape the details pages already use, so the player says the same thing they do.</summary>
    public static PlaybackTechnicalSummaryViewModel Summarize(IStringLocalizer<SharedStrings> L, PlaybackManifestDto manifest)
    {
        var technical = manifest.Technical;
        var audio = manifest.AudioTracks.FirstOrDefault(track => track.IsDefault) ?? manifest.AudioTracks.FirstOrDefault();
        var subtitleLanguages = manifest.SubtitleTracks
            .Select(track => track.Language ?? track.DisplayName)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var channels = technical.Channels ?? audio?.Channels;
        return new PlaybackTechnicalSummaryViewModel
        {
            VideoResolutionLabel = technical.QualityLabel,
            VideoCodec = technical.VideoCodec,
            AudioLanguage = audio?.Language,
            AudioCodec = technical.AudioCodec ?? audio?.Codec,
            AudioChannels = channels is > 0 ? Channels(L, channels.Value) : null,
            AudioLanguages = manifest.AudioTracks.Select(track => track.Language).Where(language => !string.IsNullOrWhiteSpace(language)).Select(language => language!).ToList(),
            SubtitleLanguages = subtitleLanguages,
            SubtitleSummary = subtitleLanguages.Count == 0 ? null : string.Join(", ", subtitleLanguages),
        };
    }

    private static string Channels(IStringLocalizer<SharedStrings> L, int channels) => channels switch
    {
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        _ => L["PlaybackInformation_Channels", channels].Value,
    };

    private static string? AudioQuality(IStringLocalizer<SharedStrings> L, PlaybackTechnicalInfoDto technical)
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

        if (technical.AudioBitrateKbps is > 0 and var kbps)
        {
            parts.Add(L["PlaybackInformation_KilobitsPerSecond", kbps.ToString(CultureInfo.InvariantCulture)].Value);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Delivery(IStringLocalizer<SharedStrings> L, PlaybackManifestDto manifest) => manifest.RecommendedDelivery switch
    {
        PlaybackDeliveryModes.Hls => L["PlaybackInformation_DeliveryConverted"].Value,
        PlaybackDeliveryModes.OfflineVariant => L["PlaybackInformation_DeliveryDownloaded"].Value,
        _ => L["PlaybackInformation_DeliveryDirect"].Value,
    };

    private static string Connection(IStringLocalizer<SharedStrings> L, string path) =>
        path == PlaybackConnectionPaths.Local ? L["PlaybackInformation_ConnectionLocal"].Value : L["PlaybackInformation_ConnectionRemote"].Value;
}
