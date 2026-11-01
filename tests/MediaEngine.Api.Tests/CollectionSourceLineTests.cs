using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Collections page and a collection's own page, for lists and sets (Collections and lists spec WP8, 4g): each curated tile says how
/// it fills up, an imported list carries its source and counts, a set knows its collections, and a list's page says "You own X of Y"
/// and lists the titles the library does not hold, except for a Kids profile.
/// </summary>
public sealed class CollectionSourceLineTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_sources_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly CollectionRepository _repository;
    private readonly CollectionListRepository _lists;
    private readonly CollectionCatalogReadService _catalog;
    private readonly StubProfiles _profiles = new();
    private readonly DetailComposerService _composer;

    public CollectionSourceLineTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        _repository = new CollectionRepository(_database);
        _lists = new CollectionListRepository(_database);
        _catalog = new CollectionCatalogReadService(
            _repository,
            new SeriesManifestRepository(_database),
            new PersonRepository(_database),
            new ArtworkPaletteService(),
            new CollectionMediaLookupReadService(_database),
            _database,
            NullLogger<CollectionCatalogReadService>.Instance,
            listRepo: _lists);
        _composer = new DetailComposerService(
            _database,
            new LibraryItemRepository(_database),
            new PersonRepository(_database),
            new EntityAssetRepository(_database),
            new CanonicalValueArrayRepository(_database),
            new SeriesManifestRepository(_database),
            new PersonCreditReadService(new CanonicalValueArrayRepository(_database), new PersonRepository(_database), _database),
            collectionBrowse: new CollectionBrowseReadService(_repository, _database, NullLogger<CollectionBrowseReadService>.Instance),
            collectionCatalog: _catalog,
            profiles: _profiles,
            collectionLists: _lists);
    }

    [Fact]
    public async Task EachCuratedCollection_SaysHowItFillsUp()
    {
        var handPicked = await CreateCollectionAsync("Favourites", CollectionResolution.Materialized);
        var rules = await CreateCollectionAsync("Long books", CollectionResolution.Query);
        var set = await CreateCollectionAsync("Decades", CollectionResolution.Materialized, type: CollectionType.Set);
        var pack = await CreateCollectionAsync("1990s", CollectionResolution.Query, parent: set.Id);
        var list = await CreateListCollectionAsync("Top films", entryCount: 4, matched: 2);

        var catalog = await _catalog.GetCatalogAsync(null);

        Assert.Equal(CollectionFillKinds.HandPicked, Entry(catalog, handPicked).FillKind);
        Assert.Equal(CollectionFillKinds.Rules, Entry(catalog, rules).FillKind);
        Assert.Equal(CollectionFillKinds.List, Entry(catalog, list).FillKind);
        var packEntry = Entry(catalog, pack);
        Assert.Equal(CollectionFillKinds.Pack, packEntry.FillKind);
        Assert.Equal(set.Id, packEntry.SetId);
        Assert.Equal("Decades", packEntry.SetName);
        var setEntry = Entry(catalog, set);
        Assert.Equal(CollectionFillKinds.Set, setEntry.FillKind);
        Assert.Equal(1, setEntry.ChildCollectionCount);
    }

    [Fact]
    public async Task AnImportedList_CarriesItsSourceRefreshAndCounts()
    {
        var list = await CreateListCollectionAsync("Top films", entryCount: 250, matched: 83);

        var source = Assert.IsType<CollectionCatalogListSourceDto>(Entry(await _catalog.GetCatalogAsync(null), list).ListSource);

        Assert.Equal("Letterboxd export", source.SourceLabel);
        Assert.Equal(250, source.EntryCount);
        Assert.Equal(83, source.OwnedCount);
        Assert.Equal(167, source.UnmatchedCount);
        Assert.Equal(ListImportChoices.RefreshWeekly, source.Refresh);
        Assert.NotNull(source.LastRefreshedAt);
    }

    [Fact]
    public async Task ASetWithNoOwnedTitlesInItsCollections_IsStillOnTheCollectionsPage()
    {
        var set = await CreateCollectionAsync("Awards", CollectionResolution.Materialized, type: CollectionType.Set);

        Assert.Contains(await _catalog.GetCatalogAsync(null), entry => entry.Id == set.Id);
    }

    [Fact]
    public async Task AKidsProfile_GetsNoListSourceOnTheTile()
    {
        var list = await CreateListCollectionAsync("Top films", entryCount: 4, matched: 1);

        var entry = Entry(await _catalog.GetCatalogAsync(new Profile { Id = Guid.NewGuid(), Role = ProfileRole.RestrictedProfile }), list);

        Assert.Equal(CollectionFillKinds.List, entry.FillKind);
        Assert.Null(entry.ListSource);
    }

    [Fact]
    public async Task AListsPage_SaysYouOwnXOfYAndListsTheTitlesNotInTheLibrary()
    {
        var list = await CreateListCollectionAsync("Top films", entryCount: 4, matched: 2);

        var page = await _composer.BuildAsync(DetailEntityType.Collection, list.Id, DetailPresentationContext.Watch);

        var info = Assert.IsType<DetailCollectionInfoViewModel>(page!.CollectionInfo);
        Assert.Equal(CollectionFillKinds.List, info.FillKind);
        var details = Assert.IsType<DetailCollectionListViewModel>(info.List);
        Assert.Equal(2, details.OwnedCount);
        Assert.Equal(4, details.EntryCount);
        Assert.Equal(["Missing One", "Missing Two"], details.Missing.Select(title => title.Title));
        Assert.Equal(1999, details.Missing[0].Year);
        var pill = Assert.Single(page.Metadata, item => item.Kind == "list_owned");
        Assert.Equal("You own 2 of 4", pill.Label);
    }

    [Fact]
    public async Task AListsPage_ForAKidsProfile_ShowsNeitherTheCountNorTheMissingTitles()
    {
        var list = await CreateListCollectionAsync("Top films", entryCount: 4, matched: 2);
        var kids = _profiles.AddKids();

        var page = await _composer.BuildAsync(DetailEntityType.Collection, list.Id, DetailPresentationContext.Watch, profileId: kids);

        Assert.Null(page!.CollectionInfo!.List);
        Assert.DoesNotContain(page.Metadata, item => item.Kind == "list_owned");
    }

    [Fact]
    public async Task ASetsPage_IsMarkedAsASetAndAnOrdinaryCollectionsPageIsNot()
    {
        var set = await CreateCollectionAsync("Decades", CollectionResolution.Materialized, type: CollectionType.Set);
        var plain = await CreateCollectionAsync("Favourites", CollectionResolution.Materialized);

        var setPage = await _composer.BuildAsync(DetailEntityType.Collection, set.Id, DetailPresentationContext.Watch);
        var plainPage = await _composer.BuildAsync(DetailEntityType.Collection, plain.Id, DetailPresentationContext.Watch);

        Assert.True(setPage!.CollectionInfo!.IsSet);
        Assert.Null(plainPage!.CollectionInfo);
    }

    [Fact]
    public async Task AListEntryTheLibraryCouldNotMatch_NeverReachesTheOwnedCountsOrTheTilesTitles()
    {
        var list = await CreateListCollectionAsync("Top films", entryCount: 4, matched: 2);

        var entry = Entry(await _catalog.GetCatalogAsync(null), list);

        // Only the two owned titles are members; the two missing ones are never counted as items.
        Assert.Equal(2, entry.ItemCount);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (ObjectDisposedException) { }
        try { File.Delete(_databasePath); } catch (IOException) { }
    }

    private static CollectionManagementCatalogDto Entry(IEnumerable<CollectionManagementCatalogDto> catalog, Collection collection) =>
        Assert.Single(catalog, entry => entry.Id == collection.Id);

    private async Task<Collection> CreateCollectionAsync(
        string name,
        CollectionResolution resolution,
        CollectionType type = CollectionType.Custom,
        Guid? parent = null,
        IReadOnlyList<Guid>? works = null)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            IsEnabled = true,
            ParentCollectionId = parent,
            MembershipMode = resolution == CollectionResolution.Query ? ContainerMembershipMode.Smart : ContainerMembershipMode.Manual,
            RuleJson = resolution == CollectionResolution.Query
                ? JsonSerializer.Serialize(CollectionRuleDefinition.SingleGroup(
                    [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Books" }]))
                : null,
        };
        collection.RestoreDefinition(
            type, CollectionScope.Library, resolution, CollectionMatchMode.All, CollectionSortDirection.Asc, CollectionUniverseStatus.Unknown);
        var items = (works ?? [])
            .Select((workId, index) => new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = collection.Id,
                WorkId = workId,
                SortOrder = index,
                AddedAt = DateTimeOffset.UtcNow,
            })
            .ToList();
        await _repository.CreateManagedCollectionAsync(collection, items);
        return collection;
    }

    private async Task<Collection> CreateListCollectionAsync(string name, int entryCount, int matched)
    {
        var owned = new List<Guid>();
        for (var index = 0; index < matched; index++)
        {
            owned.Add(await SeedOwnedBookAsync($"{name} owned {index}"));
        }

        var collection = await CreateCollectionAsync(name, CollectionResolution.Materialized, works: owned);
        var now = DateTimeOffset.UtcNow;
        var entries = new List<CollectionListEntry>();
        for (var index = 0; index < entryCount; index++)
        {
            var isOwned = index < matched;
            entries.Add(new CollectionListEntry
            {
                Id = Guid.NewGuid(),
                CollectionId = collection.Id,
                Position = index + 1,
                Title = isOwned ? $"Owned {index}" : index - matched == 0 ? "Missing One" : index - matched == 1 ? "Missing Two" : $"Missing {index - matched + 1}",
                Year = isOwned ? 2001 : 1999 + (index - matched),
                EntryKey = $"entry-{index}",
                Status = isOwned ? ListImportChoices.StatusMatched : ListImportChoices.StatusUnmatched,
                MatchedWorkId = isOwned ? owned[index] : null,
                OnList = true,
            });
        }

        await _lists.SaveAsync(
            new CollectionListSource
            {
                CollectionId = collection.Id,
                SourceKind = "letterboxd",
                SourceLabel = "Letterboxd export",
                SourceName = "watchlist.csv",
                Refresh = ListImportChoices.RefreshWeekly,
                SyncMode = ListImportChoices.SyncFollow,
                LastRefreshedAt = now,
                EntryCount = entryCount,
                MatchedCount = matched,
                UnmatchedCount = entryCount - matched,
                CreatedAt = now,
                UpdatedAt = now,
            },
            entries,
            owned.Select((workId, index) => new ListCollectionItemPlacement(workId, index, "list")).ToList());
        return collection;
    }

    private async Task<Guid> SeedOwnedBookAsync(string title)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, ownership, curator_state)
            VALUES (@workId, 'Books', 'standalone', 'Owned', 'Accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @hash, @path);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            """,
            new
            {
                workId,
                assetId,
                editionId = Guid.NewGuid(),
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.epub",
                title,
            });
        return workId;
    }

    // The detail page reads the caller's profile only to know whether it is a Kids profile.
    private sealed class StubProfiles : IProfileRepository
    {
        private readonly Dictionary<Guid, Profile> _byId = [];

        public Guid AddKids()
        {
            var profile = new Profile { Id = Guid.NewGuid(), DisplayName = "Kid", Role = ProfileRole.RestrictedProfile };
            _byId[profile.Id] = profile;
            return profile.Id;
        }

        public Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Profile>>(_byId.Values.ToList());

        public Task<Profile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_byId.GetValueOrDefault(id));

        public Task<bool> UpdateAsync(Profile profile, CancellationToken ct = default) => Task.FromResult(false);
    }
}
