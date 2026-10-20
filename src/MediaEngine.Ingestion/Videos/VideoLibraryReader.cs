using System.Globalization;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Processors.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Videos;

/// <summary>
/// Reads a Videos library with no outside lookup: folder layout (standalone, series, section, episode), then the
/// identification order of <c>config/videos.json</c> (YouTube ID in the name or .info.json, a Kodi .nfo, the cleaned
/// file name). The item records how it was identified.
/// </summary>
public sealed class VideoLibraryReader : IVideoLibraryReader
{
    // Claims the generic video processor guesses from a file name. A Videos library never reads a TV show out of a name.
    private static readonly HashSet<string> GuessedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        MetadataFieldConstants.Title, "show_name", "series", "season_number", "season", "episode_number", "episode",
        "episode_end", "episode_title", MetadataFieldConstants.Year, MetadataFieldConstants.Description,
    };

    private readonly VideoNamingConfigurationProvider _naming;
    private readonly IWorkRepository? _works;
    private readonly ICanonicalValueRepository? _canonicals;
    private readonly ILogger<VideoLibraryReader>? _logger;

    public VideoLibraryReader(
        VideoNamingConfigurationProvider naming,
        IWorkRepository? works = null,
        ICanonicalValueRepository? canonicals = null,
        ILogger<VideoLibraryReader>? logger = null)
    {
        _naming = naming;
        _works = works;
        _canonicals = canonicals;
        _logger = logger;
    }

    public ProcessorResult Apply(ProcessorResult result, string libraryFolder)
    {
        if (result.IsCorrupt || !VideoFolderLayout.IsVideoFile(result.FilePath))
        {
            return result;
        }

        var naming = _naming.Current;
        var placement = VideoFolderLayout.Locate(libraryFolder, result.FilePath, naming);
        var stem = Path.GetFileNameWithoutExtension(result.FilePath);
        var cleaned = VideoTitleCleaner.Clean(stem, naming.TitleCleaning);
        var info = VideoSidecarReader.ReadInfoJson(result.FilePath);
        var nfo = VideoSidecarReader.ReadNfo(result.FilePath);

        var youtubeId = cleaned.YoutubeId ?? info?.YoutubeId;
        var method = Identify(naming, youtubeId, info, nfo);
        var title = info?.Title ?? nfo?.Title ?? cleaned.Title;
        var titleConfidence = info?.Title is not null || nfo?.Title is not null ? 0.95 : 0.7;

        var claims = result.Claims.Where(claim => !GuessedKeys.Contains(claim.Key)).ToList();
        void Add(string key, string? value, double confidence)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                claims.Add(new ExtractedClaim { Key = key, Value = value, Confidence = confidence });
            }
        }

        Add(MetadataFieldConstants.Title, title, titleConfidence);
        Add(MetadataFieldConstants.Description, info?.Description ?? nfo?.Description, 0.9);
        var date = info?.ReleaseDate ?? nfo?.ReleaseDate;
        if (date is { } released)
        {
            Add(MetadataFieldConstants.OriginalReleaseDate, released.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0.9);
            Add(MetadataFieldConstants.Year, released.Year.ToString(CultureInfo.InvariantCulture), 0.9);
        }

        Add(MetadataFieldConstants.Network, info?.Channel ?? nfo?.Channel, 0.85);
        Add(MetadataFieldConstants.VideoIdentifiedBy, method, 1.0);
        Add(MetadataFieldConstants.YoutubeVideoId, youtubeId, 1.0);
        if (!placement.IsStandalone)
        {
            Add(MetadataFieldConstants.VideoSeries, placement.SeriesTitle, 0.98);
            Add(MetadataFieldConstants.VideoSeriesKey, placement.SeriesKey, 0.98);
            Add(MetadataFieldConstants.VideoSection, placement.SectionTitle, 0.98);
            Add(MetadataFieldConstants.VideoSectionIndex, placement.SectionIndex?.ToString(CultureInfo.InvariantCulture), 0.98);
            Add(MetadataFieldConstants.VideoEpisodeIndex, placement.EpisodeIndex.ToString(CultureInfo.InvariantCulture), 0.98);

            // The same position under the names the Watch pages already understand, so Resume, Up Next and the episode
            // list work unchanged. A series without sections is a single group, number 1.
            Add(MetadataFieldConstants.ShowName, placement.SeriesTitle, 0.98);
            Add(MetadataFieldConstants.SeasonNumber, (placement.SectionIndex ?? 1).ToString(CultureInfo.InvariantCulture), 0.98);
            Add(MetadataFieldConstants.EpisodeNumber, placement.EpisodeIndex.ToString(CultureInfo.InvariantCulture), 0.98);
        }

        return new ProcessorResult
        {
            FilePath = result.FilePath,
            DetectedType = MediaType.Videos,
            Claims = claims,
            CoverImage = result.CoverImage,
            CoverImageMimeType = result.CoverImageMimeType,
            MediaTypeCandidates = [new() { Type = MediaType.Videos, Confidence = 0.99, Reason = "Configured Videos library" }],
        };
    }

    public async Task RecordPlacementAsync(Guid assetId, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct = default)
    {
        if (_works is null || _canonicals is null || metadata is null
            || !metadata.TryGetValue(MetadataFieldConstants.VideoSeries, out var seriesTitle))
        {
            return;
        }

        var lineage = await _works.GetLineageByAssetAsync(assetId, ct).ConfigureAwait(false);
        if (lineage?.ParentWorkId is not { } parentId)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var values = new List<CanonicalValue>();
        if (metadata.TryGetValue(MetadataFieldConstants.VideoSection, out var sectionTitle) && lineage.RootParentWorkId != parentId)
        {
            // series (root) -> section (parent) -> episode
            values.Add(Title(lineage.RootParentWorkId, seriesTitle, now));
            values.Add(Title(parentId, sectionTitle, now));
        }
        else
        {
            values.Add(Title(lineage.RootParentWorkId, seriesTitle, now));
        }

        await _canonicals.UpsertBatchAsync(values, ct).ConfigureAwait(false);
        _logger?.LogDebug("Videos: recorded series '{Series}' for asset {AssetId}", seriesTitle, assetId);
    }

    private static string Identify(VideoNamingConfiguration naming, string? youtubeId, VideoSidecarInfo? info, VideoSidecarInfo? nfo)
    {
        foreach (var step in naming.IdentificationOrder)
        {
            switch (step)
            {
                case VideoIdentificationMethods.YoutubeId when youtubeId is not null:
                case VideoIdentificationMethods.InfoJson when info is not null:
                case VideoIdentificationMethods.Nfo when nfo is not null:
                    return step;
            }
        }

        return VideoIdentificationMethods.FileName;
    }

    private static CanonicalValue Title(Guid workId, string title, DateTimeOffset at) => new()
    {
        EntityId = workId,
        Key = MetadataFieldConstants.Title,
        Value = title,
        LastScoredAt = at,
    };
}
