using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class SeriesManifestRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public SeriesManifestRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_manifest_{Guid.NewGuid():N}.db");
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
    public async Task UpsertManifest_PersistsNamedOwnedAndMissingItems()
    {
        var repo = new SeriesManifestRepository(_db);
        var collectionId = await CreateCollectionAsync("QSeries", "Dune");
        var ownedWorkId = await CreateWorkAsync("QOwned");
        var now = DateTimeOffset.UtcNow;

        var items = new List<SeriesManifestItemRecord>
        {
            Item(collectionId, "QSeries", "QOwned", "Dune", 1, "Owned", ownedWorkId, now),
            Item(collectionId, "QSeries", "QMissing", "Dune Messiah", 2, "Missing", null, now),
        };

        await repo.UpsertManifestAsync(new SeriesManifestHydration
        {
            SeriesQid = "QSeries",
            CollectionId = collectionId,
            SeriesLabel = "Dune",
            ManifestHash = "manifest-hash",
            KnownItemQidsHash = "qid-hash",
            WarningsJson = "[]",
            ApiMetadataJson = "{}",
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        }, items);
        await repo.LinkOwnedWorksAsync(collectionId, items);

        var view = await repo.GetViewByCollectionIdAsync(collectionId);

        Assert.NotNull(view);
        Assert.Equal(2, view.TotalCount);
        Assert.Equal(1, view.OwnedCount);
        Assert.Equal(1, view.MissingCount);
        Assert.Contains(view.Items, i => i.ItemQid == "QOwned" && i.ItemLabel == "Dune" && i.LinkedWorkId == ownedWorkId);
        Assert.Contains(view.Items, i => i.ItemQid == "QMissing" && i.ItemLabel == "Dune Messiah" && i.OwnershipState == "Missing");
        Assert.Equal(1, CountRows("collection_items"));
        Assert.Equal(0, CountRows("media_assets"));
    }

    [Fact]
    public async Task FindWorkIdsByQids_ReturnsAllMatchesForAmbiguousLocalQid()
    {
        var repo = new SeriesManifestRepository(_db);
        var first = await CreateWorkAsync("QDuplicate");
        var second = await CreateWorkAsync("QDuplicate");

        var matches = await repo.FindWorkIdsByQidsAsync(["QDuplicate"]);

        Assert.True(matches.TryGetValue("QDuplicate", out var workIds));
        Assert.Contains(first, workIds);
        Assert.Contains(second, workIds);
        Assert.Equal(2, workIds.Count);
    }

    [Fact]
    public async Task FindWorkIdsByExternalIds_ReturnsProviderMatches()
    {
        var repo = new SeriesManifestRepository(_db);
        var workId = await CreateWorkAsync("QWork");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "UPDATE works SET external_identifiers = @json WHERE id = @workId;",
                new { json = "{\"tmdb_id\":\"78\"}", workId });
        }

        var matches = await repo.FindWorkIdsByExternalIdsAsync("tmdb_id", ["78", "999"]);

        Assert.True(matches.TryGetValue("78", out var workIds));
        Assert.Equal([workId], workIds);
        Assert.False(matches.ContainsKey("999"));
    }

    [Fact]
    public async Task FindWorkIdsByExternalIds_ReturnsAssetScopedCanonicalMatchesThroughLineage()
    {
        var repo = new SeriesManifestRepository(_db);
        var workId = await CreateWorkAsync("QEpisode", "TV");
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Episode');",
                new { editionId, workId });
            conn.Execute(
                """
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES (@assetId, @editionId, @contentHash, @filePath);
                """,
                new
                {
                    assetId,
                    editionId,
                    contentHash = Guid.NewGuid().ToString("N"),
                    filePath = "episode.mkv",
                });
            conn.Execute(
                """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'tmdb_episode_id', '62085', @now);
                """,
                new { assetId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var matches = await repo.FindWorkIdsByExternalIdsAsync("tmdb_episode_id", ["62085"]);

        Assert.True(matches.TryGetValue("62085", out var workIds));
        Assert.Equal([workId], workIds);
    }

    [Fact]
    public async Task UpsertManifest_ReplacesRemovedProviderItemsAndPersistsClassification()
    {
        var repo = new SeriesManifestRepository(_db);
        var collectionId = await CreateCollectionAsync(string.Empty, "Provider sequence");
        var now = DateTimeOffset.UtcNow;
        var first = Item(collectionId, "provider:series:1", "provider:item:1", "One", 1, "Missing", null, now, mediaKind: "Film");
        var second = Item(collectionId, "provider:series:1", "provider:item:2", "Two", 2, "Missing", null, now);

        await repo.UpsertManifestAsync(Hydration(collectionId, "provider:series:1", now), [first, second]);
        await repo.UpsertManifestAsync(Hydration(collectionId, "provider:series:1", now.AddMinutes(1)), [first]);

        var view = await repo.GetViewByCollectionIdAsync(collectionId);
        Assert.NotNull(view);
        var retained = Assert.Single(view.Items);
        Assert.Equal("provider:item:1", retained.ItemQid);
        Assert.Equal("Film", retained.MediaKind);
    }

    [Fact]
    public async Task GetView_CountsMainSequenceWithoutDroppingSupplementaryWorks()
    {
        var repo = new SeriesManifestRepository(_db);
        var collectionId = await CreateCollectionAsync("QExpanse", "The Expanse");
        var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(1, 9)
            .Select(index => Item(collectionId, "QExpanse", $"QBook{index}", $"Book {index}", index, "Missing", null, now))
            .Append(Item(
                collectionId,
                "QExpanse",
                "QChurn",
                "The Churn",
                10,
                "Owned",
                await CreateWorkAsync("QChurn"),
                now,
                membershipScope: SeriesMembershipScopeNames.Supplementary))
            .ToList();

        await repo.UpsertManifestAsync(Hydration(collectionId, "QExpanse", now), items);

        var view = await repo.GetViewByCollectionIdAsync(collectionId);

        Assert.NotNull(view);
        Assert.Equal(9, view.TotalCount);
        Assert.Equal(0, view.OwnedCount);
        Assert.Equal(1, view.SupplementaryCount);
        Assert.Equal(10, view.Items.Count);
        Assert.Contains(view.Items, item =>
            item.ItemQid == "QChurn"
            && item.MembershipScope == SeriesMembershipScopeNames.Supplementary);
    }

    [Fact]
    public async Task UpsertManifest_UpdatesExistingItemLabelAndSortOrder()
    {
        var repo = new SeriesManifestRepository(_db);
        var collectionId = await CreateCollectionAsync("QSeries", "The Expanse");
        var now = DateTimeOffset.UtcNow;

        await repo.UpsertManifestAsync(Hydration(collectionId, "QSeries", now), [
            Item(collectionId, "QSeries", "Q1", "Old Label", 10, "Missing", null, now)
        ]);
        await repo.UpsertManifestAsync(Hydration(collectionId, "QSeries", now.AddMinutes(1)), [
            Item(collectionId, "QSeries", "Q1", "Leviathan Wakes", 1, "Missing", null, now.AddMinutes(1))
        ]);

        var view = await repo.GetViewByCollectionIdAsync(collectionId);

        Assert.NotNull(view);
        var item = Assert.Single(view.Items);
        Assert.Equal("Leviathan Wakes", item.ItemLabel);
        Assert.Equal(1, item.SortOrder);
    }

    [Fact]
    public async Task GetViewByCollectionId_UsesExpectedTotalMetadataWhenManifestRowsAreSparse()
    {
        var repo = new SeriesManifestRepository(_db);
        var collectionId = await CreateCollectionAsync("Q827099", "The Sandman");
        var firstWorkId = await CreateWorkAsync("QIssue1");
        var secondWorkId = await CreateWorkAsync("QIssue2");
        var now = DateTimeOffset.UtcNow;

        var items = new List<SeriesManifestItemRecord>
        {
            Item(collectionId, "Q827099", "QIssue1", "Sleep of the Just", 1, "Owned", firstWorkId, now),
            Item(collectionId, "Q827099", "QIssue2", "Imperfect Hosts", 2, "Owned", secondWorkId, now),
        };

        await repo.UpsertManifestAsync(new SeriesManifestHydration
        {
            SeriesQid = "Q827099",
            CollectionId = collectionId,
            SeriesLabel = "The Sandman",
            WarningsJson = "[]",
            ApiMetadataJson = """
                {
                  "containerKind":"ComicSeries",
                  "expectedTotal":75,
                  "expectedTotalKind":"issues",
                  "expectedTotalSource":"external-reference",
                  "expectedTotalConfidence":0.9
                }
                """,
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        }, items);

        var view = await repo.GetViewByCollectionIdAsync(collectionId);

        Assert.NotNull(view);
        Assert.Equal("ComicSeries", view.ContainerKind);
        Assert.Equal(75, view.ExpectedTotal);
        Assert.Equal("issues", view.ExpectedTotalKind);
        Assert.Equal(75, view.TotalCount);
        Assert.Equal(2, view.OwnedCount);
        Assert.Equal(73, view.MissingCount);
    }

    [Fact]
    public async Task LinkOwnedWorks_DoesNotAssignExpandedParentManifestAsLaneShelf()
    {
        var repo = new SeriesManifestRepository(_db);
        var broadCollectionId = await CreateCollectionAsync("Q26214973", "Peter Jackson's Middle-earth film series");
        var childCollectionId = await CreateCollectionAsync("Q74331", "The Hobbit trilogy");
        var workId = await CreateWorkAsync("Q80379");
        var now = DateTimeOffset.UtcNow;

        var broadItems = new[]
        {
            Item(
                broadCollectionId,
                "Q26214973",
                "Q80379",
                "The Hobbit: An Unexpected Journey",
                2,
                "Owned",
                workId,
                now,
                parentCollectionQid: "Q74331",
                parentCollectionLabel: "The Hobbit trilogy",
                isExpandedFromCollection: true),
        };

        await repo.UpsertManifestAsync(Hydration(broadCollectionId, "Q26214973", now), broadItems);
        await repo.LinkOwnedWorksAsync(broadCollectionId, broadItems);

        Assert.Null(await GetWorkCollectionIdAsync(workId));

        await AssignWorkToCollectionAsync(workId, broadCollectionId);

        var childItems = new[]
        {
            Item(
                childCollectionId,
                "Q74331",
                "Q80379",
                "The Hobbit: An Unexpected Journey",
                1,
                "Owned",
                workId,
                now.AddMinutes(1)),
        };

        await repo.UpsertManifestAsync(Hydration(childCollectionId, "Q74331", now.AddMinutes(1)), childItems);
        await repo.LinkOwnedWorksAsync(childCollectionId, childItems);

        Assert.Equal(childCollectionId, await GetWorkCollectionIdAsync(workId));
    }

    [Fact]
    public async Task LinkOwnedWorks_DoesNotOverrideProviderOwnedImmediateShelf()
    {
        var repo = new SeriesManifestRepository(_db);
        var wikidataCollectionId = await CreateCollectionAsync("Q74331", "The Hobbit trilogy");
        var providerCollectionId = await CreateCollectionAsync(
            qid: "",
            name: "The Hobbit Collection",
            ruleHash: "tmdb:collection:121938");
        var workId = await CreateWorkAsync("Q80379", mediaType: "Movies");
        var now = DateTimeOffset.UtcNow;

        await AssignWorkToCollectionAsync(workId, providerCollectionId);

        var items = new[]
        {
            Item(
                wikidataCollectionId,
                "Q74331",
                "Q80379",
                "The Hobbit: An Unexpected Journey",
                1,
                "Owned",
                workId,
                now),
        };

        await repo.UpsertManifestAsync(Hydration(wikidataCollectionId, "Q74331", now), items);
        await repo.LinkOwnedWorksAsync(wikidataCollectionId, items);

        Assert.Equal(providerCollectionId, await GetWorkCollectionIdAsync(workId));
    }

    private async Task<Guid> CreateCollectionAsync(string qid, string name, string? ruleHash = null)
    {
        var repo = new CollectionRepository(_db);
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            WikidataQid = string.IsNullOrWhiteSpace(qid) ? null : qid,
            RuleHash = ruleHash,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);
        return await repo.UpsertAsync(collection);
    }

    private Task<Guid> CreateWorkAsync(string qid, string mediaType = "Books")
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, collection_id, media_type, work_kind, wikidata_qid, external_identifiers)
            VALUES (@id, NULL, @mediaType, 'standalone', @qid, @externalIdentifiers);
            """;
        cmd.Parameters.AddWithValue("@id", GuidSql.ToBlob(id));
        cmd.Parameters.AddWithValue("@mediaType", mediaType);
        cmd.Parameters.AddWithValue("@qid", qid);
        cmd.Parameters.AddWithValue("@externalIdentifiers", $$"""{"wikidata_qid":"{{qid}}"}""");
        cmd.ExecuteNonQuery();
        return Task.FromResult(id);
    }

    private async Task AssignWorkToCollectionAsync(Guid workId, Guid collectionId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE works SET collection_id = @collectionId WHERE id = @workId;",
            new { collectionId, workId });
    }

    private async Task<Guid?> GetWorkCollectionIdAsync(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid?>(
            "SELECT collection_id FROM works WHERE id = @workId;",
            new { workId });
    }

    private int CountRows(string table)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static SeriesManifestHydration Hydration(Guid collectionId, string seriesQid, DateTimeOffset now) => new()
    {
        SeriesQid = seriesQid,
        CollectionId = collectionId,
        SeriesLabel = "Series",
        WarningsJson = "[]",
        ApiMetadataJson = "{}",
        LastHydratedAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static SeriesManifestItemRecord Item(
        Guid collectionId,
        string seriesQid,
        string qid,
        string label,
        double sortOrder,
        string ownership,
        Guid? linkedWorkId,
        DateTimeOffset now,
        string? parentCollectionQid = null,
        string? parentCollectionLabel = null,
        bool isCollection = false,
        bool isExpandedFromCollection = false,
        string membershipScope = SeriesMembershipScopeNames.MainSequence,
        string? mediaKind = null)
        => new()
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            SeriesQid = seriesQid,
            ItemQid = qid,
            ItemLabel = label,
            MediaKind = mediaKind,
            SortOrder = sortOrder,
            OrderSource = "SeriesOrdinal",
            OwnershipState = ownership,
            LinkedWorkId = linkedWorkId,
            ParentCollectionQid = parentCollectionQid,
            ParentCollectionLabel = parentCollectionLabel,
            IsCollection = isCollection,
            IsExpandedFromCollection = isExpandedFromCollection,
            MembershipScope = membershipScope,
            SourcePropertiesJson = """["P179"]""",
            RelationshipsJson = "[]",
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
}
