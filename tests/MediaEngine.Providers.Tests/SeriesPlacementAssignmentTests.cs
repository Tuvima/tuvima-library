using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// One placement rule decides which Series a work sits on. These tests run the real assignment service and the real
/// manifest repositories against a temp data store, with an Expanse-style library: novels 1-9, novellas with decimal
/// positions listed (or not) by Wikidata, an audiobook of the same Series, and a TV show of the same name.
/// </summary>
public sealed class SeriesPlacementAssignmentTests : IDisposable
{
    private const string Expanse = "QExpanse";
    private const string MemorysLegion = "QMemorysLegion";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _collections;
    private readonly SeriesManifestRepository _manifests;
    private readonly ReviewQueueRepository _reviews;
    private readonly CollectionAssignmentService _assignment;
    private readonly Dictionary<string, Library> _works = new();

    private sealed record Library(Guid WorkId, Guid AssetId);

    public SeriesPlacementAssignmentTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_placement_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _collections = new CollectionRepository(_db);
        _manifests = new SeriesManifestRepository(_db);
        _reviews = new ReviewQueueRepository(_db);
        var canonicals = new CanonicalValueRepository(_db);
        var outcomes = new StageOutcomeFactory(
            _reviews,
            new SystemActivityRepository(_db),
            new SilentEventPublisher(),
            canonicals,
            NullLogger<StageOutcomeFactory>.Instance);
        _assignment = new CollectionAssignmentService(
            _collections,
            canonicals,
            new WorkRepository(_db),
            NullLogger<CollectionAssignmentService>.Instance,
            new CanonicalValueArrayRepository(_db),
            _manifests,
            outcomes);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpanseLibrary_PlacesNovelsAndNovellasOnOneBookSeries_WhateverTheOrderOfAssignmentAndHydration(bool hydrateBeforeAssigning)
    {
        SeedExpanseLibrary();

        if (hydrateBeforeAssigning)
        {
            await HydrateExpanseManifestsAsync();
            await AssignEverythingAsync();
        }
        else
        {
            // Assignment first: Gods of Risk's claims name Memory's Legion, so it lands on that shelf until the manifests arrive.
            await AssignEverythingAsync();
            Assert.NotEqual(await ShelfOfAsync("Leviathan Wakes"), await ShelfOfAsync("Gods of Risk"));
            await HydrateExpanseManifestsAsync();
            await AssignEverythingAsync();
        }

        // Running it again changes nothing.
        var firstPass = await SnapshotShelvesAsync();
        await AssignEverythingAsync();
        await AssignEverythingAsync();
        Assert.Equal(firstPass, await SnapshotShelvesAsync());

        var bookSeries = (await _collections.FindSeriesShelfAsync(Expanse, "Books"))!;
        var audiobookSeries = (await _collections.FindSeriesShelfAsync(Expanse, "Audiobooks"))!;
        var tvSeries = (await _collections.FindSeriesShelfAsync(Expanse, "TV"))!;
        Assert.Equal(3, new[] { bookSeries.Id, audiobookSeries.Id, tvSeries.Id }.Distinct().Count());

        // The nine novels and every novella are on the one book Series.
        foreach (var title in NovelTitles.Concat(["The Churn", "Gods of Risk", "The Vital Abyss", "The Butcher of Anderson Station", "Strange Dog"]))
        {
            Assert.Equal(bookSeries.Id, await ShelfOfAsync(title));
        }

        // Memory's Legion never became a shelf for its works.
        var memorysLegion = await _collections.FindSeriesShelfAsync(MemorysLegion, "Books");
        if (memorysLegion is not null)
        {
            Assert.Equal(0, await CountWorksOnShelfAsync(memorysLegion.Id));
        }

        // The audiobook and the TV show are separate Series, not merged into the book Series.
        Assert.Equal(audiobookSeries.Id, await ShelfOfAsync("Leviathan Wakes (audiobook)"));
        Assert.Equal(tvSeries.Id, await ShelfOfAsync("The Expanse (TV)"));
        Assert.Equal(1, await CountWorksOnShelfAsync(audiobookSeries.Id));
        Assert.Equal(1, await CountWorksOnShelfAsync(tvSeries.Id));
        Assert.Equal("Audiobooks", audiobookSeries.MediaType);
        Assert.Equal("TV", tvSeries.MediaType);
        Assert.Equal("Books", bookSeries.MediaType);
    }

