using System.Text.Json.Serialization;

namespace MediaEngine.Web.Models.ViewDTOs;

public sealed class PlaybackTechnicalSummaryViewModel
{
    [JsonPropertyName("video_resolution_label")]
    public string? VideoResolutionLabel { get; set; }

    [JsonPropertyName("video_codec")]
    public string? VideoCodec { get; set; }

    [JsonPropertyName("audio_language")]
    public string? AudioLanguage { get; set; }

    [JsonPropertyName("audio_codec")]
    public string? AudioCodec { get; set; }

    [JsonPropertyName("audio_channels")]
    public string? AudioChannels { get; set; }

    [JsonPropertyName("subtitle_summary")]
    public string? SubtitleSummary { get; set; }

    [JsonPropertyName("audio_languages")]
    public List<string> AudioLanguages { get; set; } = [];

    [JsonPropertyName("subtitle_languages")]
    public List<string> SubtitleLanguages { get; set; } = [];
}
