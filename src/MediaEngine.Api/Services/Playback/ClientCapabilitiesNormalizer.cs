using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Cleans a capabilities report before the Engine keeps it: lists are lower-cased, de-duplicated and capped, sizes are clamped,
/// and the learned limits are always the Engine's own (an app's value is ignored; playback-path spec D13).
/// </summary>
public static class ClientCapabilitiesNormalizer
{
    public static ClientCapabilitiesDto Normalize(ClientCapabilitiesDto source, IReadOnlyList<LearnedPlaybackLimitDto>? learned = null) => new()
    {
        SchemaVersion = Math.Clamp(source.SchemaVersion, 1, 2),
        Containers = NormalizeValues(source.Containers, 20),
        VideoCodecs = NormalizeValues(source.VideoCodecs, 20),
        AudioCodecs = NormalizeValues(source.AudioCodecs, 20),
        SubtitleFormats = NormalizeValues(source.SubtitleFormats, 20),
        Protocols = NormalizeValues(source.Protocols, 10),
        MaxWidth = Clamp(source.MaxWidth, 320, 16384),
        MaxHeight = Clamp(source.MaxHeight, 240, 8640),
        MaxBitrateKbps = Clamp(source.MaxBitrateKbps, 64, 500_000),
        MaxAudioChannels = Clamp(source.MaxAudioChannels, 1, 32),
        SupportsHdr = source.SupportsHdr,
        SupportsPlaybackSpeed = source.SupportsPlaybackSpeed,
        SupportsOfflineDownloads = source.SupportsOfflineDownloads,
        HardwareDecodeVideoCodecs = NormalizeValues(source.HardwareDecodeVideoCodecs, 20),
        TenBitVideoCodecs = NormalizeValues(source.TenBitVideoCodecs, 20),
        LearnedLimits = learned ?? [],
    };

    /// <summary>The same report with a different learned list.</summary>
    public static ClientCapabilitiesDto WithLearned(ClientCapabilitiesDto source, IReadOnlyList<LearnedPlaybackLimitDto> learned) =>
        Normalize(source, learned);

    private static IReadOnlyList<string> NormalizeValues(IEnumerable<string> values, int maximum) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length <= 40)
            .Distinct(StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();

    private static int? Clamp(int? value, int minimum, int maximum) => value.HasValue ? Math.Clamp(value.Value, minimum, maximum) : null;
}
