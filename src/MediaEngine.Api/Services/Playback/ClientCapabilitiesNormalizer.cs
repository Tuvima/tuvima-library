using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Cleans a capabilities report before the Engine keeps it: lists are lower-cased, de-duplicated and capped, sizes are clamped,
/// the learned limits are always the Engine's own (an app's value is ignored; playback-path spec D13), and so are the per-device video
/// options (they change through the video options action, not through a report; spec D15).
/// </summary>
public static class ClientCapabilitiesNormalizer
{
    public static ClientCapabilitiesDto Normalize(
        ClientCapabilitiesDto source,
        IReadOnlyList<LearnedPlaybackLimitDto>? learned = null,
        DeviceVideoOptionsDto? options = null) => new()
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
            VideoOptions = CleanOptions(options),
        };

    /// <summary>The same report with a different learned list; the video options stay.</summary>
    public static ClientCapabilitiesDto WithLearned(ClientCapabilitiesDto source, IReadOnlyList<LearnedPlaybackLimitDto> learned) =>
        Normalize(source, learned, source.VideoOptions);

    /// <summary>The same report with different per-device video options; the learned limits stay.</summary>
    public static ClientCapabilitiesDto WithOptions(ClientCapabilitiesDto source, DeviceVideoOptionsDto options) =>
        Normalize(source, source.LearnedLimits, options);

    /// <summary>Anything that is not a known choice becomes automatic, so a stored value can never name something the list does not offer.</summary>
    public static DeviceVideoOptionsDto CleanOptions(DeviceVideoOptionsDto? options) => new()
    {
        MaxResolution = PlaybackChoiceRules.ParseMaxResolution(options?.MaxResolution) is { } height
            ? height.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : VideoOptionValues.Auto,
        HardwareDecoding = Pick(options?.HardwareDecoding, VideoOptionValues.Auto, VideoOptionValues.On, VideoOptionValues.Off),
        SurroundSound = Pick(options?.SurroundSound, VideoOptionValues.Auto, VideoOptionValues.SurroundPassthrough, VideoOptionValues.SurroundStereo),
    };

    private static string Pick(string? value, params string[] allowed) =>
        allowed.FirstOrDefault(choice => string.Equals(choice, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? allowed[0];

    private static IReadOnlyList<string> NormalizeValues(IEnumerable<string> values, int maximum) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length <= 40)
            .Distinct(StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();

    private static int? Clamp(int? value, int minimum, int maximum) => value.HasValue ? Math.Clamp(value.Value, minimum, maximum) : null;
}
