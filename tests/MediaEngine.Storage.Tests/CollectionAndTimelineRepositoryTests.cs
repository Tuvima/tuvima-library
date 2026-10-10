using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class CollectionAndTimelineRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public CollectionAndTimelineRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_timeline_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task GetByIdsAsync_PreservesRequestedOrderAndOmitsMissingAndDuplicateIds()
    {
        var repository = new CollectionRepository(_db);
        var first = CreateCollection("First");
        var second = CreateCollection("Second");
        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);

        var collections = await repository.GetByIdsAsync(
            [second.Id, Guid.NewGuid(), first.Id, second.Id]);

        Assert.Equal([second.Id, first.Id], collections.Select(collection => collection.Id));
        Assert.Equal(["Second", "First"], collections.Select(collection => collection.DisplayName));
    }

    [Fact]
    public async Task GetCollectionsWithWorksAsync_PreservesOrderAndHydratesCanonicalValues()
    {
        var repository = new CollectionRepository(_db);
        var first = CreateCollection("First");
        var second = CreateCollection("Second");
        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);

        var firstWorkId = InsertWork(first.Id, "Books", 2);
        var secondWorkId = InsertWork(second.Id, "Movies", 1);
        InsertCanonicalValue(firstWorkId, "title", "First Work");
        InsertCanonicalValue(secondWorkId, "title", "Second Work");

        var collections = await repository.GetCollectionsWithWorksAsync(
            [second.Id, Guid.NewGuid(), first.Id]);

        Assert.Equal([second.Id, first.Id], collections.Select(collection => collection.Id));
        Assert.Equal(secondWorkId, Assert.Single(collections[0].Works).Id);
        Assert.Equal(firstWorkId, Assert.Single(collections[1].Works).Id);
        Assert.Equal(
            "Second Work",
            Assert.Single(collections[0].Works[0].CanonicalValues, value => value.Key == "title").Value);
        Assert.Equal(
            "First Work",
            Assert.Single(collections[1].Works[0].CanonicalValues, value => value.Key == "title").Value);
    }

    [Fact]
    public async Task CreateManagedCollectionAsync_PersistsDefinitionAndInitialMembershipTogether()
    {
        var repository = new CollectionRepository(_db);
        var collection = CreateCollection("Weekend Watchlist");
        collection.ClassifyAs(CollectionType.Playlist);
        var workId = InsertWork(collectionId: null, "Movies", 1);

        await repository.CreateManagedCollectionAsync(collection,
        [
            new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = collection.Id,
                WorkId = workId,
                SortOrder = 1,
                AddedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var saved = await repository.GetByIdAsync(collection.Id);
        var items = await repository.GetCollectionItemsAsync(collection.Id, 10);

        Assert.NotNull(saved);
        Assert.Equal(CollectionType.Playlist, saved.CollectionType);
        Assert.Equal(workId, Assert.Single(items).WorkId);
    }

    [Fact]
    public async Task SelectedProfileAudience_RoundTripsWithManagedCollections()
    {
        var repository = new CollectionRepository(_db);
        var collection = CreateCollection("Family picks");
        collection.ClassifyAs(CollectionType.Custom);
        collection.OwnerKind = ContainerOwnerKind.Profile;
        collection.Audience = ContainerAudience.SelectedProfiles;
        var profileId = InsertProfile("Family member");

        await repository.CreateManagedCollectionAsync(collection, []);
        await repository.ReplaceAudienceProfileIdsAsync(collection.Id, [profileId]);

        var saved = await repository.GetByIdAsync(collection.Id);
        var listed = Assert.Single(await repository.GetManagedCollectionsAsync());

        Assert.Equal(profileId, Assert.Single(saved!.AudienceProfileIds));
        Assert.Equal(profileId, Assert.Single(listed.AudienceProfileIds));
    }

    [Fact]
    public async Task EntityTimelineRepository_RoundTripsEventsAndFieldChanges()
    {
        var repository = new EntityTimelineRepository(_db);
        var entityId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var evt = new EntityEvent
        {
            Id = eventId,
            EntityId = entityId,
            EntityType = "Work",
            EventType = "wikidata_bridge_resolved",
            Stage = 2,
            Trigger = "ingestion",
            ResolvedQid = "Q42",
            Confidence = 0.95,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var change = new EntityFieldChange
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            EntityId = entityId,
            Field = "title",
            OldValue = "Old",
            NewValue = "New",
            Confidence = 0.95,
            IsFileOriginal = true,
        };

        await repository.InsertEventsAsync([evt]);
        await repository.InsertFieldChangesAsync([change]);

        var events = await repository.GetEventsByEntityAsync(entityId);
        var latest = await repository.GetLatestStage2EventsAsync([entityId]);
        var changes = await repository.GetFieldChangesByEventAsync(eventId);

        Assert.Equal(eventId, Assert.Single(events).Id);
        Assert.Equal("Q42", latest[entityId].ResolvedQid);
        Assert.Equal(change.Id, Assert.Single(changes).Id);
        Assert.True(changes[0].IsFileOriginal);
    }

    [Fact]
    public async Task RepositoryReads_ObservePreCancelledTokens()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var collections = new CollectionRepository(_db);
        var timeline = new EntityTimelineRepository(_db);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => collections.GetByIdsAsync([Guid.NewGuid()], source.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => timeline.GetEventsByEntityAsync(Guid.NewGuid(), source.Token));
    }

    private static Collection CreateCollection(string displayName)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);
        return collection;
    }

    private Guid InsertWork(Guid? collectionId, string mediaType, int ordinal)
    {
        var workId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, collection_id, media_type, ordinal)
            VALUES (@id, @collectionId, @mediaType, @ordinal);
            """;
        cmd.Parameters.Add("@id", SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        cmd.Parameters.Add("@collectionId", SqliteType.Blob).Value = collectionId.HasValue ? GuidSql.ToBlob(collectionId.Value) : DBNull.Value;
        cmd.Parameters.AddWithValue("@mediaType", mediaType);
        cmd.Parameters.AddWithValue("@ordinal", ordinal);
        cmd.ExecuteNonQuery();
        return workId;
    }

    private Guid InsertProfile(string displayName)
    {
        var profileId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles (id, display_name, created_at)
            VALUES (@id, @displayName, @createdAt);
            """;
        cmd.Parameters.Add("@id", SqliteType.Blob).Value = GuidSql.ToBlob(profileId);
        cmd.Parameters.AddWithValue("@displayName", displayName);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return profileId;
    }

    private void InsertCanonicalValue(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@entityId, @key, @value, @lastScoredAt);
            """;
        cmd.Parameters.Add("@entityId", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@lastScoredAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
