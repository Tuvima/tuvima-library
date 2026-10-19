using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A Series is its Wikidata Series QID plus its media type, and one placement rule decides which Series a work sits on,
/// however many times manifests are hydrated and in whatever order.
/// </summary>
public sealed class SeriesIdentityTests : IDisposable
{
    private const string Expanse = "QExpanse";
    private const string MemorysLegion = "QMemorysLegion";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _collections;
    private readonly SeriesManifestRepository _manifests;

    public SeriesIdentityTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_identity_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _collections = new CollectionRepository(_db);
        _manifests = new SeriesManifestRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task FindSeriesShelf_ReturnsOnlyTheSeriesOfTheAskedMediaType()
    {
        var books = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var audiobooks = await CreateSeriesAsync(Expanse, "The Expanse", "Audiobooks");

        Assert.Equal(books, (await _collections.FindSeriesShelfAsync(Expanse, "Books"))!.Id);
        Assert.Equal(audiobooks, (await _collections.FindSeriesShelfAsync(Expanse, "audiobooks"))!.Id);
        Assert.Null(await _collections.FindSeriesShelfAsync(Expanse, "TV"));
    }

    [Fact]
    public async Task FindSeriesShelf_NeverReturnsAUniverseOrOtherCollectionThatSharesTheQid()
    {
        var universe = new Collection { Id = Guid.NewGuid(), DisplayName = "The Expanse universe", WikidataQid = Expanse, MediaType = "Books", CreatedAt = DateTimeOffset.UtcNow };
        universe.RestoreDefinition(CollectionType.Universe, CollectionScope.Library, CollectionResolution.Materialized, CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        await _collections.UpsertAsync(universe);

        Assert.Null(await _collections.FindSeriesShelfAsync(Expanse, "Books"));
    }

    [Fact]
    public async Task UpsertCollection_KeepsTheMediaTypeWhenALaterWriteDoesNotCarryOne()
    {
        var id = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var loaded = (await _collections.GetByIdAsync(id))!;
        Assert.Equal("Books", loaded.MediaType);

        var withoutMediaType = new Collection { Id = id, DisplayName = "The Expanse (renamed)", WikidataQid = Expanse, CreatedAt = loaded.CreatedAt };
        withoutMediaType.RestoreDefinition(CollectionType.ContentGroup, CollectionScope.Library, CollectionResolution.Materialized, CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        await _collections.UpsertAsync(withoutMediaType);

        Assert.Equal("Books", (await _collections.GetByIdAsync(id))!.MediaType);
    }

    [Fact]
    public async Task ABookSeriesAndAnAudiobookSeriesOfTheSameQid_KeepTheirOwnManifests()
    {
        var books = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var audiobooks = await CreateSeriesAsync(Expanse, "The Expanse", "Audiobooks");
        var book = await CreateWorkAsync("QLeviathan", "Books");
        var audiobook = await CreateWorkAsync("QLeviathan", "Audiobooks");
        var now = DateTimeOffset.UtcNow;

        await UpsertAndLinkAsync(books, Expanse, now, Item(books, Expanse, "QLeviathan", "Leviathan Wakes", "Books", linked: book, parsed: 1));
        await UpsertAndLinkAsync(audiobooks, Expanse, now, Item(audiobooks, Expanse, "QLeviathan", "Leviathan Wakes", "Audiobooks", linked: audiobook, parsed: 1));

        Assert.Equal(books, (await _manifests.GetHydrationAsync(Expanse, "Books"))!.CollectionId);
        Assert.Equal(audiobooks, (await _manifests.GetHydrationAsync(Expanse, "Audiobooks"))!.CollectionId);
        Assert.Equal(book, Assert.Single(await _manifests.GetItemsBySeriesQidAsync(Expanse, "Books")).LinkedWorkId);
        Assert.Equal(audiobook, Assert.Single(await _manifests.GetItemsBySeriesQidAsync(Expanse, "Audiobooks")).LinkedWorkId);
        Assert.Equal(2, (await _manifests.GetItemsBySeriesQidAsync(Expanse)).Count);
        Assert.Equal(books, await ShelfOfAsync(book));
        Assert.Equal(audiobooks, await ShelfOfAsync(audiobook));
    }

    [Fact]
    public async Task FindWorkIdsByQids_CanBeLimitedToOneMediaType()
    {
        var book = await CreateWorkAsync("QLeviathan", "Books");
        var audiobook = await CreateWorkAsync("QLeviathan", "Audiobooks");

        var all = await _manifests.FindWorkIdsByQidsAsync(["QLeviathan"]);
        var onlyAudio = await _manifests.FindWorkIdsByQidsAsync(["QLeviathan"], "Audiobooks");

        Assert.Equal(2, all["QLeviathan"].Count);
        Assert.Equal([audiobook], onlyAudio["QLeviathan"]);
        Assert.DoesNotContain(book, onlyAudio["QLeviathan"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectedContentListedThroughAParentCollection_SitsOnTheSeries_NotOnTheParentShelf_InEitherHydrationOrder(bool parentFirst)
    {
        var series = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var parent = await CreateSeriesAsync(MemorysLegion, "Memory's Legion", "Books");
        var leviathan = await CreateWorkAsync("QLeviathan", "Books");
        var churn = await CreateWorkAsync("QChurn", "Books");
        var godsOfRisk = await CreateWorkAsync("QGodsOfRisk", "Books");
        var now = DateTimeOffset.UtcNow;

        // The Expanse's manifest: the nine novels (one here), Memory's Legion as a collection, and its expanded children.
        var seriesItems = new[]
        {
            Item(series, Expanse, "QLeviathan", "Leviathan Wakes", "Books", linked: leviathan, parsed: 1),
            Item(series, Expanse, MemorysLegion, "Memory's Legion", "Books", isCollection: true, scope: SeriesMembershipScopeNames.CollectedContent),
            Item(series, Expanse, "QChurn", "The Churn", "Books", linked: churn, scope: SeriesMembershipScopeNames.CollectedContent,
                expandedFrom: MemorysLegion),
            Item(series, Expanse, "QGodsOfRisk", "Gods of Risk", "Books", linked: godsOfRisk, scope: SeriesMembershipScopeNames.CollectedContent,
                expandedFrom: MemorysLegion),
        };

        // Memory's Legion's own manifest, hydrated because The Expanse listed it. Its works are main-sequence there.
        var parentItems = new[]
        {
            Item(parent, MemorysLegion, "QChurn", "The Churn", "Books", linked: churn, parsed: 2),
            Item(parent, MemorysLegion, "QGodsOfRisk", "Gods of Risk", "Books", linked: godsOfRisk, parsed: 3),
        };

        if (parentFirst)
        {
            await UpsertAndLinkAsync(parent, MemorysLegion, now, parentItems);
            await UpsertAndLinkAsync(series, Expanse, now, seriesItems);
        }
        else
        {
            await UpsertAndLinkAsync(series, Expanse, now, seriesItems);
            await UpsertAndLinkAsync(parent, MemorysLegion, now, parentItems);
        }

        Assert.Equal(series, await ShelfOfAsync(leviathan));
        Assert.Equal(series, await ShelfOfAsync(churn));
        Assert.Equal(series, await ShelfOfAsync(godsOfRisk));
        Assert.Equal(0, await CountItemsAsync(parent));
        Assert.Equal(3, await CountItemsAsync(series));
    }

    [Fact]
    public async Task AParentCollectionsWork_IsMovedToTheListingSeries_ByLinkingAlone()
    {
        // The Gods of Risk case: only the parent's manifest names the work; the Series that lists the parent arrives
        // later and lists the collection itself, with no row for the work. No assignment run happens in between.
        var series = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var parent = await CreateSeriesAsync(MemorysLegion, "Memory's Legion", "Books");
        var godsOfRisk = await CreateWorkAsync("QGodsOfRisk", "Books");
        var now = DateTimeOffset.UtcNow;

        await UpsertAndLinkAsync(parent, MemorysLegion, now,
            Item(parent, MemorysLegion, "QGodsOfRisk", "Gods of Risk", "Books", linked: godsOfRisk, parsed: 3));
        Assert.Equal(parent, await ShelfOfAsync(godsOfRisk));

        await UpsertAndLinkAsync(series, Expanse, now,
            Item(series, Expanse, MemorysLegion, "Memory's Legion", "Books", isCollection: true, scope: SeriesMembershipScopeNames.CollectedContent));

        Assert.Equal(series, await ShelfOfAsync(godsOfRisk));
        Assert.Equal(1, await CountItemsAsync(series));
        Assert.Equal(0, await CountItemsAsync(parent));
        var decision = SeriesPlacementRule.Decide(new SeriesPlacementInput("Books", await _manifests.GetMembershipsForWorkAsync(godsOfRisk), null));
        Assert.Equal(series, decision.ShelfCollectionId);
        Assert.Equal(SeriesMembershipScopeNames.CollectedContent, decision.Scope);

        // Linking again changes nothing.
        await _manifests.LinkOwnedWorksAsync(series, [Item(series, Expanse, MemorysLegion, "Memory's Legion", "Books", isCollection: true, scope: SeriesMembershipScopeNames.CollectedContent)]);
        Assert.Equal(series, await ShelfOfAsync(godsOfRisk));
        Assert.Equal(1, await CountItemsAsync(series));
    }

    [Fact]
    public async Task AParentCollectionIsReadFromTheData_NotFromAnyFlagOnItsHydration()
    {
        var series = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var parent = await CreateSeriesAsync(MemorysLegion, "Memory's Legion", "Books");
        var gods = await CreateWorkAsync("QGodsOfRisk", "Books");
        var now = DateTimeOffset.UtcNow;

        // Whichever way the parent's own hydration was written (an ordinary Series hydration here), it is a parent as
        // soon as another Series manifest of the same media type lists its QID as a collection item.
        await UpsertAndLinkAsync(parent, MemorysLegion, now, Item(parent, MemorysLegion, "QGodsOfRisk", "Gods of Risk", "Books", linked: gods, parsed: 3));
        await UpsertAndLinkAsync(series, Expanse, now,
            Item(series, Expanse, MemorysLegion, "Memory's Legion", "Books", isCollection: true),
            Item(series, Expanse, "QGodsOfRisk", "Gods of Risk", "Books", linked: gods, scope: SeriesMembershipScopeNames.CollectedContent, expandedFrom: MemorysLegion));

        var memberships = await _manifests.GetMembershipsForWorkAsync(gods);

        Assert.Contains(memberships, m => m.CollectionId == parent && m.Origin == SeriesMembershipOrigin.WikidataParentCollection);
        Assert.Contains(memberships, m => m.CollectionId == series && m.Origin == SeriesMembershipOrigin.WikidataSeries);
        Assert.Equal(series, await ShelfOfAsync(gods));
    }

    [Fact]
    public async Task ASeriesListingOfADifferentMediaType_DoesNotMakeTheParentAParent()
    {
        var audiobookSeries = await CreateSeriesAsync(Expanse, "The Expanse", "Audiobooks");
        var parent = await CreateSeriesAsync(MemorysLegion, "Memory's Legion", "Books");
        var gods = await CreateWorkAsync("QGodsOfRisk", "Books");
        var now = DateTimeOffset.UtcNow;

        await UpsertAndLinkAsync(parent, MemorysLegion, now, Item(parent, MemorysLegion, "QGodsOfRisk", "Gods of Risk", "Books", linked: gods, parsed: 3));
        await UpsertAndLinkAsync(audiobookSeries, Expanse, now,
            Item(audiobookSeries, Expanse, MemorysLegion, "Memory's Legion", "Audiobooks", isCollection: true));

        Assert.Equal(parent, await ShelfOfAsync(gods));
    }

    [Fact]
    public async Task ReLinkingAWorkTwoSeriesNowClaimAsMainSequence_ReturnsItSoAReviewCanBeRaised()
    {
        var one = await CreateSeriesAsync("QOne", "Series One", "Books");
        var two = await CreateSeriesAsync("QTwo", "Series Two", "Books");
        var work = await CreateWorkAsync("QWork", "Books");
        var now = DateTimeOffset.UtcNow;

        var first = await UpsertAndLinkAsync(one, "QOne", now, Item(one, "QOne", "QWork", "Shared Work", "Books", linked: work, parsed: 1));
        var second = await UpsertAndLinkAsync(two, "QTwo", now, Item(two, "QTwo", "QWork", "Shared Work", "Books", linked: work, parsed: 4));

        Assert.Empty(first);
        var ambiguous = Assert.Single(second);
        Assert.Equal(work, ambiguous.WorkId);
        Assert.Equal(["QOne", "QTwo"], ambiguous.Candidates.Select(c => c.SeriesQid).Order().ToArray());
        Assert.Null(await ShelfOfAsync(work));
    }

    [Fact]
    public async Task ATieBelowMainSequence_IsNotAmbiguous_AndTheSeriesTheWorkClaimsWins()
    {
        var one = await CreateSeriesAsync("QOne", "Series One", "Books");
        var two = await CreateSeriesAsync("QTwo", "Series Two", "Books");
        var work = await CreateWorkAsync("QWork", "Books");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'series_qid', 'QTwo::Series Two', @now);",
                new { work, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var now = DateTimeOffset.UtcNow;
        await UpsertAndLinkAsync(one, "QOne", now, Item(one, "QOne", "QWork", "Shared Work", "Books", linked: work, scope: SeriesMembershipScopeNames.Supplementary));
        var result = await UpsertAndLinkAsync(two, "QTwo", now, Item(two, "QTwo", "QWork", "Shared Work", "Books", linked: work, scope: SeriesMembershipScopeNames.Supplementary));

        Assert.Empty(result);
        Assert.Equal(two, await ShelfOfAsync(work));
    }

    [Fact]
    public async Task GetLinkedItemsByCollectionIds_ReadsManyShelvesInOneCall()
    {
        var one = await CreateSeriesAsync("QOne", "Series One", "Books");
        var two = await CreateSeriesAsync("QTwo", "Series Two", "Books");
        var a = await CreateWorkAsync("QA", "Books");
        var b = await CreateWorkAsync("QB", "Books");
        var now = DateTimeOffset.UtcNow;
        await UpsertAndLinkAsync(one, "QOne", now, Item(one, "QOne", "QA", "A", "Books", linked: a, parsed: 1), Item(one, "QOne", "QMissing", "Missing", "Books"));
        await UpsertAndLinkAsync(two, "QTwo", now, Item(two, "QTwo", "QB", "B", "Books", linked: b, parsed: 2));

        var rows = await _manifests.GetLinkedItemsByCollectionIdsAsync([one, two]);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.CollectionId == one && row.LinkedWorkId == a);
        Assert.Contains(rows, row => row.CollectionId == two && row.LinkedWorkId == b);
    }

    [Fact]
    public async Task LinkOwnedWorks_KeepsDecimalPositionsInTheShelfOrder()
    {
        var series = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var gods = await CreateWorkAsync("QGodsOfRisk", "Books");
        var now = DateTimeOffset.UtcNow;

        await UpsertAndLinkAsync(series, Expanse, now,
            Item(series, Expanse, "QGodsOfRisk", "Gods of Risk", "Books", linked: gods, parsed: 2.5, raw: "2.5"));

        using var conn = _db.CreateConnection();
        var stored = conn.QuerySingle<(string Type, double Value)>(
            "SELECT typeof(sort_order) AS Type, sort_order AS Value FROM collection_items WHERE collection_id = @series;",
            new { series });
        Assert.Equal("real", stored.Type);
        Assert.Equal(2.5, stored.Value);
    }

    [Fact]
    public async Task TwoSeriesListingTheWorkAsMainSequence_LeaveItOnNeitherShelf()
    {
        var one = await CreateSeriesAsync("QOne", "Series One", "Books");
        var two = await CreateSeriesAsync("QTwo", "Series Two", "Books");
        var work = await CreateWorkAsync("QWork", "Books");
        var now = DateTimeOffset.UtcNow;

        await UpsertAndLinkAsync(one, "QOne", now, Item(one, "QOne", "QWork", "Shared Work", "Books", linked: work, parsed: 1));
        Assert.Equal(one, await ShelfOfAsync(work));

        await UpsertAndLinkAsync(two, "QTwo", now, Item(two, "QTwo", "QWork", "Shared Work", "Books", linked: work, parsed: 4));

        Assert.Null(await ShelfOfAsync(work));
        Assert.Equal(0, await CountItemsAsync(one) + await CountItemsAsync(two));
    }

    [Fact]
    public async Task LinkOwnedWorks_NeverPutsAnAudiobookInTheBookSeries()
    {
        var books = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var audiobook = await CreateWorkAsync("QLeviathan", "Audiobooks");
        var now = DateTimeOffset.UtcNow;

        await UpsertAndLinkAsync(books, Expanse, now, Item(books, Expanse, "QLeviathan", "Leviathan Wakes", "Books", linked: audiobook, parsed: 1));

        Assert.Null(await ShelfOfAsync(audiobook));
        Assert.Equal(0, await CountItemsAsync(books));
    }

    [Fact]
    public async Task LinkingTwiceInAnyOrder_GivesTheSameShelves()
    {
        var series = await CreateSeriesAsync(Expanse, "The Expanse", "Books");
        var work = await CreateWorkAsync("QLeviathan", "Books");
        var now = DateTimeOffset.UtcNow;
        var item = Item(series, Expanse, "QLeviathan", "Leviathan Wakes", "Books", linked: work, parsed: 1);

        await UpsertAndLinkAsync(series, Expanse, now, item);
        await _manifests.LinkOwnedWorksAsync(series, [item]);
        await _manifests.LinkOwnedWorksAsync(series, [item]);

        Assert.Equal(series, await ShelfOfAsync(work));
        Assert.Equal(1, await CountItemsAsync(series));
    }

    // ----------------------------------------------------------------------------------------------------------------

    private async Task<Guid> CreateSeriesAsync(string qid, string name, string mediaType)
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

    private Task<Guid> CreateWorkAsync(string qid, string mediaType)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, collection_id, media_type, work_kind, wikidata_qid, external_identifiers)
            VALUES (@id, NULL, @mediaType, 'standalone', @qid, @externalIdentifiers);
            """,
            new { id, mediaType, qid, externalIdentifiers = $$"""{"wikidata_qid":"{{qid}}"}""" });
        return Task.FromResult(id);
    }

    private async Task<IReadOnlyList<AmbiguousSeriesPlacement>> UpsertAndLinkAsync(
        Guid collectionId,
        string seriesQid,
        DateTimeOffset now,
        params SeriesManifestItemRecord[] items)
        => await HydrateAsync(collectionId, seriesQid, now, "{}", items);

    private async Task<IReadOnlyList<AmbiguousSeriesPlacement>> HydrateAsync(
        Guid collectionId,
        string seriesQid,
        DateTimeOffset now,
        string apiMetadataJson,
        SeriesManifestItemRecord[] items)
    {
        await _manifests.UpsertManifestAsync(
            new SeriesManifestHydration
            {
                SeriesQid = seriesQid,
                CollectionId = collectionId,
                SeriesLabel = seriesQid,
                WarningsJson = "[]",
                ApiMetadataJson = apiMetadataJson,
                LastHydratedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            items);
        return await _manifests.LinkOwnedWorksAsync(collectionId, items);
    }

    private static SeriesManifestItemRecord Item(
        Guid collectionId,
        string seriesQid,
        string qid,
        string label,
        string mediaType,
        Guid? linked = null,
        double? parsed = null,
        string? raw = null,
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
            MediaType = mediaType,
            RawOrdinal = raw ?? parsed?.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

    private async Task<Guid?> ShelfOfAsync(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid?>(
            "-- readiness: test lookup of one work by id\nSELECT collection_id FROM works WHERE id = @workId;",
            new { workId });
    }

    private async Task<int> CountItemsAsync(Guid collectionId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM collection_items WHERE collection_id = @collectionId;",
            new { collectionId });
    }
}