    [Fact]
    public async Task TwoSeriesBothListingAWorkAsMainSequence_LeaveItUnplaced_AndAskOnceInNeedsReview()
    {
        var one = await CreateSeriesShelfAsync("QSeriesOne", "Series One", "Books");
        var two = await CreateSeriesShelfAsync("QSeriesTwo", "Series Two", "Books");
        var work = AddWork("Shared Work", "Books", seriesQid: "QSeriesOne", seriesLabel: "Series One");
        await _assignment.AssignAsync(work.AssetId);
        Assert.Equal(one, await ShelfOfAsync("Shared Work"));

        await StoreManifestAsync(one, "QSeriesOne", Item(one, "QSeriesOne", "QShared", "Shared Work", work.WorkId, parsed: 1));
        await StoreManifestAsync(two, "QSeriesTwo", Item(two, "QSeriesTwo", "QShared", "Shared Work", work.WorkId, parsed: 3));

        var result = await _assignment.AssignAsync(work.AssetId);
        var again = await _assignment.AssignAsync(work.AssetId);

        Assert.Equal(CollectionAssignmentOutcome.SkippedAmbiguousSeries, result.Outcome);
        Assert.Equal(CollectionAssignmentOutcome.SkippedAmbiguousSeries, again.Outcome);
        Assert.Null(await ShelfOfAsync("Shared Work"));

        var pending = (await _reviews.GetPendingAsync(50)).Where(r => r.Trigger == ReviewTrigger.AmbiguousSeriesMembership).ToList();
        var review = Assert.Single(pending);
        Assert.Equal(work.AssetId, review.EntityId);
        Assert.Contains("QSeriesOne", review.CandidatesJson);
        Assert.Contains("QSeriesTwo", review.CandidatesJson);
    }

    [Fact]
    public async Task ASeriesOfAnotherMediaType_IsNeverFoundForAWork()
    {
        var bookSeries = await CreateSeriesShelfAsync(Expanse, "The Expanse", "Books");
        var audiobook = AddWork("Leviathan Wakes (audiobook)", "Audiobooks", seriesQid: Expanse, seriesLabel: "The Expanse");

        var result = await _assignment.AssignAsync(audiobook.AssetId);

        Assert.Equal(CollectionAssignmentOutcome.Assigned, result.Outcome);
        Assert.NotEqual(bookSeries, result.CollectionId);
        Assert.True(result.CreatedCollection);
        Assert.Equal("Audiobooks", (await _collections.GetByIdAsync(result.CollectionId!.Value))!.MediaType);
    }

