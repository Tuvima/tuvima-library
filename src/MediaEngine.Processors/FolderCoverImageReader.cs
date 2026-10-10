using MediaEngine.Domain.Enums;

namespace MediaEngine.Processors;

/// <summary>A validated sibling cover image read from a media file's own folder.</summary>
/// <param name="Bytes">The raw image bytes (JPEG or PNG).</param>
/// <param name="MimeType"><c>image/jpeg</c> or <c>image/png</c>.</param>
/// <param name="SourcePath">The image file the bytes were read from (read-only; never modified).</param>
public sealed record FolderCoverImage(byte[] Bytes, string MimeType, string SourcePath);

/// <summary>
/// Finds the cover image that audiobook, book and music folders carry next to the media file
/// (<c>cover.jpg</c>, <c>folder.jpg</c>, …) for files whose tags contain no embedded picture.
///
/// The reader is strictly read-only: it never modifies, moves, or locks the source image
/// (opened with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>), and it only
/// looks in the media file's own directory, never in parents.
/// </summary>
public static class FolderCoverImageReader
{
    public const long MinimumImageBytes = 1024;
    public const long MaximumImageBytes = 25L * 1024 * 1024;

    /// <summary>Candidate file names in priority order.</summary>
    public static IReadOnlyList<string> CandidateFileNames { get; } =
    [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.png",
    ];

    /// <summary>
    /// Folder artwork applies to Books, Audiobooks and Music only. Comics are excluded because issue
    /// files often share one series folder, and Movies/TV use their own poster conventions.
    /// </summary>
    public static bool AppliesTo(MediaType mediaType) =>
        mediaType is MediaType.Books or MediaType.Audiobooks or MediaType.Music;

    /// <summary>
    /// Returns the first existing candidate image in the media file's own directory when it is a
    /// valid JPEG/PNG between 1 KB and 25 MB; otherwise <see langword="null"/>.
    /// Only the first existing candidate is considered, so the result is deterministic.
    /// </summary>
    public static FolderCoverImage? TryRead(string mediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(mediaFilePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                present.TryAdd(Path.GetFileName(file), file);
            }

            foreach (var name in CandidateFileNames)
            {
                if (present.TryGetValue(name, out var candidatePath))
                {
                    return ReadAndValidate(candidatePath);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: an unreadable folder or image simply means "no folder cover".
            return null;
        }
    }

    private static FolderCoverImage? ReadAndValidate(string imagePath)
    {
        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        var length = stream.Length;
        if (length < MinimumImageBytes || length > MaximumImageBytes)
        {
            return null;
        }

        var bytes = new byte[length];
        stream.ReadExactly(bytes);

        var mimeType = DetectMimeType(bytes);
        return mimeType is null ? null : new FolderCoverImage(bytes, mimeType, imagePath);
    }

    private static string? DetectMimeType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        return null;
    }
}
