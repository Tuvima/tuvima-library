using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>One chapter of a book or audiobook as the companion lists it.</summary>
public sealed record CompanionChapter(int Index, string Title, double? StartSeconds = null);

/// <summary>Where the companion gets chapter names from, so the context read does not depend on files or players.</summary>
public interface ICompanionChapterSource
{
    /// <summary>The chapters of the Work's file in reading or listening order; empty when none are known.</summary>
    Task<IReadOnlyList<CompanionChapter>> GetChaptersAsync(DisplayWorkRow row, Guid? profileId, CancellationToken ct = default);
}

/// <summary>
/// Audiobook chapters come from the same playback manifest the player uses; book chapters are the top level of the EPUB's
/// table of contents. Video chapters are not listed (a film is held as a whole). A file that cannot be read gives no
/// chapters, which the companion says plainly, and the failure is logged.
/// </summary>
public sealed class CompanionChapterSource(
    PlaybackCapabilitiesService playback,
    IMediaAssetRepository assets,
    IEpubContentService epub,
    ILogger<CompanionChapterSource> logger) : ICompanionChapterSource
{
    public async Task<IReadOnlyList<CompanionChapter>> GetChaptersAsync(DisplayWorkRow row, Guid? profileId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        try
        {
            switch (DisplayMediaRules.NormalizeDisplayKind(row.MediaType))
            {
                case "Audiobook":
                    var manifest = await playback.BuildManifestAsync(row.AssetId, "web", profileId, ct).ConfigureAwait(false);
                    return manifest is null
                        ? []
                        : manifest.Chapters
                            .Where(chapter => string.Equals(chapter.Kind, PlaybackChapterKinds.Chapter, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(chapter => chapter.Index)
                            .Select(chapter => new CompanionChapter(chapter.Index, chapter.Title.Trim(), chapter.StartSeconds))
                            .ToList();
                case "Book":
                    var asset = await assets.FindByIdAsync(row.AssetId, ct).ConfigureAwait(false);
                    if (asset is null || !File.Exists(asset.FilePathRoot)
                        || !string.Equals(Path.GetExtension(asset.FilePathRoot), ".epub", StringComparison.OrdinalIgnoreCase))
                    {
                        return [];
                    }

                    var toc = await epub.GetTableOfContentsAsync(asset.FilePathRoot, ct).ConfigureAwait(false);
                    return toc
                        .Where(entry => !string.IsNullOrWhiteSpace(entry.Title))
                        .GroupBy(entry => entry.ChapterIndex)
                        .Select(group => new CompanionChapter(group.Key, group.First().Title.Trim()))
                        .OrderBy(chapter => chapter.Index)
                        .ToList();
                default:
                    return [];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Chapters for work {WorkId} could not be read for the companion", row.WorkId);
            return [];
        }
    }
}