    [Fact]
    public async Task ANovellaListedThroughAParentCollection_DoesNotWriteItsOwnSeriesClaimOntoTheSeriesShelf()
    {
        SeedExpanseLibrary();
        await AssignEverythingAsync();
        await HydrateExpanseManifestsAsync();
        await AssignEverythingAsync();

        var bookSeries = (await _collections.FindSeriesShelfAsync(Expanse, "Books"))!;
        var relationships = await _collections.GetRelationshipsAsync(bookSeries.Id);

        // Gods of Risk's claims name Memory's Legion as its series; that is not The Expanse's own series relationship.
        Assert.DoesNotContain(relationships, r => r.RelType == "series" && r.RelQid == MemorysLegion);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // The Expanse-style library
    // ----------------------------------------------------------------------------------------------------------------

    private static readonly string[] NovelTitles =
    [
        "Leviathan Wakes", "Caliban's War", "Abaddon's Gate", "Cibola Burn", "Nemesis Games",
        "Babylon's Ashes", "Persepolis Rising", "Tiamat's Wrath", "Leviathan Falls",
    ];

    private void SeedExpanseLibrary()
    {
        for (var index = 0; index < NovelTitles.Length; index++)
        {
            AddWork(NovelTitles[index], "Books", seriesQid: Expanse, seriesLabel: "The Expanse", position: (index + 1).ToString());
        }

        // Retail-only novellas: no Wikidata manifest names them.
        AddWork("The Butcher of Anderson Station", "Books", seriesQid: Expanse, seriesLabel: "The Expanse", position: "0.5", qid: "QButcher");
        AddWork("Strange Dog", "Books", seriesQid: Expanse, seriesLabel: "The Expanse", position: "6.5", qid: "QStrangeDog");

        // Novellas the Expanse manifest lists as collected content of Memory's Legion. Gods of Risk's claims name
        // Memory's Legion itself as its series, the way a retail source can.
        AddWork("The Churn", "Books", seriesQid: Expanse, seriesLabel: "The Expanse", position: "0.2", qid: "QChurn");
        AddWork("Gods of Risk", "Books", seriesQid: MemorysLegion, seriesLabel: "Memory's Legion", position: "2.5", qid: "QGodsOfRisk");
        AddWork("The Vital Abyss", "Books", seriesQid: Expanse, seriesLabel: "The Expanse", position: "5.5", qid: "QVitalAbyss");

        AddWork("Leviathan Wakes (audiobook)", "Audiobooks", seriesQid: Expanse, seriesLabel: "The Expanse", position: "1", qid: "QNovel1");
        AddWork("The Expanse (TV)", "TV", seriesQid: Expanse, seriesLabel: "The Expanse");

        // The first novel's Wikidata item is shared by the book and its audiobook.
        SetQid("Leviathan Wakes", "QNovel1");
    }

    private async Task HydrateExpanseManifestsAsync()
    {
        var series = await FindOrCreateSeriesShelfAsync(Expanse, "The Expanse", "Books");
        var parent = await FindOrCreateSeriesShelfAsync(MemorysLegion, "Memory's Legion", "Books");

        var seriesItems = new List<SeriesManifestItemRecord>
        {
            Item(series, Expanse, "QNovel1", "Leviathan Wakes", _works["Leviathan Wakes"].WorkId, parsed: 1),
            Item(series, Expanse, MemorysLegion, "Memory's Legion", null, isCollection: true, scope: SeriesMembershipScopeNames.CollectedContent),
        };
        foreach (var (title, qid) in new[] { ("The Churn", "QChurn"), ("Gods of Risk", "QGodsOfRisk"), ("The Vital Abyss", "QVitalAbyss") })
        {
            seriesItems.Add(Item(series, Expanse, qid, title, _works[title].WorkId,
                scope: SeriesMembershipScopeNames.CollectedContent, expandedFrom: MemorysLegion));
        }

        // The other eight novels are listed and owned too.
        for (var index = 1; index < NovelTitles.Length; index++)
        {
            seriesItems.Add(Item(series, Expanse, $"QNovel{index + 1}", NovelTitles[index], _works[NovelTitles[index]].WorkId, parsed: index + 1));
        }

        await StoreManifestAsync(series, Expanse, [.. seriesItems]);

        var parentItems = new[]
        {
            Item(parent, MemorysLegion, "QChurn", "The Churn", _works["The Churn"].WorkId, parsed: 1),
            Item(parent, MemorysLegion, "QGodsOfRisk", "Gods of Risk", _works["Gods of Risk"].WorkId, parsed: 2),
            Item(parent, MemorysLegion, "QVitalAbyss", "The Vital Abyss", _works["The Vital Abyss"].WorkId, parsed: 3),
        };
        await StoreManifestAsync(parent, MemorysLegion, parentItems, isParentCollection: true);
    }

    private async Task AssignEverythingAsync()
    {
        foreach (var library in _works.Values)
        {
            await _assignment.AssignAsync(library.AssetId);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Fixtures
    // ----------------------------------------------------------------------------------------------------------------

    private Library AddWork(
        string title,
        string mediaType,
        string seriesQid,
        string seriesLabel,
        string? position = null,
        string? qid = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, collection_id, media_type, work_kind, wikidata_qid, external_identifiers, ordinal_sort)
            VALUES (@workId, NULL, @mediaType, 'standalone', @qid, @externalIdentifiers, @ordinalSort);
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Edition');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @contentHash, @filePath);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                mediaType,
                qid,
                externalIdentifiers = qid is null ? "{}" : $$"""{"wikidata_qid":"{{qid}}"}""",
                ordinalSort = double.TryParse(position, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : (double?)null,
                contentHash = Guid.NewGuid().ToString("N"),
                filePath = $"{title}.bin",
            });

        var values = new Dictionary<string, string>
        {
            ["title"] = title,
            ["series"] = seriesLabel,
            ["series_qid"] = seriesQid,
            ["media_type"] = mediaType,
        };
        if (position is not null)
        {
            values["series_position"] = position;
        }

        foreach (var (key, value) in values)
        {
            conn.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, @key, @value, @now);",
                new { workId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var library = new Library(workId, assetId);
        _works[title] = library;
        return library;
    }

    private void SetQid(string title, string qid)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "UPDATE works SET wikidata_qid = @qid, external_identifiers = @json WHERE id = @id;",
            new { qid, json = $$"""{"wikidata_qid":"{{qid}}"}""", id = _works[title].WorkId });
    }

