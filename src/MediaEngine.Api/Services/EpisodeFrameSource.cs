using MediaEngine.Domain.Contracts;
using MediaEngine.Processors;

namespace MediaEngine.Api.Services;

/// <summary>Takes an episode's frame from its first video file with the shared frame service (the one Other videos already use).</summary>
public sealed class EpisodeFrameSource(IMediaAssetRepository mediaAssets, VideoStillFrameService frames) : IEpisodeFrameSource
{
    public async Task<byte[]?> GetFrameAsync(Guid episodeWorkId, CancellationToken ct = default)
    {
        var asset = await mediaAssets.FindFirstByWorkIdAsync(episodeWorkId, ct).ConfigureAwait(false);
        if (asset is null || string.IsNullOrWhiteSpace(asset.FilePathRoot))
        {
            return null;
        }

        var frame = await frames.GetOrCreateAsync(asset.FilePathRoot, ct).ConfigureAwait(false);
        return frame?.Bytes;
    }
}
