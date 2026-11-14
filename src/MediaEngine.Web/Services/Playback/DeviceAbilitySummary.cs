using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The one line the Devices page shows for what a device can play (playback-path spec D14), for example
/// "Plays H.264, HEVC and AV1 in hardware · HDR · up to 4K". Built only from what the device reported; nothing is guessed.
/// </summary>
public static class DeviceAbilitySummary
{
    /// <summary>The line for a device's report, or null when the device has not reported what it plays.</summary>
    public static string? Describe(ClientCapabilitiesDto? capabilities)
    {
        if (capabilities is null || capabilities.VideoCodecs.Count == 0)
        {
            return null;
        }

        var codecs = capabilities.VideoCodecs.Select(Display).Distinct(StringComparer.Ordinal).ToList();
        var hardware = capabilities.HardwareDecodeVideoCodecs
            .Where(codec => capabilities.VideoCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
            .Select(Display)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var parts = new List<string>();
        if (hardware.Count == 0)
        {
            parts.Add($"Plays {Join(codecs)}");
        }
        else if (hardware.Count == codecs.Count)
        {
            parts.Add($"Plays {Join(codecs)} in hardware");
        }
        else
        {
            parts.Add($"Plays {Join(codecs)}");
            parts.Add($"{Join(hardware)} in hardware");
        }

        if (capabilities.SupportsHdr)
        {
            parts.Add("HDR");
        }

        if (HeightLabel(capabilities.MaxHeight) is { } height)
        {
            parts.Add($"up to {height}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"4K" for 2160 and up, "1440p" and so on below; null when the device reported no limit.</summary>
    public static string? HeightLabel(int? maxHeight) => maxHeight switch
    {
        null or <= 0 => null,
        >= 2160 => "4K",
        var height => $"{height}p",
    };

    private static string Display(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg2video" => "MPEG-2",
        var other => other.ToUpperInvariant(),
    };

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };
}
