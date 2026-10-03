using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class AudiobookPlaybackProjectionTests
{
    [Fact]
    public void MultiAssetProjectionUsesTheCurrentAssetsLocalChaptersAndCumulativeBookOffset()
    {
        var parts = Enumerable.Range(0, 32)
            .Select(index =>
            {
                var assetId = Guid.NewGuid();
                PlaybackChapterDto[] localChapters = index switch
                {
                    0 => [Chapter(assetId, 0, "Intro", 0, 100)],
                    31 => [Chapter(assetId, 0, "Outro", 0, 100)],
                    28 => [Chapter(assetId, 0, "Chapter 29", 0, 100)],
                    _ => [Chapter(assetId, 0, $"Part {index + 1}", 0, 100)],
                };
                return (AssetId: assetId, Chapters: localChapters);
            })
            .ToArray();
        var allChapters = parts.SelectMany(part => part.Chapters).ToArray();

        var intro = AudiobookPlaybackProjection.Create(allChapters, parts[0].AssetId, 0, 100);
        Assert.NotNull(intro);
        Assert.Equal("Intro", intro.ActiveChapter?.Title);
        Assert.Equal("Book progress", intro.Label);
        Assert.Equal(0d, intro.Position);
        Assert.Equal(3200d, intro.Total);

        var lateLocalTimeInIntro = AudiobookPlaybackProjection.Create(allChapters, parts[0].AssetId, 98, 100);
        Assert.NotNull(lateLocalTimeInIntro);
        Assert.Equal("Intro", lateLocalTimeInIntro.ActiveChapter?.Title);
        Assert.Equal(98d, lateLocalTimeInIntro.Position);

        var chapter29 = AudiobookPlaybackProjection.Create(allChapters, parts[28].AssetId, 10, 100);
        Assert.NotNull(chapter29);
        Assert.Equal("Chapter 29", chapter29.ActiveChapter?.Title);
        Assert.Equal(2810d, chapter29.Position);
        Assert.Equal(32, chapter29.ChapterCount);

    }

    [Fact]
    public void IncompleteOrInvalidPartMetadataFallsBackToTruthfulCurrentRecordingProgress()
    {
        var firstAsset = Guid.NewGuid();
        var currentAsset = Guid.NewGuid();
        var incomplete = new[]
        {
            Chapter(firstAsset, 0, "Part 1", 0, 100),
            Chapter(currentAsset, 0, "Current chapter", 0, null),
            Chapter(null, 2, "Unassigned chapter", 0, 100),
        };

        var projection = AudiobookPlaybackProjection.Create(incomplete, currentAsset, 28, 90);

        Assert.NotNull(projection);
        Assert.Equal("Current recording", projection.Label);
        Assert.Equal(28d, projection.Position);
        Assert.Equal(90d, projection.Total);
        Assert.Equal("Current chapter", projection.ActiveChapter?.Title);

        var invalid = incomplete.Where(chapter => chapter.AssetId.HasValue).Select(chapter => chapter with
        {
            EndSeconds = chapter.AssetId == currentAsset ? 0 : chapter.EndSeconds,
        }).ToArray();
        var invalidProjection = AudiobookPlaybackProjection.Create(invalid, currentAsset, 28, 90);
        Assert.NotNull(invalidProjection);
        Assert.Equal("Current recording", invalidProjection.Label);
        Assert.Equal(90d, invalidProjection.Total);
    }

    private static PlaybackChapterDto Chapter(Guid? assetId, int index, string title, double start, double? end) => new()
    {
        AssetId = assetId,
        Index = index,
        Title = title,
        StartSeconds = start,
        EndSeconds = end,
    };
}
