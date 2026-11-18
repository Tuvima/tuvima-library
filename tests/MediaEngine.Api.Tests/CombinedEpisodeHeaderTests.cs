using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A file named like S01E02-03 is one file for two episodes (spec review-mockups-2026-11-18, D9). The editor's Season dropdown counts
/// the episodes and the files apart, and its item dropdown lists the file once as "S01E02-03: Title A + Title B".
/// </summary>
public sealed class CombinedEpisodeHeaderTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("Dr Horrible");

    [Fact]
    public async Task SeasonDropdown_WhenOneFileCoversTwoEpisodes_CountsEpisodesAndFilesApart()
    {
        var show = SeedShow(out var season, out _);

        var header = await _world.Header().GetHeaderAsync(show.Episode1, null);

        var entry = Assert.Single(header!.SecondLevel!.Entries);
        Assert.Equal(season, entry.Id);
        Assert.Equal(3, entry.ItemCount);
        Assert.Equal(2, entry.FileCount);
    }

    [Fact]
    public async Task SeasonDropdown_WhenEveryEpisodeIsItsOwnFile_HasNoFileCount()
    {
        var season = _world.Work("TV", "parent", "Season 1", _world.Work("TV", "parent", "Dr Horrible"), 1);
        _world.File(_world.Work("TV", "child", "Act One", season, 1), "Season 01/S01E01.mkv");
        _world.File(_world.Work("TV", "child", "Act Two", season, 2), "Season 01/S01E02.mkv");

        var header = await _world.Header().GetHeaderAsync(season, null);

        var entry = Assert.Single(header!.SecondLevel!.Entries);
        Assert.Equal(2, entry.ItemCount);
        Assert.Null(entry.FileCount);
    }

    [Fact]
    public async Task ItemDropdown_ListsAFileCoveringTwoEpisodesAsOneRowWithBothTitles()
    {
        var show = SeedShow(out var season, out _);

        var level = await _world.Header().GetLevelAsync(show.Episode1, season, null, 0, null);

        Assert.NotNull(level);
        Assert.Equal(2, level.Items.Count);
        Assert.Equal(3, level.Total);
        var combined = level.Items[1];
        Assert.Equal(show.Episode2, combined.Id);
        Assert.Equal("S01E02-03", combined.Code);
        Assert.Equal("Act Two + Act Three", combined.Title);
        Assert.Equal(2, combined.EpisodeCount);
        Assert.Null(level.Items[0].EpisodeCount);
    }

    [Fact]
    public async Task ItemDropdown_MarksTheCombinedRowCurrentWhenEitherEpisodeIsOpen()
    {
        var show = SeedShow(out var season, out _);

        var level = await _world.Header().GetLevelAsync(show.Episode3, season, null, 0, null);

        Assert.Equal([false, true], level!.Items.Select(row => row.IsCurrent));
    }

    private Shows SeedShow(out Guid season, out Guid asset)
    {
        var show = _world.Work("TV", "parent", "Dr Horrible");
        season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode1 = _world.Work("TV", "child", "Act One", season, 1);
        var episode2 = _world.Work("TV", "child", "Act Two", season, 2);
        var episode3 = _world.Work("TV", "child", "Act Three", season, 3);
        _world.File(episode1, "Season 01/S01E01.mkv");
        asset = _world.File(episode2, "Season 01/S01E02-03.mkv");
        new MediaAssetCoverageRepository(_world.Database).ReplaceForAssetAsync(
            asset,
            [
                new MediaAssetCoverage(asset, episode2, 1, null, null, MediaAssetCoverage.SourceFilename),
                new MediaAssetCoverage(asset, episode3, 2, null, null, MediaAssetCoverage.SourceFilename),
            ]).GetAwaiter().GetResult();
        return new Shows(episode1, episode2, episode3);
    }

    private sealed record Shows(Guid Episode1, Guid Episode2, Guid Episode3);

    public void Dispose() => _world.Dispose();
}
