using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class EncodeQueueService : BackgroundService
{
    private readonly PlaybackStateRepository _state;
    private readonly IMediaAssetRepository _assets;
    private readonly IFFmpegService _ffmpeg;
    private readonly IConfigurationLoader _config;
    private readonly ILogger<EncodeQueueService> _logger;

    public EncodeQueueService(
        PlaybackStateRepository state,
        IMediaAssetRepository assets,
        IFFmpegService ffmpeg,
        IConfigurationLoader config,
        ILogger<EncodeQueueService> logger)
    {
        _state = state;
        _assets = assets;
        _ffmpeg = ffmpeg;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessNextJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Encode queue monitor failed");
            }
        }
    }

    private async Task ProcessNextJobAsync(CancellationToken ct)
    {
        var job = await _state.LeaseNextEncodeJobAsync(ct);
        if (job is null)
        {
            return;
        }

        if (!_ffmpeg.IsAvailable)
        {
            await _state.FailEncodeJobAsync(job.Id, "FFmpeg is not available on this Engine.", ct);
            return;
        }

        var asset = await _assets.FindByIdAsync(job.AssetId, ct);
        if (asset is null || !File.Exists(asset.FilePathRoot))
        {
            await _state.FailEncodeJobAsync(job.Id, "Source asset is missing.", ct);
            return;
        }

        var outputPath = BuildOutputPath(job, asset.FilePathRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var args = BuildArguments(asset.FilePathRoot, outputPath, job.ProfileKey);
        _logger.LogInformation("Starting encode job {JobId} for asset {AssetId} with profile {ProfileKey}", job.Id, job.AssetId, job.ProfileKey);

        var result = await _ffmpeg.RunAsync(args, ct);
        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            await _state.FailEncodeJobAsync(job.Id, string.IsNullOrWhiteSpace(result.Error) ? "FFmpeg failed." : result.Error, ct);
            return;
        }

        var metadata = MetadataFor(job.ProfileKey, outputPath);
        await _state.CompleteEncodeJobAsync(
            job,
            outputPath,
            metadata.DisplayName,
            metadata.Container,
            metadata.VideoCodec,
            metadata.AudioCodec,
            metadata.Width,
            metadata.Height,
            metadata.BitrateKbps,
            // A prepared variant lives for variant_retention_days; the cleanup service removes it once this passes.
            DateTimeOffset.UtcNow.AddDays(Math.Max(1, _config.LoadTranscoding().VariantRetentionDays)),
            ct);

        _logger.LogInformation("Completed encode job {JobId} at {OutputPath}", job.Id, outputPath);
    }

    private string BuildOutputPath(LeasedEncodeJob job, string sourcePath)
    {
        var transcoding = _config.LoadTranscoding();
        var root = _config.LoadCore().LibraryRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        var profile = SanitizeSegment(job.ProfileKey);
        var extension = job.ProfileKey.Equals("audio-mobile", StringComparison.OrdinalIgnoreCase)
            ? ".m4a"
            : job.ProfileKey.Equals("tv-adaptive-hls", StringComparison.OrdinalIgnoreCase)
                ? ".m3u8"
                : ".mp4";

        var fileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}.{profile}{extension}";
        var variantRoot = string.IsNullOrWhiteSpace(transcoding.VariantCachePath)
            ? Path.Combine(root, ".data", "variants")
            : Path.IsPathRooted(transcoding.VariantCachePath)
                ? transcoding.VariantCachePath
                : Path.Combine(root, transcoding.VariantCachePath);

        return Path.Combine(variantRoot, job.AssetId.ToString("N"), profile, fileName);
    }

    private static string BuildArguments(string inputPath, string outputPath, string profileKey)
    {
        var input = Quote(inputPath);
        var output = Quote(outputPath);

        return profileKey.ToLowerInvariant() switch
        {
            "mobile-small" => $"-y -i {input} -map 0:v:0? -map 0:a:0? -c:v libx264 -preset veryfast -crf 29 -vf scale=-2:540 -c:a aac -b:a 128k -movflags +faststart {output}",
            "mobile-standard" => $"-y -i {input} -map 0:v:0? -map 0:a:0? -c:v libx264 -preset veryfast -crf 26 -vf scale=-2:720 -c:a aac -b:a 160k -movflags +faststart {output}",
            "audio-mobile" => $"-y -i {input} -vn -c:a aac -b:a 96k {output}",
            "tv-adaptive-hls" => $"-y -i {input} -map 0:v:0? -map 0:a:0? -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 192k -f hls -hls_time 6 -hls_playlist_type vod {output}",
            _ => $"-y -i {input} -map 0:v:0? -map 0:a:0? -c:v libx264 -preset veryfast -crf 26 -vf scale=-2:720 -c:a aac -b:a 160k -movflags +faststart {output}",
        };
    }

    private static EncodeOutputMetadata MetadataFor(string profileKey, string outputPath) => profileKey.ToLowerInvariant() switch
    {
        "mobile-small" => new("Mobile small", "mp4", "h264", "aac", null, 540, null),
        "mobile-standard" => new("Mobile standard", "mp4", "h264", "aac", null, 720, null),
        "audio-mobile" => new("Audio mobile", "m4a", null, "aac", null, null, 96),
        "tv-adaptive-hls" => new("TV adaptive HLS", "hls", "h264", "aac", null, null, null),
        _ => new(profileKey, Path.GetExtension(outputPath).TrimStart('.'), "h264", "aac", null, null, null),
    };

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        return new string(chars);
    }

    private sealed record EncodeOutputMetadata(
        string DisplayName,
        string Container,
        string? VideoCodec,
        string? AudioCodec,
        int? Width,
        int? Height,
        int? BitrateKbps);
}
