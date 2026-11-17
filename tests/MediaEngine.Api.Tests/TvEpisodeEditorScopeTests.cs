using System.Reflection;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Opening any episode of a show in the universal editor (v8, issue 1047). Every episode gets an editor scope whose field id is its own
/// work id, whether it sits in a season or straight under the show with no season level, so the Dashboard accepts the id the item
/// dropdown passes as ready to edit.
/// </summary>
public sealed class TvEpisodeEditorScopeTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima_tv_editor_scopes_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    private readonly Guid _seasonedShowId = Guid.NewGuid();
    private readonly Guid _seasonId = Guid.NewGuid();
    private readonly Guid _seasonEpisodeId = Guid.NewGuid();
    private readonly Guid _flatShowId = Guid.NewGuid();
    private readonly Guid _flatEpisodeOneId = Guid.NewGuid();
    private readonly Guid _flatEpisodeSixteenId = Guid.NewGuid();

    public TvEpisodeEditorScopeTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        SeedShows();
    }

    [Fact]
    public async Task EpisodeInASeason_GetsSeriesSeasonAndEpisodeScopes_AndOpensOnItsOwnWorkId()
    {
        var scopes = await ScopesOfAsync(_seasonEpisodeId);

        Assert.Equal(["series", "season", "episode"], scopes.Select(scope => scope.ScopeId));
        Assert.Equal(_seasonedShowId, scopes.Single(scope => scope.ScopeId == "series").FieldEntityId);
        Assert.Equal(_seasonId, scopes.Single(scope => scope.ScopeId == "season").FieldEntityId);
        Assert.Equal(_seasonEpisodeId, scopes.Single(scope => scope.ScopeId == "episode").FieldEntityId);
        Assert.Equal("episode", await InitialScopeOfAsync(_seasonEpisodeId));
    }

    [Fact]
    public async Task EpisodeStraightUnderTheShow_GetsSeriesAndEpisodeScopes_WithNoSeason()
    {
        var scopes = await ScopesOfAsync(_flatEpisodeSixteenId);

        Assert.Equal(["series", "episode"], scopes.Select(scope => scope.ScopeId));
        Assert.Equal(_flatShowId, scopes.Single(scope => scope.ScopeId == "series").FieldEntityId);
        Assert.Equal(_flatEpisodeSixteenId, scopes.Single(scope => scope.ScopeId == "episode").FieldEntityId);
    }

    [Fact]
    public async Task EveryEpisodeOfAShow_IsOpenedUnderItsOwnWorkId_WhicheverScopeTheEngineOpensOn()
    {
        foreach (var episodeId in new[] { _seasonEpisodeId, _flatEpisodeOneId, _flatEpisodeSixteenId })
        {
            var scopes = await ScopesOfAsync(episodeId);
            var initialScope = await InitialScopeOfAsync(episodeId);
            var opened = scopes.Single(scope => scope.ScopeId == initialScope);

            Assert.Equal(episodeId, opened.FieldEntityId);
        }
    }

    [Fact]
    public async Task ShowAndSeasonLaunches_StillOpenOnTheirOwnScope()
    {
        Assert.Equal("series", await InitialScopeOfAsync(_flatShowId));
        Assert.Equal("season", await InitialScopeOfAsync(_seasonId));
        Assert.Equal(_seasonId, (await ScopesOfAsync(_seasonId)).Single(scope => scope.ScopeId == "season").FieldEntityId);
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

    // One show with a season and an episode in it, and one show whose episodes sit straight under it (no season level).
    private void SeedShows()
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@seasonedShowId, 'TV', 'parent', 'accepted');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@seasonId, 'TV', 'parent', @seasonedShowId, 1, 'accepted');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@seasonEpisodeId, 'TV', 'child', @seasonId, 1, 'accepted');
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@flatShowId, 'TV', 'parent', 'accepted');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@flatEpisodeOneId, 'TV', 'child', @flatShowId, 1, 'accepted');
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@flatEpisodeSixteenId, 'TV', 'child', @flatShowId, 16, 'accepted');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@seasonedShowId, 'title', 'Severance', datetime('now')),
                (@seasonEpisodeId, 'title', 'Good News About Hell', datetime('now')),
                (@flatShowId, 'title', 'Fargo', datetime('now')),
                (@flatEpisodeOneId, 'title', 'The Crocodiles Dilemma', datetime('now')),
                (@flatEpisodeSixteenId, 'title', 'Bisquick', datetime('now'));
            """,
            new
            {
                seasonedShowId = _seasonedShowId,
                seasonId = _seasonId,
                seasonEpisodeId = _seasonEpisodeId,
                flatShowId = _flatShowId,
                flatEpisodeOneId = _flatEpisodeOneId,
                flatEpisodeSixteenId = _flatEpisodeSixteenId,
            });
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
