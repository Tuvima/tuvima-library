using System.Text.Json;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Ingestion.Services;

/// <summary>Durable technical facts, captured at ingestion rather than during browsing or Play.</summary>
public sealed class PlaybackInspectionWriter(IFFmpegService ffmpeg, PlaybackStateRepository repository)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".wav", ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".wma", ".opus", ".mov" };

    public async Task InspectAsync(MediaAsset asset, CancellationToken ct)
    {
        if (!Extensions.Contains(Path.GetExtension(asset.FilePathRoot)))
        {
            return;
        }
        if (await repository.GetInspectionMetadataAsync(asset.Id, asset.ContentHash, ct) is not null)
        {
            return;
        }
        var probe = await ffmpeg.ProbeAsync(asset.FilePathRoot, ct)
            ?? throw new IOException("Media inspection did not return technical facts.");
        await repository.StoreInspectionAsync(asset.Id, asset.ContentHash, probe.FileSizeBytes,
            probe.Duration.TotalSeconds, Path.GetExtension(asset.FilePathRoot).TrimStart('.'),
            JsonSerializer.Serialize(probe), ct);
    }
}
