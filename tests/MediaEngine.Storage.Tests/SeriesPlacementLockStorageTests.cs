using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// A Series a person picks is saved as a lock on the work. These run the real repositories against a temp data store:
/// linking, assigning and releasing never move a locked work, and the Matching read shows what the person needs.
/// </summary>
public sealed class SeriesPlacementLockStorageTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly SeriesManifestRepository _manifests;
    private readonly SeriesPlacementRepository _placements;
    private readonly CollectionRepository _collections;

    public SeriesPlacementLockStorageTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_lock_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _manifests = new SeriesManifestRepository(_db);
        _placements = new SeriesPlacementRepository(_db);
        _collections = new CollectionRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task ChoosingASeries_PutsTheWorkOnItsShelf_LocksIt_AndKeepsTheShelfListConsistent()
    {
        var shelfA = await CreateShelfAsync("QA", "Series A");
        var shelfB = await CreateShelfAsync("QB", "Series B");
        var work = await CreateWorkAsync();
        await AssignAsync(work, shelfA);
        InsertCollectionItem(shelfA, work);

        var result = await _placements.SetChoiceAsync(work, shelfB);

        Assert.Equal(SeriesPlacementChoiceResult.Applied, result);
        Assert.Equal(shelfB, await ShelfOfAsync(work));
        Assert.Equal(1, await LockOfAsync(work));
        Assert.Equal([shelfB], await ShelfItemOwnersAsync(work));
        Assert.Equal(new SeriesPlacementLock(shelfB, "QB", "Books"), await _placements.GetLockAsync(work));
    }

    [Fact]
    public async Task ChoosingNoSeries_TakesTheWorkOffItsShelf_AndLocksThatChoice()
    {
        var shelf = await CreateShelfAsync("QA", "Series A");
        var work = await CreateWorkAsync();
        await AssignAsync(work, shelf);
        InsertCollectionItem(shelf, work);

        var result = await _placements.SetChoiceAsync(work, collectionId: null);

        Assert.Equal(SeriesPlacementChoiceResult.Applied, result);
        Assert.Null(await ShelfOfAsync(work));
        Assert.Empty(await ShelfItemOwnersAsync(work));
        Assert.True((await _placements.GetLockAsync(work))!.IsNoSeries);
    }

    [Fact]
    public async Task ChoosingAShelfOfAnotherMediaTypeOrNotASeries_IsRefused_AndNothingChanges()
    {
        var audiobookShelf = await CreateShelfAsync("QA", "Series A", mediaType: "Audiobooks");
        var universe = await CreateShelfAsync("QU", "A Universe", CollectionType.Universe);
        var work = await CreateWorkAsync();

        Assert.Equal(
            SeriesPlacementChoiceResult.NotASeriesShelfOfThisMediaType,
            await _placements.SetChoiceAsync(work, audiobookShelf));
        Assert.Equal(
            SeriesPlacementChoiceResult.NotASeriesShelfOfThisMediaType,
            await _placements.SetChoiceAsync(work, universe));
        Assert.Equal(SeriesPlacementChoiceResult.ShelfNotFound, await _placements.SetChoiceAsync(work, Guid.NewGuid()));
        Assert.Equal(SeriesPlacementChoiceResult.WorkNotFound, await _placements.SetChoiceAsync(Guid.NewGuid(), null));
        Assert.Null(await ShelfOfAsync(work));
        Assert.Equal(0, await LockOfAsync(work));
    }

    [Fact]
    public async Task LinkingManifests_NeverMovesALockedWork_EvenWhenAManifestListsItElsewhere()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var listing = await CreateShelfAsync("QListing", "Listing");
        var work = await CreateWorkAsync();
        await _placements.SetChoiceAsync(work, chosen);

        await LinkAsync(listing, "QListing", work, "Item");

        Assert.Equal(chosen, await ShelfOfAsync(work));
        Assert.Equal([chosen], await ShelfItemOwnersAsync(work));
    }

    [Fact]
    public async Task LinkingManifests_KeepsALockedWorkOnItsShelf_WhenTwoSeriesTieAtMainSequence_AndRaisesNoAmbiguity()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var first = await CreateShelfAsync("QFirst", "First");
        var second = await CreateShelfAsync("QSecond", "Second");
        var work = await CreateWorkAsync();
        await LinkAsync(first, "QFirst", work, "Item");

        // Without a choice the second main-sequence listing makes the work ambiguous, and it leaves its shelf.
        var ambiguous = await LinkAsync(second, "QSecond", work, "Item");
        Assert.Single(ambiguous);
        Assert.Null(await ShelfOfAsync(work));

        await _placements.SetChoiceAsync(work, chosen);
        var afterChoice = await LinkAsync(second, "QSecond", work, "Item");
        var again = await LinkAsync(first, "QFirst", work, "Item");

        Assert.Empty(afterChoice);
        Assert.Empty(again);
        Assert.Equal(chosen, await ShelfOfAsync(work));
    }

    [Fact]
    public async Task LinkingManifests_KeepsANoSeriesChoice_WhenAManifestListsTheWork()
    {
        var listing = await CreateShelfAsync("QListing", "Listing");
        var work = await CreateWorkAsync();
        await _placements.SetChoiceAsync(work, collectionId: null);

        await LinkAsync(listing, "QListing", work, "Item");

        Assert.Null(await ShelfOfAsync(work));
        Assert.Empty(await ShelfItemOwnersAsync(work));
        Assert.Equal(1, await LockOfAsync(work));
    }

    [Fact]
    public async Task RefreshingTheChosenSeriesManifest_UpdatesThePositionButNotTheShelf()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var work = await CreateWorkAsync();
        await _placements.SetChoiceAsync(work, chosen);

        await LinkAsync(chosen, "QChosen", work, "Item", sortOrder: 7);

        Assert.Equal(chosen, await ShelfOfAsync(work));
        Assert.Equal(7d, await ItemSortOrderAsync(chosen, work));
    }

    [Fact]
    public async Task AssigningOrClearingAShelf_NeverMovesALockedWork_ButMovesAnUnlockedOne()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var other = await CreateShelfAsync("QOther", "Other");
        var locked = await CreateWorkAsync();
        var free = await CreateWorkAsync();
        await _placements.SetChoiceAsync(locked, chosen);
        await AssignAsync(free, chosen);

        await _collections.AssignWorkToCollectionAsync(locked, other);
        await _collections.AssignWorkToCollectionAsync(free, other);
        await _collections.ClearWorkCollectionAsync(locked);

        Assert.Equal(chosen, await ShelfOfAsync(locked));
        Assert.Equal(other, await ShelfOfAsync(free));
    }

    [Fact]
    public async Task ReleasingTheChoice_UnlocksTheWorkAndTakesItOffItsShelf_SoPlacementCanDecideAgain()
    {
        var chosen = await CreateShelfAsync("QChosen", "Chosen");
        var work = await CreateWorkAsync();
        await _placements.SetChoiceAsync(work, chosen);

        await _placements.ReleaseChoiceAsync(work);

        Assert.Null(await _placements.GetLockAsync(work));
        Assert.Null(await ShelfOfAsync(work));
        Assert.Empty(await ShelfItemOwnersAsync(work));
    }

    [Fact]
    public async Task TheMatchingRead_ShowsTheCurrentSeries_TheListedSeries_AndOtherSeriesOfTheSameMediaTypeOnly()
    {
        var current = await CreateShelfAsync("QCurrent", "Dune Novels");
        var listing = await CreateShelfAsync("QListing", "Dune Universe Reading Order");
        await CreateShelfAsync("QSpice", "Spice Wars");
        await CreateShelfAsync("QAudio", "Dune Audio", mediaType: "Audiobooks");
        var work = await CreateWorkAsync();
        await LinkAsync(listing, "QListing", work, "Item", sortOrder: 3, scope: SeriesMembershipScopeNames.Supplementary);
        await _placements.SetChoiceAsync(work, current);

        var view = await _placements.GetViewAsync(work, query: "dune", candidateLimit: 10);

        Assert.NotNull(view);
        Assert.True(view!.IsPersonChoice);
        Assert.Equal("Dune Novels", view.Current!.Label);
        var listed = Assert.Single(view.Listed);
        Assert.Equal(listing, listed.CollectionId);
        Assert.Equal(SeriesMembershipScopeNames.Supplementary, listed.Scope);
        Assert.Equal("3", listed.PositionLabel);
        // The search matches by name, never offers the current or listed Series again, and never offers another media type.
        Assert.Empty(view.Candidates);

        var all = await _placements.GetViewAsync(work, query: null, candidateLimit: 10);
        Assert.Equal(["Spice Wars"], all!.Candidates.Select(c => c.Label));
        var bounded = await _placements.GetViewAsync(work, query: null, candidateLimit: 0);
        Assert.Empty(bounded!.Candidates);
    }

    [Fact]
    public async Task TheMatchingRead_TreatsPercentAndUnderscoreInTheSearchAsPlainText()
    {
        await CreateShelfAsync("QA", "100% Sure");
        await CreateShelfAsync("QB", "Plain");
        var work = await CreateWorkAsync();

        var view = await _placements.GetViewAsync(work, query: "%", candidateLimit: 10);

        Assert.Equal(["100% Sure"], view!.Candidates.Select(c => c.Label));
    }

    [Fact]
    public async Task ATieThatIsNotAtMainSequence_GoesToTheSeriesTheClaimNames_EvenWhenTheClaimIsStoredOnlyOnTheFile()
    {
        var first = await CreateShelfAsync("QA", "Series A");
        var second = await CreateShelfAsync("QB", "Series B");
        var withClaim = await CreateWorkAsync();
        var withoutClaim = await CreateWorkAsync();
        var asset = AddFile(withClaim);
        AddFile(withoutClaim);
        SetCanonical(asset, "series_qid", "QB");

        foreach (var work in new[] { withClaim, withoutClaim })
        {
            await LinkAsync(first, "QA", work, "Item", scope: SeriesMembershipScopeNames.Supplementary);
            await LinkAsync(second, "QB", work, "Item", scope: SeriesMembershipScopeNames.Supplementary);
        }

        Assert.Equal(second, await ShelfOfAsync(withClaim));
        // No claim: the lowest Series id wins, the same answer every caller gets.
        Assert.Equal(first, await ShelfOfAsync(withoutClaim));
    }

    [Fact]
    public async Task EveryEntityOfAWork_IsReturnedForReview_WithTheEarliestNormalOriginalAsTheStableKey()
    {
        var work = await CreateWorkAsync();
        var firstFile = AddFile(work);
        var secondFile = AddFile(work);
        var orphan = AddFile(work, orphaned: true);

        var entities = await _placements.GetReviewEntitiesAsync(work);

        Assert.Equal(firstFile, entities.StableAssetId);
        Assert.Equal(new HashSet<Guid> { work, firstFile, secondFile, orphan }, entities.EntityIds.ToHashSet());
    }

    [Fact]
    public async Task AnEditorEntity_ResolvesToItsWork_FromTheWorkItsEditionOrItsFile()
    {
        var work = await CreateWorkAsync();
        var asset = AddFile(work);
        using var conn = _db.CreateConnection();
        var edition = conn.ExecuteScalar<Guid>("SELECT edition_id FROM media_assets WHERE id = @asset;", new { asset });

        Assert.Equal(work, await _placements.ResolveWorkIdAsync(work));
        Assert.Equal(work, await _placements.ResolveWorkIdAsync(edition));
        Assert.Equal(work, await _placements.ResolveWorkIdAsync(asset));
        Assert.Null(await _placements.ResolveWorkIdAsync(Guid.NewGuid()));
    }

    // ----------------------------------------------------------------------------------------------------------------

    private async Task<Guid> CreateShelfAsync(string qid, string name, CollectionType type = CollectionType.ContentGroup, string mediaType = "Books")
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
            type,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);
        return await _collections.UpsertAsync(collection);
    }

    private Task<Guid> CreateWorkAsync()
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO works (id, collection_id, media_type, work_kind) VALUES (@id, NULL, 'Books', 'standalone');",
            new { id });
        return Task.FromResult(id);
    }

    private Guid AddFile(Guid workId, bool orphaned = false)
    {
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO editions (id, work_id) VALUES (@edition, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, is_orphaned)
            VALUES (@asset, @edition, @hash, @path, @status, @orphaned);
            """,
            new
            {
                edition,
                workId,
                asset,
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{Guid.NewGuid():N}.epub",
                status = orphaned ? "Orphaned" : "Normal",
                orphaned = orphaned ? 1 : 0,
            });
        return asset;
    }

    private void SetCanonical(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, @now);",
            new { entityId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private async Task AssignAsync(Guid workId, Guid collectionId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE works SET collection_id = @collectionId WHERE id = @workId;", new { collectionId, workId });
    }

    private void InsertCollectionItem(Guid collectionId, Guid workId)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO collection_items (id, collection_id, work_id, sort_order, progress_state, added_at) VALUES (@id, @collectionId, @workId, 0, 'not_started', @now);",
            new { id = Guid.NewGuid(), collectionId, workId, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private async Task<IReadOnlyList<MediaEngine.Domain.Services.AmbiguousSeriesPlacement>> LinkAsync(
        Guid shelf,
        string seriesQid,
        Guid workId,
        string label,
        double sortOrder = 1,
        string scope = SeriesMembershipScopeNames.MainSequence)
    {
        var now = DateTimeOffset.UtcNow;
        var items = new List<SeriesManifestItemRecord>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CollectionId = shelf,
                SeriesQid = seriesQid,
                ItemQid = $"Q-{seriesQid}-{label}",
                ItemLabel = label,
                MediaType = "Books",
                ParsedOrdinal = sortOrder,
                RawOrdinal = sortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SortOrder = sortOrder,
                OrderSource = "SeriesOrdinal",
                OwnershipState = "Owned",
                LinkedWorkId = workId,
                MembershipScope = scope,
                SourcePropertiesJson = "[]",
                RelationshipsJson = "[]",
                LastHydratedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
        };
        await _manifests.UpsertManifestAsync(
            new SeriesManifestHydration
            {
                SeriesQid = seriesQid,
                CollectionId = shelf,
                SeriesLabel = seriesQid,
                WarningsJson = "[]",
                ApiMetadataJson = "{}",
                LastHydratedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            items);
        return await _manifests.LinkOwnedWorksAsync(shelf, items);
    }

    private async Task<Guid?> ShelfOfAsync(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid?>("SELECT collection_id FROM works WHERE id = @workId;", new { workId });
    }

    private async Task<int> LockOfAsync(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT series_placement_locked FROM works WHERE id = @workId;", new { workId });
    }

    private async Task<List<Guid>> ShelfItemOwnersAsync(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<Guid>("SELECT collection_id FROM collection_items WHERE work_id = @workId;", new { workId })).ToList();
    }

    private async Task<double> ItemSortOrderAsync(Guid collectionId, Guid workId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<double>(
            "SELECT sort_order FROM collection_items WHERE collection_id = @collectionId AND work_id = @workId;",
            new { collectionId, workId });
    }
}
