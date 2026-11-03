using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Engine reads behind the editor's family tree (universal editor v3 WP2): the navigator's structure-only answer (root and parents, each
/// with a matched state and reason), and the owned-children read's direct-only paging with the reason a file is not matched.
/// </summary>
public sealed class MediaEditorFamilyReadTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly Guid _showId = Guid.NewGuid();
    private readonly Guid _seasonId = Guid.NewGuid();
    private readonly Guid _episodeId = Guid.NewGuid();
    private readonly Guid _strayId = Guid.NewGuid();

    public MediaEditorFamilyReadTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_editor_family_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
    }

    public void Dispose()
    {
        _database.Dispose();
        try { File.Delete(_databasePath); } catch (IOException) { /* a temp file another process still holds is cleaned up with the folder */ }
    }

    // A show with one season holding one episode (matched by id), and one file sitting straight under the show with no episode.
    private void SeedShow(bool showMatched)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@showId, 'TV', 'parent', 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership) VALUES (@seasonId, 'TV', 'parent', @showId, 1, 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership) VALUES (@episodeId, 'TV', 'child', @seasonId, 1, 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ownership) VALUES (@strayId, 'TV', 'child', @showId, 'Owned');
            INSERT INTO editions (id, work_id, format_label) VALUES (@episodeEdition, @episodeId, 'MKV'), (@strayEdition, @strayId, 'MKV');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@episodeAsset, @episodeEdition, 'ep1', '/tv/show/s01e01.mkv'), (@strayAsset, @strayEdition, 'stray', '/tv/show/random clip.mkv');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@showId, 'title', 'Le Continental', datetime('now')),
                (@episodeId, 'tmdb_id', '9001', datetime('now')),
                (@episodeId, 'episode_title', 'Pilot', datetime('now'));
            """, new
        {
            showId = _showId,
            seasonId = _seasonId,
            episodeId = _episodeId,
            strayId = _strayId,
            episodeEdition = Guid.NewGuid(),
            strayEdition = Guid.NewGuid(),
            episodeAsset = Guid.NewGuid(),
            strayAsset = Guid.NewGuid(),
        });
        if (showMatched)
        {
            connection.Execute("INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@showId, 'tmdb_id', '1234', datetime('now'));", new { showId = _showId });
        }
    }

    private MediaEditorNavigationReadService Navigator() =>
        new(_database, null!, new HierarchyAlignmentService(_database, null!));

    [Fact]
    public async Task Navigator_StructureOnly_ReturnsTheRootAndItsSeasons_NotTheEpisodes()
    {
        SeedShow(showMatched: true);

        var full = await Navigator().GetNavigatorAsync(_episodeId, CancellationToken.None);
        var structure = await Navigator().GetNavigatorAsync(_episodeId, CancellationToken.None, structureOnly: true);

        Assert.Contains(full!.Nodes, node => node.NodeKind == "episode");
        Assert.NotNull(structure);
        Assert.Equal(_showId, structure.ContainerEntityId);
        Assert.Equal(["series", "season"], structure.Nodes.Select(node => node.NodeKind));
        Assert.DoesNotContain(structure.Nodes, node => node.IsLeaf);
    }

    [Fact]
    public async Task Navigator_MarksASeasonMatched_WhenTheShowAboveItIsMatched()
    {
        SeedShow(showMatched: true);

        var structure = await Navigator().GetNavigatorAsync(_showId, CancellationToken.None, structureOnly: true);

        Assert.All(structure!.Nodes, node =>
        {
            Assert.Equal("matched", node.MatchState);
            Assert.Null(node.MatchReason);
        });
    }

    [Fact]
    public async Task Navigator_MarksAnUnmatchedShowAndItsSeasons_WithAReasonInWords()
    {
        SeedShow(showMatched: false);

        var structure = await Navigator().GetNavigatorAsync(_showId, CancellationToken.None, structureOnly: true);

        Assert.All(structure!.Nodes, node =>
        {
            Assert.Equal("unmatched", node.MatchState);
            Assert.False(string.IsNullOrWhiteSpace(node.MatchReason));
        });
    }

    // A series of two videos, one more video that stands alone (universal editor v3.1).
    private (Guid Series, Guid InSeries, Guid Alone) SeedVideos()
    {
        var series = Guid.NewGuid();
        var inSeries = Guid.NewGuid();
        var other = Guid.NewGuid();
        var alone = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@series, 'Videos', 'parent', 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership) VALUES (@inSeries, 'Videos', 'child', @series, 1, 'Owned');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership) VALUES (@other, 'Videos', 'child', @series, 2, 'Owned');
            INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@alone, 'Videos', 'child', 'Owned');
            INSERT INTO editions (id, work_id, format_label) VALUES (@e1, @inSeries, 'MP4'), (@e2, @other, 'MP4'), (@e3, @alone, 'MP4');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@a1, @e1, 'v1', '/clips/one.mp4'), (@a2, @e2, 'v2', '/clips/two.mp4'), (@a3, @e3, 'v3', '/clips/alone.mp4');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@series, 'title', 'Cooking with Sam', datetime('now'));
            """, new
        {
            series, inSeries, other, alone,
            e1 = Guid.NewGuid(), e2 = Guid.NewGuid(), e3 = Guid.NewGuid(),
            a1 = Guid.NewGuid(), a2 = Guid.NewGuid(), a3 = Guid.NewGuid(),
        });
        return (series, inSeries, alone);
    }

    [Fact]
    public async Task Navigator_ForASeriesOfVideos_ShowsTheSeriesWithoutAnAmberMark()
    {
        var (series, inSeries, _) = SeedVideos();

        var structure = await Navigator().GetNavigatorAsync(inSeries, CancellationToken.None, structureOnly: true);

        Assert.True(structure!.Enabled);
        Assert.Equal(series, structure.ContainerEntityId);
        Assert.All(structure.Nodes, node => Assert.Equal("matched", node.MatchState));
    }

    [Fact]
    public async Task OwnedChildren_ForVideos_MarkOnlyTheOnesInNoSeriesAsNotMatched()
    {
        var (series, _, alone) = SeedVideos();
        var reader = new MediaEditorOwnedChildReadService(_database);

        var inside = await reader.SearchAsync(series, null, 1, 50, null, null, null, null, null, CancellationToken.None);

        Assert.All(inside!.Items, item => Assert.Equal("matched", item.MatchState));
        var standalone = await reader.SearchAsync(alone, null, 1, 50, null, null, null, null, null, CancellationToken.None);
        Assert.Equal("unmatched", Assert.Single(standalone!.Items).MatchState);
        Assert.Equal("This video is not in a series.", Assert.Single(standalone.Items).MatchReason);
    }

    [Fact]
    public async Task OwnedChildren_DirectOnly_ListsOnlyTheFilesStraightUnderTheParent()
    {
        SeedShow(showMatched: true);
        var reader = new MediaEditorOwnedChildReadService(_database);

        var everything = await reader.SearchAsync(_showId, null, 1, 50, null, null, null, null, null, CancellationToken.None);
        var underShow = await reader.SearchAsync(_showId, null, 1, 50, null, null, null, null, null, CancellationToken.None, directOnly: true);
        var underSeason = await reader.SearchAsync(_seasonId, null, 1, 50, null, null, null, null, null, CancellationToken.None, directOnly: true);

        Assert.Equal(2, everything!.TotalCount);
        Assert.Equal(_strayId, Assert.Single(underShow!.Items).WorkId);
        Assert.Equal(1, underShow.TotalCount);
        Assert.Equal(_episodeId, Assert.Single(underSeason!.Items).WorkId);
    }

    [Fact]
    public async Task OwnedChildren_AnUnmatchedFileWithNoNumber_SaysWhyInWords_AndAMatchedFileSaysNothing()
    {
        SeedShow(showMatched: true);
        var reader = new MediaEditorOwnedChildReadService(_database);

        var result = await reader.SearchAsync(_showId, null, 1, 50, null, null, null, null, null, CancellationToken.None);

        var matched = result!.Items.Single(item => item.WorkId == _episodeId);
        var stray = result.Items.Single(item => item.WorkId == _strayId);
        Assert.Equal("matched", matched.MatchState);
        Assert.Null(matched.MatchReason);
        Assert.Equal("unmatched", stray.MatchState);
        Assert.Contains("season and episode number", stray.MatchReason);
    }

    [Theory]
    [InlineData("TV", "1", 1, "No show, season or episode")]
    [InlineData("Music", "3", null, "No album or song")]
    [InlineData("Books", null, null, "No book or issue")]
    [InlineData("Movies", null, null, "No provider matches")]
    public void BuildMatchReason_NamesWhatToLookAt_PerKindOfMedia(string mediaType, string? number, int? season, string expected)
    {
        var reason = MediaEditorOwnedChildReadService.BuildMatchReason("unmatched", mediaType, number, season);

        Assert.StartsWith(expected, reason);
        Assert.Null(MediaEditorOwnedChildReadService.BuildMatchReason("matched", mediaType, number, season));
    }
}
