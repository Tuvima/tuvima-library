using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

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
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
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
    public async Task UpsertAsync_RoundTripsTheUniverseQidOfAShelf()
    {
        var repo = new CollectionRepository(_db);
        await EnsureUniverseRecordAsync("Q18011049");
        var shelf = CreateCollection("Dune book series", "ContentGroup");
        shelf.UniverseQid = "Q18011049";

        await repo.UpsertAsync(shelf);

        var saved = await repo.GetByIdAsync(shelf.Id);
        Assert.Equal("Q18011049", saved!.UniverseQid);
        var all = await repo.GetAllAsync();
        Assert.Equal("Q18011049", all.Single(candidate => candidate.Id == shelf.Id).UniverseQid);
    }

    [Fact]
    public async Task SetUniverseAsync_SetsTheUniverseRecordAndTheUniverseShelfTogether()
    {
        var repo = new CollectionRepository(_db);
        await EnsureUniverseRecordAsync("Q18011049");
        var universe = CreateUniverseShelf("Dune", "Q18011049");
        var shelf = CreateCollection("Dune book series", "ContentGroup");
        await repo.UpsertAsync(universe);
        await repo.UpsertAsync(shelf);

        await repo.SetUniverseAsync(shelf.Id, "Q18011049", universe.Id);

        var saved = await repo.GetByIdAsync(shelf.Id);
        Assert.Equal("Q18011049", saved!.UniverseQid);
        Assert.Equal(universe.Id, saved.ParentCollectionId);

        await repo.SetUniverseAsync(shelf.Id, null, null);

        saved = await repo.GetByIdAsync(shelf.Id);
        Assert.Null(saved!.UniverseQid);
        Assert.Null(saved.ParentCollectionId);
    }

    [Fact]
    public async Task UniverseShelfLookups_SeparateTheUniverseShelfFromItsSeriesShelves()
    {
        var repo = new CollectionRepository(_db);
        await EnsureUniverseRecordAsync("Q18011049");
        var universe = CreateUniverseShelf("Dune", "Q18011049");
        var books = CreateCollection("Dune book series", "ContentGroup");
        books.UniverseQid = "Q18011049";
        var movies = CreateCollection("Dune film series", "ContentGroup");
        movies.UniverseQid = "Q18011049";
        await repo.UpsertAsync(universe);
        await repo.UpsertAsync(books);
        await repo.UpsertAsync(movies);

        var found = await repo.FindUniverseShelfByUniverseQidAsync("Q18011049");
        var series = await repo.ListSeriesShelvesByUniverseQidAsync("Q18011049");

        Assert.Equal(universe.Id, found!.Id);
        Assert.Equal(2, series.Count);
        Assert.Contains(series, shelf => shelf.Id == books.Id);
        Assert.Contains(series, shelf => shelf.Id == movies.Id);
    }

    [Fact]
    public async Task UniverseShelfIndex_RejectsASecondUniverseShelfForOneUniverseRecord()
    {
        var repo = new CollectionRepository(_db);
        await EnsureUniverseRecordAsync("Q18011049");
        await repo.UpsertAsync(CreateUniverseShelf("Dune", "Q18011049"));

        // The unique index refuses the second row: a direct insert fails, and the repository's insert-or-ignore stores nothing.
        using (var conn = _db.CreateConnection())
        {
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => conn.Execute(
                "INSERT INTO collections (id, collection_type, universe_qid) VALUES (@id, 'Universe', 'Q18011049');",
                new { id = Guid.NewGuid() }));
        }

        var second = CreateUniverseShelf("Dune again", "Q18011049");
        await repo.UpsertAsync(second);
        Assert.Null(await repo.GetByIdAsync(second.Id));
        Assert.Single(await repo.GetByTypeAsync("Universe"));

        // Series shelves may share a Universe record freely.
        var books = CreateCollection("Dune book series", "ContentGroup");
        books.UniverseQid = "Q18011049";
        var movies = CreateCollection("Dune film series", "ContentGroup");
        movies.UniverseQid = "Q18011049";
        await repo.UpsertAsync(books);
        await repo.UpsertAsync(movies);
        Assert.Equal(2, (await repo.ListSeriesShelvesByUniverseQidAsync("Q18011049")).Count);
    }

    [Fact]
    public async Task FindSeriesShelvesByQidAsync_ReturnsSeriesShelvesOfEveryMediaTypeButNeverTheUniverseShelf()
    {
        var repo = new CollectionRepository(_db);
        var books = CreateCollection("The Expanse", "ContentGroup");
        books.WikidataQid = "Q19610143";
        books.MediaType = "Books";
        var audiobooks = CreateCollection("The Expanse (audio)", "ContentGroup");
        audiobooks.WikidataQid = "Q19610143";
        audiobooks.MediaType = "Audiobooks";
        var universe = CreateCollection("The Expanse", "Universe");
        universe.WikidataQid = "Q19610143";
        await repo.UpsertAsync(books);
        await repo.UpsertAsync(audiobooks);
        await repo.UpsertAsync(universe);

        var shelves = await repo.FindSeriesShelvesByQidAsync("Q19610143");

        Assert.Equal(2, shelves.Count);
        Assert.Contains(shelves, shelf => shelf.Id == books.Id);
        Assert.Contains(shelves, shelf => shelf.Id == audiobooks.Id);
    }

    [Fact]
    public async Task ListShelvesWithRollupRelationshipQidAsync_ReturnsSeriesShelvesThatNameTheQid()
    {
        var repo = new CollectionRepository(_db);
        var books = CreateCollection("The Expanse", "ContentGroup");
        var television = CreateCollection("The Expanse TV", "ContentGroup");
        var unrelated = CreateCollection("Dune", "ContentGroup");
        var universe = CreateCollection("The Expanse universe", "Universe");
        await repo.UpsertAsync(books);
        await repo.UpsertAsync(television);
        await repo.UpsertAsync(unrelated);
        await repo.UpsertAsync(universe);
        await repo.InsertRelationshipsAsync(
        [
            CreateRelationship(books.Id, "series", "Q19610143", "The Expanse"),
            CreateRelationship(television.Id, "based_on", "Q19610143", "The Expanse"),
            CreateRelationship(universe.Id, "series", "Q19610143", "The Expanse"),
            CreateRelationship(unrelated.Id, "franchise", "Q6095696", "Dune"),
        ]);

        var shelves = await repo.ListShelvesWithRollupRelationshipQidAsync("Q19610143");

        Assert.Equal(2, shelves.Count);
        Assert.Contains(shelves, shelf => shelf.Id == books.Id);
        Assert.Contains(shelves, shelf => shelf.Id == television.Id);
    }

    [Fact]
    public async Task DeleteUnusedUniverseShelfAsync_DeletesOnlyAnUneditedShelfWithNoChildren()
    {
        var repo = new CollectionRepository(_db);
        await EnsureUniverseRecordAsync("Q1");
        await EnsureUniverseRecordAsync("Q2");
        await EnsureUniverseRecordAsync("Q3");
        var empty = CreateUniverseShelf("Empty", "Q1");
        var edited = CreateUniverseShelf("Edited", "Q2");
        edited.Description = "Written by the owner.";
        var withChild = CreateUniverseShelf("With child", "Q3");
        var child = CreateCollection("Child", "ContentGroup");
        child.ParentCollectionId = withChild.Id;
        await repo.UpsertAsync(empty);
        await repo.UpsertAsync(edited);
        await repo.UpsertAsync(withChild);
        await repo.UpsertAsync(child);

        Assert.True(await repo.DeleteUnusedUniverseShelfAsync(empty.Id));
        Assert.False(await repo.DeleteUnusedUniverseShelfAsync(edited.Id));
        Assert.False(await repo.DeleteUnusedUniverseShelfAsync(withChild.Id));
        Assert.False(await repo.DeleteUnusedUniverseShelfAsync(child.Id));

        Assert.Null(await repo.GetByIdAsync(empty.Id));
        Assert.NotNull(await repo.GetByIdAsync(edited.Id));
        Assert.NotNull(await repo.GetByIdAsync(withChild.Id));
        Assert.NotNull(await repo.GetByIdAsync(child.Id));
    }

    [Fact]
    public async Task FindSeriesShelfForWorkTreeAsync_FindsTheShelfOfAnEpisodeFromItsShow()
    {
        var repo = new CollectionRepository(_db);
        var shelf = CreateCollection("The Expanse", "ContentGroup");
        await repo.UpsertAsync(shelf);
        var showWorkId = Guid.NewGuid();
        var seasonWorkId = Guid.NewGuid();
        var episodeWorkId = Guid.NewGuid();
        InsertWork(showWorkId, shelf.Id, mediaType: "TV");
        InsertWork(seasonWorkId, shelf.Id, showWorkId, mediaType: "TV");
        InsertWork(episodeWorkId, shelf.Id, seasonWorkId, mediaType: "TV");

        var found = await repo.FindSeriesShelfForWorkTreeAsync(showWorkId);
        var roots = await repo.GetRootWorkIdsOnShelfAsync(shelf.Id);

        Assert.Equal(shelf.Id, found!.Id);
        Assert.Equal([showWorkId], roots);
        Assert.Null(await repo.FindSeriesShelfForWorkTreeAsync(Guid.NewGuid()));
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

    private static Collection CreateUniverseShelf(string name, string universeQid)
    {
        var shelf = CreateCollection(name, "Universe");
        shelf.WikidataQid = universeQid;
        shelf.UniverseQid = universeQid;
        return shelf;
    }

    private Task EnsureUniverseRecordAsync(string qid) =>
        new NarrativeRootRepository(_db).EnsureAsync(new MediaEngine.Domain.Models.NarrativeRoot
        {
            Qid = qid,
            Label = qid,
            Level = NarrativeLevel.Universe,
        });

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
        cmd.CommandText = "INSERT INTO works (id, collection_id, parent_work_id, media_type, ready_at) VALUES ($id, $collectionId, $parentWorkId, $mediaType, '2026-01-01T00:00:00.0000000+00:00')";
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
