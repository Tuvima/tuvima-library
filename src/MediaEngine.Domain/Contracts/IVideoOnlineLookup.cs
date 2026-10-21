namespace MediaEngine.Domain.Contracts;

/// <summary>What an online lookup told us about one video. Every field is optional.</summary>
public sealed record VideoOnlineInfo(
    string? Title,
    string? Channel,
    string? Description,
    DateOnly? Published,
    byte[]? Thumbnail,
    string? ThumbnailMimeType);

/// <summary>
/// Looks a video up by its YouTube ID. Only the ID leaves the machine, never the file, its name or its path.
/// Returns null when the lookup is switched off, unavailable or finds nothing.
/// </summary>
public interface IVideoOnlineLookup
{
    Task<VideoOnlineInfo?> LookupAsync(string youtubeVideoId, CancellationToken ct = default);
}
