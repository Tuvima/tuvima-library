namespace MediaEngine.Domain.Models;

/// <summary>Why a file is Unmatched (Library setup spec D18, D34).</summary>
public static class UnmatchedReasons
{
    /// <summary>A lookup ran and found nothing it could accept.</summary>
    public const string NoMatch = "no_match";

    /// <summary>A file in a YouTube library with no video ID in its name or its <c>.info.json</c>; it is never looked up.</summary>
    public const string NoYoutubeId = "no_youtube_id";
}

/// <summary>
/// A media file the Engine could not match. It stays exactly where it is, plays and browses with its file name and tags, and is looked up
/// again later. <see cref="KeptAsIs"/> stops the retries and takes it out of the Unmatched count and filter.
/// </summary>
public sealed record UnmatchedAsset(
    Guid AssetId,
    string LibraryId,
    string Reason,
    DateTimeOffset FirstUnmatchedAt,
    DateTimeOffset LastTriedAt,
    DateTimeOffset? NextRetryAt,
    bool KeptAsIs,
    DateTimeOffset? KeptAt);

/// <summary>One Unmatched file as a list shows it: the row plus where the file is and the title it plays under.</summary>
public sealed record UnmatchedAssetListItem(UnmatchedAsset Asset, string FilePath, string? Title);

/// <summary>One page of a library's Unmatched files, newest first, with the total that match the filter.</summary>
public sealed record UnmatchedAssetPage(IReadOnlyList<UnmatchedAssetListItem> Items, int Total);
