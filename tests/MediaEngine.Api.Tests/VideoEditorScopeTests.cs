using System.Reflection;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Editing a folder of videos (universal editor v5, WP1, bug 21). A Videos series, its sections and its videos each get an editor
/// scope whose field id is the work the page or the item dropdown asked for, so the Dashboard accepts the item as ready to edit.
/// The words are the folder's own: Series, Section, Video, never Season or Episode.
/// </summary>
public sealed class VideoEditorScopeTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_video_editor_scopes_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly Guid _sectionId = Guid.NewGuid();
    private readonly Guid _sectionVideoId = Guid.NewGuid();
    private readonly Guid _looseVideoId = Guid.NewGuid();
    private readonly Guid _standaloneId = Guid.NewGuid();

    public VideoEditorScopeTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        SeedGeneratedVideoFolder();
    }

    [Fact]
    public async Task VideoInASection_GetsSeriesSectionAndVideoScopes_AndTheLaunchedVideosScopeIdIsItsWorkId()
    {
        var scopes = await ScopesOfAsync(_sectionVideoId);

        Assert.Equal(["series", "section", "video"], scopes.Select(scope => scope.ScopeId));
        Assert.Equal(_seriesId, scopes.Single(scope => scope.ScopeId == "series").FieldEntityId);
        Assert.Equal(_sectionId, scopes.Single(scope => scope.ScopeId == "section").FieldEntityId);
        Assert.Equal(_sectionVideoId, scopes.Single(scope => scope.ScopeId == "video").FieldEntityId);
        Assert.Equal("video", await InitialScopeOfAsync(_sectionVideoId));
    }

    [Fact]
    public async Task VideoStraightUnderTheSeries_GetsSeriesAndVideoScopes_WithNoSection()
    {
        var scopes = await ScopesOfAsync(_looseVideoId);

        Assert.Equal(["series", "video"], scopes.Select(scope => scope.ScopeId));
        Assert.Equal(_looseVideoId, scopes.Single(scope => scope.ScopeId == "video").FieldEntityId);
        Assert.Equal(_seriesId, scopes.Single(scope => scope.ScopeId == "series").FieldEntityId);
    }

    [Fact]
    public async Task SectionLaunch_GetsSeriesAndSectionScopes_AndOpensOnTheSection()
    {
        var scopes = await ScopesOfAsync(_sectionId);

        Assert.Equal(["series", "section"], scopes.Select(scope => scope.ScopeId));
        Assert.Equal(_sectionId, scopes.Single(scope => scope.ScopeId == "section").FieldEntityId);
        Assert.Equal("section", await InitialScopeOfAsync(_sectionId));
    }

    [Fact]
    public async Task SeriesLaunch_GetsOnlyTheSeriesScope_AndOpensOnIt()
    {
        var scopes = await ScopesOfAsync(_seriesId);

        var series = Assert.Single(scopes);
        Assert.Equal("series", series.ScopeId);
        Assert.Equal(_seriesId, series.FieldEntityId);
        Assert.Equal("series", await InitialScopeOfAsync(_seriesId));
    }

    [Fact]
    public async Task StandaloneVideo_GetsOneVideoScopeWithItsOwnWorkId()
    {
        var scopes = await ScopesOfAsync(_standaloneId);

        var video = Assert.Single(scopes);
        Assert.Equal("video", video.ScopeId);
        Assert.Equal(_standaloneId, video.FieldEntityId);
        Assert.Equal("video", await InitialScopeOfAsync(_standaloneId));
    }

    [Fact]
    public async Task EveryVideoOfTheFolder_IsOpenedUnderItsOwnWorkId_WhicheverScopeTheEngineOpensOn()
    {
        foreach (var videoId in new[] { _sectionVideoId, _looseVideoId, _standaloneId })
        {
            var scopes = await ScopesOfAsync(videoId);
            var initialScope = await InitialScopeOfAsync(videoId);
            var opened = scopes.Single(scope => scope.ScopeId == initialScope);

            Assert.Equal(videoId, opened.FieldEntityId);
        }
    }

    [Fact]
    public async Task VideoFolderScopeLabels_UseSeriesSectionAndVideo_NeverTheTvWords()
    {
        var scopes = await ScopesOfAsync(_sectionVideoId);

        Assert.Equal(["Series", "Section", "Video"], scopes.Select(scope => scope.Label));
        var words = scopes.SelectMany(scope => new[] { scope.Label, scope.DisplaySubtitle, scope.BreadcrumbLabel, scope.ScopeSummary, scope.ReadOnlyHint })
            .Where(text => text is not null)
            .Select(text => text!);
        Assert.DoesNotContain(words, text =>
            text.Contains("Season", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Episode", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"S\d+E\d+"));
    }

    [Fact]
    public void VideoFolderSeries_KeepsItsCoverAsASingleScopedPicture_NotAnAutomaticStack()
    {
        Assert.False(ArtworkScopeService.IsAutomaticGroupScope("series", "Videos"));
        Assert.False(ArtworkScopeService.IsAutomaticGroupScope("series", "TV"));
        Assert.True(ArtworkScopeService.IsAutomaticGroupScope("series", "Books"));
        Assert.False(ArtworkScopeService.IsAutomaticGroupScope("video", "Videos"));
    }

    private async Task<string> InitialScopeOfAsync(Guid entityId)
    {
        var context = await ResolveContextAsync(entityId);
        return (string)context.GetType().GetProperty("InitialScope")!.GetValue(context)!;
    }

    private async Task<IReadOnlyList<MetadataEndpoints.EditorScopeResolution>> ScopesOfAsync(Guid entityId)
    {
        var context = await ResolveContextAsync(entityId);
        return (IReadOnlyList<MetadataEndpoints.EditorScopeResolution>)context.GetType().GetProperty("Scopes")!.GetValue(context)!;
    }

    private async Task<object> ResolveContextAsync(Guid entityId)
    {
        var method = typeof(MetadataEndpoints).GetMethod(
            "ResolveEditorScopeContextAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(null,
        [
            entityId,
            new CanonicalValueRepository(_database),
            new LibraryItemRepository(_database),
            new MetadataEditorRepository(_database),
            CancellationToken.None,
        ])!;
        await task;
        var context = task.GetType().GetProperty("Result")!.GetValue(task);
        Assert.NotNull(context);
        return context;
    }

    // A generated Videos folder: a series with one section holding a video and one video straight under it, plus a loose video.
    private void SeedGeneratedVideoFolder()
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@seriesId, 'Videos', 'parent', 'accepted');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@sectionId, 'Videos', 'parent', @seriesId, 1, 'accepted');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@sectionVideoId, 'Videos', 'child', @sectionId, 1, 'accepted');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@looseVideoId, 'Videos', 'child', @seriesId, 2, 'accepted');
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@standaloneId, 'Videos', 'standalone', 'accepted');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@seriesId, 'title', 'Steph Curry MasterClass', datetime('now')),
                (@sectionId, 'title', 'Ball Handling', datetime('now')),
                (@sectionVideoId, 'title', 'Warm Up', datetime('now')),
                (@looseVideoId, 'title', 'Introduction', datetime('now')),
                (@standaloneId, 'title', 'Holiday Recording', datetime('now'));
            """,
            new
            {
                seriesId = _seriesId,
                sectionId = _sectionId,
                sectionVideoId = _sectionVideoId,
                looseVideoId = _looseVideoId,
                standaloneId = _standaloneId,
            });
        foreach (var (workId, name) in new[] { (_sectionVideoId, "section/warm-up.mkv"), (_looseVideoId, "intro.mkv"), (_standaloneId, "holiday.mkv") })
        {
            var editionId = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
                """,
                new
                {
                    editionId,
                    workId,
                    assetId = Guid.NewGuid(),
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"/videos/course/{name}",
                    libraryId = Guid.NewGuid().ToString("D"),
                });
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // SQLite can release a pooled test handle just after disposal; the OS temp sweep removes the file.
        }
    }
}
