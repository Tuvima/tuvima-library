using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain.Services;
using MediaEngine.Processors;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Metadata;

/// <summary>
/// The "Frames from the video" source of the editor's Artwork tab (review mock-ups 2026-11-18, D2). A video, a video collection and an
/// episode can take their picture from a frame of the video itself: this lists the frames, serves one, saves a chosen one as a picture of
/// the item (source "generated", so the Artwork tab labels it "From video frame"), and gives a video that has no cover picture yet the
/// Engine's best frame as its default cover.
/// </summary>
public sealed class VideoFrameArtworkService(
    VideoFrameCandidateService frames,
    ArtworkAssetService assets,
    ILogger<VideoFrameArtworkService>? logger = null)
{
    public const string CoverSlot = "CoverArt";
    public const string CoverRole = "Primary";

    /// <summary>The slots a frame can be chosen for: the cover, the background and an episode's still. A logo is never a frame.</summary>
    public static bool AcceptsFrames(string? slot) =>
        slot is not null && slot.Trim().ToLowerInvariant() is "coverart" or "background" or "episodestill";

    /// <summary>Whether a scope with this media type gets the Engine's best frame as its default cover when it has no cover picture.</summary>
    public static bool GetsDefaultCover(string? mediaType) => VideoFolderWording.IsVideoItemMediaType(mediaType);

    /// <summary>The frames of the scope's video, and (for an Other videos scope with no cover picture yet) the default cover made from the best one.</summary>
    public async Task<VideoFramesDto> ListAsync(
        Guid entityId,
        string scopeId,
        string? videoPath,
        string ownerType,
        Guid ownerId,
        string title,
        string mediaType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return new VideoFramesDto([], "There is no video file to take frames from.");
        }

        var found = await frames.GetAsync(videoPath, ct).ConfigureAwait(false);
        if (found.Count == 0)
        {
            return new VideoFramesDto([], "No frames could be taken from this video.");
        }

        if (GetsDefaultCover(mediaType))
        {
            await EnsureDefaultCoverAsync(found, videoPath, ownerType, ownerId, title, mediaType, ct).ConfigureAwait(false);
        }

        return new VideoFramesDto(found.Select(frame => new VideoFrameDto(
            frame.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame.Seconds,
            $"/metadata/{entityId:D}/video-frames/{Uri.EscapeDataString(scopeId)}/{frame.Index}",
            frame.IsBest,
            VideoFrameSources.SourceKeyFor(frame.Seconds))).ToList());
    }

    /// <summary>The picture of one frame, or null when the video or the frame is not there.</summary>
    public async Task<byte[]?> ImageAsync(string? videoPath, int index, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return null;
        }

        return (await frames.GetOneAsync(videoPath, index, ct).ConfigureAwait(false))?.Bytes;
    }

    /// <summary>
    /// Saves one frame as an unchosen picture of the owner for a slot; the editor then chooses it through the edit session, like an upload.
    /// Null when the frame is not there.
    /// </summary>
    public async Task<ArtworkAssetDto?> ImportAsync(
        string? videoPath, int index, string slot, string ownerType, Guid ownerId, string title, string mediaType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !AcceptsFrames(slot))
        {
            return null;
        }

        var frame = await frames.GetOneAsync(videoPath, index, ct).ConfigureAwait(false);
        return frame is null
            ? null
            : await SaveAsync(frame, slot, ownerType, ownerId, title, mediaType, preferred: false, ct).ConfigureAwait(false);
    }

    // A video with no cover picture of its own gets the best frame as its cover (from the source "generated"). A picture beside the file
    // (cover.jpg, folder.jpg, ...) is the person's own, so it is left alone.
    private async Task EnsureDefaultCoverAsync(
        IReadOnlyList<VideoFrameCandidate> found, string videoPath, string ownerType, Guid ownerId, string title, string mediaType, CancellationToken ct)
    {
        try
        {
            var workspace = await assets.GetEntityAsync(ownerType, ownerId, ct).ConfigureAwait(false);
            if (workspace.Variants.Any(variant => string.Equals(variant.Role, CoverRole, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(variant.SourceAssetType)
                    || string.Equals(variant.SourceAssetType, CoverSlot, StringComparison.OrdinalIgnoreCase))))
            {
                return;
            }

            if (FolderCoverImageReader.TryRead(videoPath) is not null)
            {
                return;
            }

            var best = found.FirstOrDefault(frame => frame.IsBest);
            if (best is not null)
            {
                await SaveAsync(best, CoverSlot, ownerType, ownerId, title, mediaType, preferred: true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The strip still works without a default; the next read tries again.
            logger?.LogWarning(ex, "The default cover from a video frame could not be saved for {OwnerId}", ownerId);
        }
    }

    private Task<ArtworkAssetDto> SaveAsync(
        VideoFrameCandidate frame, string slot, string ownerType, Guid ownerId, string title, string mediaType, bool preferred, CancellationToken ct)
    {
        var role = ArtworkAssetService.RoleForSlot(slot) ?? CoverRole;
        var stream = new MemoryStream(frame.Bytes, writable: false);
        return SaveAndDisposeAsync(stream, role, slot, ownerType, ownerId, title, mediaType, preferred, frame.Seconds, ct);
    }

    private async Task<ArtworkAssetDto> SaveAndDisposeAsync(
        MemoryStream stream, string role, string slot, string ownerType, Guid ownerId, string title, string mediaType, bool preferred, double seconds,
        CancellationToken ct)
    {
        await using (stream.ConfigureAwait(false))
        {
            return await assets.UploadAsync(
                stream,
                ".jpg",
                ownerType,
                ownerId,
                new ArtworkLinkRequest(Guid.Empty, role, null, preferred, title, mediaType, null, slot),
                VideoFrameSources.GeneratedProvider,
                VideoFrameSources.SourceKeyFor(seconds),
                ct).ConfigureAwait(false);
        }
    }
}
