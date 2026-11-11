using MediaEngine.Contracts.Reading;

namespace MediaEngine.Api.Services.Reading;

/// <summary>Why a comic page could not be given (PDF and comic reader spec section 4, error rows).</summary>
public enum ComicPageFailure
{
    /// <summary>The file is damaged, empty of pictures or not an archive.</summary>
    Unreadable,
    /// <summary>The archive is over a limit (too many entries, a page too big, too much to unpack).</summary>
    Refused,
    /// <summary>There is no such page.</summary>
    NoSuchPage,
    /// <summary>The width asked for is not one of the allowed widths.</summary>
    BadWidth,
}

public sealed class ComicPageException(ComicPageFailure failure, string message) : Exception(message)
{
    public ComicPageFailure Failure { get; } = failure;
}

/// <summary>One page picture ready to send.</summary>
public sealed record ComicPageImage(byte[] Bytes, string ContentType);

/// <summary>Lists a comic's pages and makes page pictures for all three archive kinds (CBZ, CBR, CB7).</summary>
public interface IComicPageService
{
    Task<ComicPageListDto> GetPageListAsync(string filePath, CancellationToken ct);

    /// <param name="width">0 for the original, otherwise one of the configured widths.</param>
    Task<ComicPageImage> GetPageAsync(string filePath, int page, int width, CancellationToken ct);
}
