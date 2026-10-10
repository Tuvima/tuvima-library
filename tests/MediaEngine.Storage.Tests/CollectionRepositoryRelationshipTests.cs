using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class CollectionRepositoryRelationshipTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public CollectionRepositoryRelationshipTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_rel_{Guid.NewGuid():N}.db");
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
    public async Task InsertRelationshipsAsync_RoundTripsRows()
    {
        var repo = new CollectionRepository(_db);
        var collection = CreateCollection("Dune");

        await repo.UpsertAsync(collection);

        var relationship = new CollectionRelationship
        {
            Id = Guid.NewGuid(),
            CollectionId = collection.Id,
            RelType = "franchise",
            RelQid = "Q6095696",
            RelLabel = "Dune",
            Confidence = 1.0,
            DiscoveredAt = DateTimeOffset.UtcNow,
        };

        await repo.InsertRelationshipsAsync([relationship]);

        var saved = await repo.GetRelationshipsAsync(collection.Id);
        var row = Assert.Single(saved);
        Assert.Equal(relationship.Id, row.Id);
        Assert.Equal(relationship.CollectionId, row.CollectionId);
        Assert.Equal(relationship.RelType, row.RelType);
        Assert.Equal(relationship.RelQid, row.RelQid);
        Assert.Equal(relationship.RelLabel, row.RelLabel);
    }

    [Fact]
    public async Task FindParentCollectionByRelationshipAsync_IgnoresContentGroups()
    {
        var repo = new CollectionRepository(_db);
        var contentGroup = CreateCollection("The Expanse", "ContentGroup");

        await repo.UpsertAsync(contentGroup);
        await repo.InsertRelationshipsAsync([CreateRelationship(contentGroup.Id, "franchise", "Q19610143", "The Expanse")]);

        var parent = await repo.FindParentCollectionByRelationshipAsync("Q19610143");
        Assert.Null(parent);
    }

    [Fact]
    public async Task FindParentCollectionByRelationshipAsync_ReturnsUniverseParent()
    {
        var repo = new CollectionRepository(_db);
        var contentGroup = CreateCollection("The Expanse", "ContentGroup");
        var parentUniverse = CreateCollection("The Expanse Universe", "Universe");

        await repo.UpsertAsync(contentGroup);
        await repo.UpsertAsync(parentUniverse);
        await repo.InsertRelationshipsAsync(
        [
            CreateRelationship(contentGroup.Id, "franchise", "Q19610143", "The Expanse"),
            CreateRelationship(parentUniverse.Id, "franchise", "Q19610143", "The Expanse"),
        ]);

        var parent = await repo.FindParentCollectionByRelationshipAsync("Q19610143");
        Assert.NotNull(parent);
        Assert.Equal(parentUniverse.Id, parent!.Id);
    }

    [Fact]
    public async Task RelationshipRollupQueries_IncludeBroaderSeriesRelationshipsForShelfCollections()
    {
        var repo = new CollectionRepository(_db);
        var lotr = CreateCollection("The Lord of the Rings", "ContentGroup");
        var hobbit = CreateCollection("The Hobbit trilogy", "ContentGroup");
        var parentUniverse = CreateCollection("Peter Jackson's Middle-earth film series", "Universe");

        await repo.UpsertAsync(lotr);
        await repo.UpsertAsync(hobbit);
        await repo.UpsertAsync(parentUniverse);
        await repo.InsertRelationshipsAsync(
        [
            CreateRelationship(lotr.Id, "series", "Q26214973", "Peter Jackson's Middle-earth film series"),
            CreateRelationship(hobbit.Id, "series", "Q26214973", "Peter Jackson's Middle-earth film series"),
            CreateRelationship(parentUniverse.Id, "series", "Q26214973", "Peter Jackson's Middle-earth film series"),
        ]);

        var shelfIds = await repo.FindCollectionIdsByFranchiseQidAsync("Q26214973");
        var parent = await repo.FindParentCollectionByRelationshipAsync("Q26214973");

        Assert.Contains(lotr.Id, shelfIds);
        Assert.Contains(hobbit.Id, shelfIds);
        Assert.DoesNotContain(parentUniverse.Id, shelfIds);
        Assert.NotNull(parent);
        Assert.Equal(parentUniverse.Id, parent!.Id);
    }

    [Fact]
    public async Task RelationshipRollupQueries_IncludeBasedOnRelationshipsForCrossMediaShelves()
    {
        var repo = new CollectionRepository(_db);
        var comic = CreateCollection("Batman", "ContentGroup");
        var movie = CreateCollection("Batman in film", "ContentGroup");
        var parentUniverse = CreateCollection("Batman", "Universe");

        await repo.UpsertAsync(comic);
        await repo.UpsertAsync(movie);
        await repo.UpsertAsync(parentUniverse);
        await repo.InsertRelationshipsAsync(
        [
            CreateRelationship(comic.Id, "based_on", "Q2695156", "Batman"),
            CreateRelationship(movie.Id, "based_on", "Q2695156", "Batman"),
            CreateRelationship(parentUniverse.Id, "based_on", "Q2695156", "Batman"),
        ]);

        var shelfIds = await repo.FindCollectionIdsByFranchiseQidAsync("Q2695156");
        var parent = await repo.FindParentCollectionByRelationshipAsync("Q2695156");

        Assert.Contains(comic.Id, shelfIds);
        Assert.Contains(movie.Id, shelfIds);
        Assert.DoesNotContain(parentUniverse.Id, shelfIds);
        Assert.NotNull(parent);
        Assert.Equal(parentUniverse.Id, parent!.Id);
    }

    [Fact]
    public async Task UpdateCollectionArtworkAsync_RoundTripsMetadata()
    {
        var repo = new CollectionRepository(_db);
        var collection = CreateCollection("Road Trip Mix", "Playlist");
        collection.SortField = "year";
        collection.SecondarySortField = "title";
        collection.SecondarySortDirection = MediaEngine.Domain.Enums.CollectionSortDirection.Asc;

        await repo.UpsertAsync(collection);
        await repo.UpdateCollectionArtworkAsync(collection.Id, "poster", @"C:\Tuvima\collections\road-trip.jpg", "image/jpeg");
        await repo.UpdateCollectionArtworkAsync(collection.Id, "background", @"C:\Tuvima\collections\road-trip-background.jpg", "image/jpeg");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.UpdateCollectionArtworkAsync(
            collection.Id,
            "banner",
            @"C:\Tuvima\collections\road-trip-banner.jpg",
            "image/jpeg"));

        var saved = await repo.GetByIdAsync(collection.Id);

        Assert.NotNull(saved);
        Assert.Equal(@"C:\Tuvima\collections\road-trip.jpg", saved!.CoverArtworkPath);
        Assert.Equal("image/jpeg", saved.CoverArtworkMimeType);
        Assert.Equal(@"C:\Tuvima\collections\road-trip-background.jpg", saved.BackgroundArtworkPath);
        Assert.Null(saved.BannerArtworkPath);
        Assert.Equal("title", saved.SecondarySortField);
        Assert.Equal(MediaEngine.Domain.Enums.CollectionSortDirection.Asc, saved.SecondarySortDirection);
    }

    [Fact]
    public async Task GetCollectionItemCountsAsync_ReturnsCountsForMultipleCollectionsInOneCall()
    {
        var repo = new CollectionRepository(_db);
        var first = CreateCollection("Road Trip Mix", "Playlist");
        var second = CreateCollection("Night Queue", "Playlist");

        await repo.UpsertAsync(first);
        await repo.UpsertAsync(second);

        var firstWorkA = Guid.NewGuid();
        var firstWorkB = Guid.NewGuid();
        var secondWork = Guid.NewGuid();
        InsertWork(firstWorkA, first.Id);
        InsertWork(firstWorkB, first.Id);
        InsertWork(secondWork, second.Id);

        await repo.AddCollectionItemAsync(CreateCollectionItem(first.Id, firstWorkA, 0));
        await repo.AddCollectionItemAsync(CreateCollectionItem(first.Id, firstWorkB, 1));
        await repo.AddCollectionItemAsync(CreateCollectionItem(second.Id, secondWork, 0));

        var counts = await repo.GetCollectionItemCountsAsync([first.Id, second.Id, Guid.NewGuid()]);

        Assert.Equal(2, counts[first.Id]);
        Assert.Equal(1, counts[second.Id]);
        Assert.Contains(counts, pair => pair.Value == 0);
    }

    [Fact]
    public async Task WorkLineageLookups_RoundTripGuidBlobAssetRelationships()
    {
        var repo = new CollectionRepository(_db);
        var collection = CreateCollection("The Matrix", "ContentGroup");
        await repo.UpsertAsync(collection);

        var rootWork = Guid.NewGuid();
        var parentWork = Guid.NewGuid();
        var leafWork = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        InsertWork(rootWork, collection.Id);
        InsertWork(parentWork, collection.Id, rootWork);
        InsertWork(leafWork, collection.Id, parentWork);
        InsertEditionAndAsset(editionId, leafWork, assetId);

        var resolvedWorkId = await repo.GetWorkIdByMediaAssetAsync(assetId);
        var lineage = await repo.GetWorkLineageIdsByMediaAssetAsync(assetId);

        Assert.Equal(leafWork, resolvedWorkId);
        Assert.Equal([leafWork, parentWork, rootWork], lineage);
    }

    [Fact]
    public async Task GetContentGroupsAsync_LoadsCanonicalValuesFromAssetLeafParentAndRoot()
    {
        var repo = new CollectionRepository(_db);
        var collection = CreateCollection("The Expanse", "ContentGroup");
        await repo.UpsertAsync(collection);

        var showWorkId = Guid.NewGuid();
        var seasonWorkId = Guid.NewGuid();
        var episodeWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        InsertWork(showWorkId, collection.Id, mediaType: "TV");
        InsertWork(seasonWorkId, collection.Id, showWorkId, mediaType: "TV");
        InsertWork(episodeWorkId, collection.Id, seasonWorkId, mediaType: "TV");
        InsertEditionAndAsset(editionId, episodeWorkId, assetId);

        InsertCanonicalValue(showWorkId, "show_name", "The Expanse");
        InsertCanonicalValue(seasonWorkId, "season_number", "1");
        InsertCanonicalValue(episodeWorkId, "episode_title", "Dulcinea");
        InsertCanonicalValue(assetId, "duration_seconds", "2700");

        var groups = await repo.GetContentGroupsAsync();

        var group = Assert.Single(groups);
        var episode = Assert.Single(group.Works);
        Assert.Equal(episodeWorkId, episode.Id);
        Assert.Contains(episode.CanonicalValues, value => value.EntityId == showWorkId && value.Key == "show_name" && value.Value == "The Expanse");
        Assert.Contains(episode.CanonicalValues, value => value.EntityId == seasonWorkId && value.Key == "season_number" && value.Value == "1");
        Assert.Contains(episode.CanonicalValues, value => value.EntityId == episodeWorkId && value.Key == "episode_title" && value.Value == "Dulcinea");
        Assert.Contains(episode.CanonicalValues, value => value.EntityId == assetId && value.Key == "duration_seconds" && value.Value == "2700");
    }

    [Fact]
    public async Task GetContentGroupsAsync_IncludesStructuralSeriesCollections()
    {
        var repo = new CollectionRepository(_db);
        var collection = CreateCollection("Dune", "Series");
        await repo.UpsertAsync(collection);

        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        InsertWork(workId, collection.Id, mediaType: "Books");
        InsertEditionAndAsset(editionId, workId, assetId);

        var group = Assert.Single(await repo.GetContentGroupsAsync());

        Assert.Equal(collection.Id, group.Id);
        Assert.Equal("Dune", group.DisplayName);
        Assert.Equal(workId, Assert.Single(group.Works).Id);
    }

    private static Collection CreateCollection(string name, string type = "Universe")
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            AggregateStateSerializer.ParseCollectionType(type),
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);
        return collection;
    }

    private static CollectionRelationship CreateRelationship(
        Guid collectionId,
        string relType,
        string qid,
        string label) => new()
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            RelType = relType,
            RelQid = qid,
            RelLabel = label,
            Confidence = 1.0,
            DiscoveredAt = DateTimeOffset.UtcNow,
        };

    private static CollectionItem CreateCollectionItem(Guid collectionId, Guid workId, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        CollectionId = collectionId,
        WorkId = workId,
        SortOrder = sortOrder,
        AddedAt = DateTimeOffset.UtcNow,
    };

    private void InsertWork(Guid workId, Guid collectionId, Guid? parentWorkId = null, string mediaType = "Music")
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO works (id, collection_id, parent_work_id, media_type) VALUES ($id, $collectionId, $parentWorkId, $mediaType)";
        cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        cmd.Parameters.Add("$collectionId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(collectionId);
        cmd.Parameters.Add("$parentWorkId", Microsoft.Data.Sqlite.SqliteType.Blob).Value =
            parentWorkId.HasValue ? GuidSql.ToBlob(parentWorkId.Value) : DBNull.Value;
        cmd.Parameters.AddWithValue("$mediaType", mediaType);
        cmd.ExecuteNonQuery();
    }

    private void InsertEditionAndAsset(Guid editionId, Guid workId, Guid assetId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO editions (id, work_id) VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES ($assetId, $editionId, $hash, $path);
            """;
        cmd.Parameters.Add("$editionId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(editionId);
        cmd.Parameters.Add("$workId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        cmd.Parameters.Add("$assetId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
        cmd.Parameters.AddWithValue("$hash", $"asset-{assetId:N}");
        cmd.Parameters.AddWithValue("$path", $"C:/library/{assetId:N}.mkv");
        cmd.ExecuteNonQuery();
    }

    private void InsertCanonicalValue(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES ($entityId, $key, $value, $lastScoredAt);
            """;
        cmd.Parameters.Add("$entityId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$lastScoredAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