    private async Task<Guid> FindOrCreateSeriesShelfAsync(string qid, string name, string mediaType)
        => (await _collections.FindSeriesShelfAsync(qid, mediaType))?.Id
           ?? await CreateSeriesShelfAsync(qid, name, mediaType);

    private async Task<Guid> CreateSeriesShelfAsync(string qid, string name, string mediaType)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            WikidataQid = qid,
            MediaType = mediaType,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);
        return await _collections.UpsertAsync(collection);
    }

    private async Task StoreManifestAsync(
        Guid collectionId,
        string seriesQid,
        SeriesManifestItemRecord item)
        => await StoreManifestAsync(collectionId, seriesQid, [item]);

    private async Task StoreManifestAsync(
        Guid collectionId,
        string seriesQid,
        SeriesManifestItemRecord[] items,
        bool isParentCollection = false)
    {
        var now = DateTimeOffset.UtcNow;
        await _manifests.UpsertManifestAsync(
            new SeriesManifestHydration
            {
                SeriesQid = seriesQid,
                CollectionId = collectionId,
                SeriesLabel = seriesQid,
                WarningsJson = "[]",
                ApiMetadataJson = isParentCollection
                    ? """{"parentCollectionHydration":true,"sourceSeriesQid":"QExpanse"}"""
                    : "{}",
                LastHydratedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            items);
        await _manifests.LinkOwnedWorksAsync(collectionId, items);
    }

    private static SeriesManifestItemRecord Item(
        Guid collectionId,
        string seriesQid,
        string qid,
        string label,
        Guid? linked,
        double? parsed = null,
        string scope = SeriesMembershipScopeNames.MainSequence,
        bool isCollection = false,
        string? expandedFrom = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new SeriesManifestItemRecord
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            SeriesQid = seriesQid,
            ItemQid = qid,
            ItemLabel = label,
            MediaType = "Books",
            RawOrdinal = parsed?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ParsedOrdinal = parsed,
            OrdinalScopeQid = parsed.HasValue ? seriesQid : null,
            SortOrder = 99,
            OrderSource = "SeriesOrdinal",
            OwnershipState = linked.HasValue ? "Owned" : "Missing",
            LinkedWorkId = linked,
            IsCollection = isCollection,
            IsExpandedFromCollection = expandedFrom is not null,
            ParentCollectionQid = expandedFrom,
            MembershipScope = scope,
            SourcePropertiesJson = """["P179"]""",
            RelationshipsJson = "[]",
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private async Task<Guid?> ShelfOfAsync(string title)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid?>(
            "-- readiness: test lookup of one work by id\nSELECT collection_id FROM works WHERE id = @id;",
            new { id = _works[title].WorkId });
    }

    private async Task<int> CountWorksOnShelfAsync(Guid collectionId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "-- readiness: test count of the works on one shelf\nSELECT COUNT(*) FROM works WHERE collection_id = @collectionId;",
            new { collectionId });
    }

    private async Task<string> SnapshotShelvesAsync()
    {
        var lines = new List<string>();
        foreach (var title in _works.Keys.Order(StringComparer.Ordinal))
        {
            var shelf = await ShelfOfAsync(title);
            var name = shelf is null ? "(none)" : (await _collections.GetByIdAsync(shelf.Value))!.DisplayName;
            lines.Add($"{title} -> {name}");
        }

        return string.Join('\n', lines);
    }

    private sealed class SilentEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
            => Task.CompletedTask;
    }
}
