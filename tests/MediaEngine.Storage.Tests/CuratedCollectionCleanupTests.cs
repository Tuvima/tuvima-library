using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A manually curated collection (Custom, manual membership) is never empty. When the last work in one leaves the
/// library the collection is removed and the removal is written to the system activity ledger; playlists and
/// rule-driven collections follow their own rules and are left alone.
/// </summary>
public sealed class CuratedCollectionCleanupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_curated_cleanup_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public CuratedCollectionCleanupTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task DeletingTheLastWorkOfACuratedCollection_RemovesTheCollectionAndLogsIt()
    {
        var leaving = SeedWorkWithoutEdition("Leaving");
        var staying = SeedWork("Staying");
        var onlyLeaving = SeedCollection("Only the leaving film", "Custom", "materialized", "Manual", leaving);
        var both = SeedCollection("Both films", "Custom", "materialized", "Manual", leaving, staying.WorkId);

        // The work leaves the library when the orphan prune removes it (it has no edition left).
        await new CollectionRepository(_db).PruneOrphanedHierarchyAsync();

        Assert.False(CollectionExists(onlyLeaving));
        Assert.True(CollectionExists(both));
        Assert.Equal(1, ItemCount(both));

        var entry = Assert.Single(await new SystemActivityRepository(_db).GetRecentAsync());
        Assert.Equal(SystemActionType.AutoPurge, entry.ActionType);
        Assert.Equal("Only the leaving film", entry.CollectionName);
        Assert.Equal(onlyLeaving, entry.EntityId);
        Assert.Equal("Removed the collection 'Only the leaving film' because its last item left the library.", entry.Detail);
    }

    [Fact]
    public async Task PlaylistsAndRuleDrivenCollections_AreNotRemovedWhenTheyHoldNoItems()
    {
        var leaving = SeedWorkWithoutEdition("Leaving");
        var playlist = SeedCollection("Road trip", "Playlist", "materialized", "Manual", leaving);
        var folder = SeedCollection("Mixes", "PlaylistFolder", "materialized", "Manual");
        var ruleDriven = SeedCollection("Everything new", "Custom", "query", "Smart");
        var smartCustom = SeedCollection("Smart custom", "Custom", "materialized", "Smart");
        var curated = SeedCollection("Curated", "Custom", "materialized", "Manual", leaving);

        await new CollectionRepository(_db).PruneOrphanedHierarchyAsync();

        Assert.True(CollectionExists(playlist));
        Assert.True(CollectionExists(folder));
        Assert.True(CollectionExists(ruleDriven));
        Assert.True(CollectionExists(smartCustom));
        Assert.False(CollectionExists(curated));
        Assert.Equal("Curated", Assert.Single(await new SystemActivityRepository(_db).GetRecentAsync()).CollectionName);
    }

    [Fact]
    public async Task PruningWorksWithoutEditions_RemovesACuratedCollectionTheyEmptied()
    {
        var orphan = SeedWorkWithoutEdition("Orphan");
        var curated = SeedCollection("Orphans", "Custom", "materialized", "Manual", orphan);
        var playlist = SeedCollection("Orphan playlist", "Playlist", "materialized", "Manual", orphan);

        await new CollectionRepository(_db).PruneOrphanedHierarchyAsync();

        Assert.False(CollectionExists(curated));
        Assert.True(CollectionExists(playlist));
        Assert.Equal(0, ItemCount(playlist));
    }

    [Fact]
    public async Task DeleteEmptyCuratedCollections_ReturnsWhatItRemovedAndTidiesSavedItems()
    {
        var emptyId = SeedCollection("Left empty", "Custom", "materialized", "Manual");
        var kept = SeedCollection("Has an item", "Custom", "materialized", "Manual", SeedWork("Item").WorkId);
        var profileId = Guid.NewGuid();
        using (var connection = _db.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@profileId, 'Reader', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
                INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at)
                VALUES (@profileId, 'Collection', @emptyId, CURRENT_TIMESTAMP),
                       (@profileId, 'Collection', @kept, CURRENT_TIMESTAMP);
                """, new { profileId, emptyId, kept });
        }

        var removed = await new CollectionRepository(_db).DeleteEmptyCuratedCollectionsAsync();

        var only = Assert.Single(removed);
        Assert.Equal(emptyId, only.Id);
        Assert.Equal("Left empty", only.Name);
        Assert.True(CollectionExists(kept));
        using var verify = _db.CreateConnection();
        Assert.Equal(0, verify.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @emptyId", new { emptyId }));
        Assert.Equal(1, verify.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @kept", new { kept }));
    }

    [Fact]
    public async Task ACuratedCollectionWithOneWorkAndOneViewSource_SurvivesTheWorksRemoval()
    {
        var leaving = SeedWorkWithoutEdition("Leaving");
        var withSource = SeedCollection("Film and photos", "Custom", "materialized", "Manual", leaving);
        SeedViewSource(withSource);
        var withoutSource = SeedCollection("Film only", "Custom", "materialized", "Manual", leaving);

        await new CollectionRepository(_db).PruneOrphanedHierarchyAsync();

        Assert.True(CollectionExists(withSource));
        Assert.Equal(0, ItemCount(withSource));
        Assert.False(CollectionExists(withoutSource));
        Assert.Equal("Film only", Assert.Single(await new SystemActivityRepository(_db).GetRecentAsync()).CollectionName);
    }

    [Fact]
    public async Task ACuratedCollectionHoldingOnlyAViewSource_IsNotRemovedByTheCleanup()
    {
        var onlySource = SeedCollection("Photos", "Custom", "materialized", "Manual");
        SeedViewSource(onlySource);

        var removed = await new CollectionRepository(_db).DeleteEmptyCuratedCollectionsAsync();

        Assert.Empty(removed);
        Assert.True(CollectionExists(onlySource));
    }

    [Fact]
    public async Task RemovingTheLastWorkItemOfACuratedCollection_IsRefusedAndTheItemStays()
    {
        var work = SeedWork("Only").WorkId;
        var collectionId = SeedCollection("Just one", "Custom", "materialized", "Manual", work);
        var itemId = ItemIds(collectionId).Single();

        var removed = await new CollectionRepository(_db).RemoveCollectionItemUnlessLastMemberAsync(collectionId, itemId);

        Assert.False(removed);
        Assert.Equal(1, ItemCount(collectionId));
    }

    [Fact]
    public async Task RemovingTheLastWorkItemOfACuratedCollection_IsAllowedWhenAViewSourceRemains()
    {
        var work = SeedWork("Only").WorkId;
        var collectionId = SeedCollection("Film and photos", "Custom", "materialized", "Manual", work);
        SeedViewSource(collectionId);
        var itemId = ItemIds(collectionId).Single();

        var removed = await new CollectionRepository(_db).RemoveCollectionItemUnlessLastMemberAsync(collectionId, itemId);

        Assert.True(removed);
        Assert.Equal(0, ItemCount(collectionId));
        Assert.Empty(await new CollectionRepository(_db).DeleteEmptyCuratedCollectionsAsync());
        Assert.True(CollectionExists(collectionId));
    }

    [Fact]
    public async Task RemovingOneOfTwoWorkItems_LeavesTheOtherAndSucceeds()
    {
        var collectionId = SeedCollection("Two", "Custom", "materialized", "Manual", SeedWork("A").WorkId, SeedWork("B").WorkId);
        var repository = new CollectionRepository(_db);
        var items = ItemIds(collectionId);

        Assert.True(await repository.RemoveCollectionItemUnlessLastMemberAsync(collectionId, items[0]));
        Assert.False(await repository.RemoveCollectionItemUnlessLastMemberAsync(collectionId, items[1]));

        Assert.Equal([items[1]], ItemIds(collectionId));
    }

    [Fact]
    public async Task SwitchingARuleDrivenCollectionToManual_SavesTheStagedItemsInTheSameWrite()
    {
        var first = SeedWork("First").WorkId;
        var second = SeedWork("Second").WorkId;
        var collectionId = SeedCollection("Everything new", "Custom", "query", "Smart");
        var repository = new CollectionRepository(_db);
        var collection = (await repository.GetByIdAsync(collectionId))!;
        collection.ChangeResolution(CollectionResolution.Materialized);
        collection.MembershipMode = ContainerMembershipMode.Manual;

        var saved = await repository.UpdateManagedCollectionAsync(
            collection,
            [NewItem(collectionId, first), NewItem(collectionId, second), NewItem(collectionId, first)],
            requireMember: true);

        Assert.True(saved);
        var stored = (await repository.GetByIdAsync(collectionId))!;
        Assert.Equal(ContainerMembershipMode.Manual, stored.MembershipMode);
        Assert.Equal(CollectionResolution.Materialized, stored.Resolution);
        using var verify = _db.CreateConnection();
        Assert.Equal(
            [first, second],
            verify.Query<Guid>(
                "SELECT work_id FROM collection_items WHERE collection_id = @collectionId ORDER BY sort_order",
                new { collectionId }).ToList());
    }

    [Fact]
    public async Task SwitchingARuleDrivenCollectionToManualWithNothingInIt_WritesNothingAndIsRefused()
    {
        var collectionId = SeedCollection("Everything new", "Custom", "query", "Smart");
        var repository = new CollectionRepository(_db);
        var collection = (await repository.GetByIdAsync(collectionId))!;
        collection.ChangeResolution(CollectionResolution.Materialized);
        collection.MembershipMode = ContainerMembershipMode.Manual;

        var saved = await repository.UpdateManagedCollectionAsync(collection, [], requireMember: true);

        Assert.False(saved);
        var stored = (await repository.GetByIdAsync(collectionId))!;
        Assert.Equal(ContainerMembershipMode.Smart, stored.MembershipMode);
        Assert.Equal(CollectionResolution.Query, stored.Resolution);
    }

    [Fact]
    public async Task SwitchingARuleDrivenCollectionToManual_IsAllowedWhenAViewSourceIsAttached()
    {
        var collectionId = SeedCollection("Everything new", "Custom", "query", "Smart");
        SeedViewSource(collectionId);
        var repository = new CollectionRepository(_db);
        var collection = (await repository.GetByIdAsync(collectionId))!;
        collection.ChangeResolution(CollectionResolution.Materialized);
        collection.MembershipMode = ContainerMembershipMode.Manual;

        Assert.True(await repository.UpdateManagedCollectionAsync(collection, [], requireMember: true));
        Assert.Equal(ContainerMembershipMode.Manual, (await repository.GetByIdAsync(collectionId))!.MembershipMode);
    }

    private static CollectionItem NewItem(Guid collectionId, Guid workId) => new()
    {
        Id = Guid.NewGuid(),
        CollectionId = collectionId,
        WorkId = workId,
        AddedAt = DateTimeOffset.UtcNow,
    };

    private void SeedViewSource(Guid collectionId)
    {
        var profileId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@profileId, 'Owner', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
            INSERT INTO collection_view_sources
                (id, collection_id, owner_profile_id, source_kind, rule_version, rule_json, position, created_at, updated_at)
            VALUES (@id, @collectionId, @profileId, 'smart_rule', 1, '{}', 0, @now, @now);
            """, new { id = Guid.NewGuid(), collectionId, profileId, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private List<Guid> ItemIds(Guid collectionId)
    {
        using var connection = _db.CreateConnection();
        return connection.Query<Guid>(
            "SELECT id FROM collection_items WHERE collection_id = @collectionId ORDER BY sort_order",
            new { collectionId }).ToList();
    }

    private (Guid WorkId, Guid AssetId) SeedWork(string title)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership)
            VALUES (@workId, 'Movies', 'standalone', 'Owned');
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, @now);
            """, new
        {
            workId,
            editionId,
            assetId,
            hash = Guid.NewGuid().ToString("N"),
            path = $"C:/library/{assetId:N}.mkv",
            title,
            now = DateTimeOffset.UtcNow.ToString("O"),
        });
        return (workId, assetId);
    }

    private Guid SeedWorkWithoutEdition(string title)
    {
        var workId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership)
            VALUES (@workId, 'Movies', 'standalone', 'Owned');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@workId, 'title', @title, @now);
            """, new { workId, title, now = DateTimeOffset.UtcNow.ToString("O") });
        return workId;
    }

    private Guid SeedCollection(string name, string type, string resolution, string membership, params Guid[] workIds)
    {
        var collectionId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
            VALUES (@collectionId, @name, @type, 'library', @resolution, @membership, 'Library', 'Everyone');
            """, new { collectionId, name, type, resolution, membership });
        var order = 0;
        foreach (var workId in workIds)
        {
            connection.Execute("""
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                VALUES (@id, @collectionId, @workId, @order);
                """, new { id = Guid.NewGuid(), collectionId, workId, order = ++order });
        }

        return collectionId;
    }

    private bool CollectionExists(Guid collectionId)
    {
        using var connection = _db.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM collections WHERE id = @collectionId", new { collectionId }) == 1;
    }

    private int ItemCount(Guid collectionId)
    {
        using var connection = _db.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM collection_items WHERE collection_id = @collectionId", new { collectionId });
    }

    public void Dispose()
    {
        _db.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_dbPath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        File.Delete(_dbPath);
    }
}
