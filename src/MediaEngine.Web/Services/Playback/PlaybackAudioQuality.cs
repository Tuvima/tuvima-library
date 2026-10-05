using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

public static class PlaybackAudioQuality
{
    public static string? Classify(PlaybackManifestDto? manifest, string? browserStreamUrl)
    {
        if (manifest is null || !manifest.DirectPlaySupported || manifest.RecommendedDelivery != PlaybackDeliveryModes.DirectStream
            || string.IsNullOrWhiteSpace(browserStreamUrl) || browserStreamUrl.Contains("hls", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(manifest.ConversionReason)) return null;
        // The bounded Dashboard proxy carries the same asset as the direct source manifest.
        if (manifest.AssetId == Guid.Empty || string.IsNullOrWhiteSpace(manifest.DirectStreamUrl)
            || !manifest.DirectStreamUrl.Contains(manifest.AssetId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !browserStreamUrl.Split('?', '#')[0].EndsWith($"/engine-stream/{manifest.AssetId:D}", StringComparison.OrdinalIgnoreCase)) return null;
        var technical = manifest.Technical;
        var codec = technical.AudioCodec?.Trim().ToLowerInvariant();
        var lossless = codec is "flac" or "alac" or "wav" or "wave" or "pcm" or "aiff" or "aif" || codec?.StartsWith("pcm_", StringComparison.Ordinal) == true;
        if (!lossless || technical.SampleRateHz is not > 0 || technical.BitDepth is not > 0) return null;
        return technical.SampleRateHz > 48000 || technical.BitDepth > 16 ? "Hi-Res Lossless" : "Lossless";
    }
}
