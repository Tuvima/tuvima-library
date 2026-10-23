using Dapper;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A manually curated collection (Custom, materialized, manual membership) is never empty, so its last personal-media
/// source cannot be removed; playlists and rule-driven collections are not curated and are unaffected (D17, D26, D32).
/// Real data store; only the caller's permissions are faked.
/// </summary>
public sealed class CollectionPersonalMediaRemovalTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima-collection-source-removal-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly CollectionPersonalMediaService _service;
    private readonly Guid _profile = Guid.NewGuid();
    private readonly RequestAuthority _administrator;

    public CollectionPersonalMediaRemovalTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _service = new CollectionPersonalMediaService(
            new CollectionRepository(_database),
            new ProfileRepository(_database),
            new ViewProfileRepository(_database),
            new ViewGalleryRepository(_database),
            new CollectionViewSourceRepository(_database),
            new AllowEverything(),
            new AllowEverything());
        _administrator = new RequestAuthority(
            PrincipalKind.Human,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: _profile,
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ActiveProfileIsServerAdministrator: true);
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@profile, 'Owner', '#7C4DFF', 'Administrator', CURRENT_TIMESTAMP);
            """, new { profile = _profile });
    }

    [Fact]
    public async Task RemovingTheLastSourceOfACuratedCollection_IsRefusedWithTheClearMessageAndKeepsTheSource()
    {
        var collection = SeedCollection("Custom", "materialized", "Manual");
        var source = SeedSource(collection);

        var result = await _service.RemoveAsync(collection, source, _administrator);

        Assert.True(result.Found);
        Assert.True(result.Allowed);
        Assert.True(result.WouldEmptyCollection);
        Assert.Equal("A collection needs at least one item. Delete the collection instead.", result.Error);
        Assert.Equal(1, SourceCount(collection));
    }

    [Fact]
    public async Task RemovingASourceFromACuratedCollection_IsAllowedWhileAnotherSourceRemains()
    {
        var collection = SeedCollection("Custom", "materialized", "Manual");
        var first = SeedSource(collection);
        var second = SeedSource(collection);

        Assert.Null((await _service.RemoveAsync(collection, first, _administrator)).Error);
        Assert.Equal(1, SourceCount(collection));

        var last = await _service.RemoveAsync(collection, second, _administrator);
        Assert.True(last.WouldEmptyCollection);
        Assert.Equal(1, SourceCount(collection));
    }

    [Fact]
    public async Task RemovingTheOnlySourceOfACuratedCollection_IsAllowedWhileAnItemRemains()
    {
        var collection = SeedCollection("Custom", "materialized", "Manual");
        var source = SeedSource(collection);
        SeedItem(collection);

        var result = await _service.RemoveAsync(collection, source, _administrator);

        Assert.True(result.Found);
        Assert.Null(result.Error);
        Assert.False(result.WouldEmptyCollection);
        Assert.Equal(0, SourceCount(collection));
    }

    [Fact]
    public async Task RemovingTheLastSourceOfARuleDrivenCollection_IsStillAllowed()
    {
        var collection = SeedCollection("Custom", "query", "Smart");
        var source = SeedSource(collection);

        var result = await _service.RemoveAsync(collection, source, _administrator);

        Assert.True(result.Found);
        Assert.Null(result.Error);
        Assert.Equal(0, SourceCount(collection));
    }

    private Guid SeedCollection(string type, string resolution, string membership)
    {
        var collection = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
            VALUES (@collection, 'Test', @type, 'library', @resolution, @membership, 'Library', 'Everyone');
            """, new { collection, type, resolution, membership });
        return collection;
    }

    private Guid SeedSource(Guid collection)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collection_view_sources
                (id, collection_id, owner_profile_id, source_kind, rule_version, rule_json, position, created_at, updated_at)
            VALUES (@id, @collection, @profile, 'smart_rule', 1, '{"media_kind":"image","favorite":true}', 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """, new { id, collection, profile = _profile });
        return id;
    }

    private void SeedItem(Guid collection)
    {
        var workId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Movies', 'standalone', 'accepted');
            INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@id, @collection, @workId, 1);
            """, new { id = Guid.NewGuid(), collection, workId });
    }

    private int SourceCount(Guid collection)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM collection_view_sources WHERE collection_id = @collection", new { collection });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class AllowEverything : IAuthorizationEvaluator, IViewResourceAuthorizationService
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());

        public Task<ViewAccessOutcome> AuthorizePersonalScopeBootstrapAsync(RequestAuthority caller, CancellationToken ct = default) =>
            Task.FromResult(ViewAccessOutcome.Allowed);

        public Task<ViewAccessDecision> AuthorizeAsync(RequestAuthority caller, ViewResourceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ViewAccessDecision(ViewAccessOutcome.Allowed));
    }
}
