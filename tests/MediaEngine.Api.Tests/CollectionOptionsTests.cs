using Dapper;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Collection options (Collections and lists spec WP2): seasonal window, at most N, random order with a daily seed
/// and minimum items, read through the same services the Dashboard and apps use. The clock is injected so a test
/// chooses the day.
/// </summary>
public sealed class CollectionOptionsTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly CollectionRepository _repository;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly CollectionCatalogReadService _catalog;

    public CollectionOptionsTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_options_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        _repository = new CollectionRepository(_database);
        _catalog = new CollectionCatalogReadService(
            _repository,
            new SeriesManifestRepository(_database),
            new PersonRepository(_database),
            new ArtworkPaletteService(),
            new CollectionMediaLookupReadService(_database),
            _database,
            NullLogger<CollectionCatalogReadService>.Instance,
            _clock);
    }

    [Fact]
    public async Task SeasonalCollection_IsAbsentFromReadsOutsideItsWindowAndPresentInside()
    {
        var collection = await CreateCollectionAsync("Halloween", workCount: 4, seasonFrom: "10-01", seasonTo: "11-01");

        _clock.Set(2026, 10, 15);
        Assert.Contains(await _catalog.GetCatalogAsync(null), entry => entry.Id == collection.Id);
        Assert.NotNull(await _catalog.GetSummaryAsync(collection.Id, null));
        Assert.False((await _catalog.GetItemPageAsync(collection.Id, null, 0, 10, new HashSet<Guid>(await AllWorkIdsAsync()))).Forbidden);

        _clock.Set(2026, 11, 15);
        Assert.DoesNotContain(await _catalog.GetCatalogAsync(null), entry => entry.Id == collection.Id);
        Assert.Null(await _catalog.GetSummaryAsync(collection.Id, null));
        Assert.True((await _catalog.GetItemPageAsync(collection.Id, null, 0, 10, new HashSet<Guid>(await AllWorkIdsAsync()))).Forbidden);
    }

    [Fact]
    public async Task SeasonalCollection_StaysInTheEditorsListWithAHiddenUntilDate()
    {
        var collection = await CreateCollectionAsync("Halloween", workCount: 3, seasonFrom: "10-01", seasonTo: "11-01");

        _clock.Set(2026, 11, 15);
        var managed = Assert.Single(await _catalog.GetManagedAsync(null));
        Assert.Equal(collection.Id, managed.Id);
        Assert.Equal("2027-10-01", managed.HiddenUntil);
        Assert.Equal("10-01", managed.SeasonFrom);
        Assert.Equal("11-01", managed.SeasonTo);

        _clock.Set(2026, 10, 31);
        Assert.Null(Assert.Single(await _catalog.GetManagedAsync(null)).HiddenUntil);
    }

    [Fact]
    public async Task SeasonalWindow_AcrossNewYearIsOpenOnBothSides()
    {
        var collection = await CreateCollectionAsync("Festive", workCount: 3, seasonFrom: "12-01", seasonTo: "01-02");

        foreach (var (month, day, shown) in new[] { (12, 1, true), (12, 25, true), (1, 2, true), (1, 3, false), (6, 1, false) })
        {
            _clock.Set(2026, month, day);
            Assert.Equal(shown, (await _catalog.GetCatalogAsync(null)).Any(entry => entry.Id == collection.Id));
        }
    }

    [Fact]
    public async Task AtMostN_LimitsTheCollectionPageTheCountAndTheManagedList()
    {
        var collection = await CreateCollectionAsync("Top five", workCount: 12, maxItems: 5);

        var items = await _catalog.GetItemsAsync(collection.Id, null, 100);
        var entry = Assert.Single(await _catalog.GetCatalogAsync(null));
        var managed = Assert.Single(await _catalog.GetManagedAsync(null));

        Assert.Equal(5, items.Items.Count);
        Assert.Equal(5, entry.ItemCount);
        Assert.Equal(5, managed.ItemCount);
        Assert.Equal(5, managed.MaxItems);
    }

    [Fact]
    public async Task RandomSample_IsTheSameAllDayAndDifferentTheNextDay()
    {
        var collection = await CreateCollectionAsync("Twenty at random", workCount: 60, maxItems: 20, sortField: "random");

        _clock.Set(2026, 10, 15, hour: 8);
        var morning = (await _catalog.GetItemsAsync(collection.Id, null, 100)).Items.Select(item => item.WorkId).ToList();
        _clock.Set(2026, 10, 15, hour: 22);
        var evening = (await _catalog.GetItemsAsync(collection.Id, null, 100)).Items.Select(item => item.WorkId).ToList();
        _clock.Set(2026, 10, 16, hour: 8);
        var nextDay = (await _catalog.GetItemsAsync(collection.Id, null, 100)).Items.Select(item => item.WorkId).ToList();

        Assert.Equal(20, morning.Count);
        Assert.Equal(morning, evening);
        Assert.Equal(20, nextDay.Count);
        Assert.NotEqual(morning.Order(), nextDay.Order());
    }

    [Fact]
    public async Task MinimumItems_HidesAShortCollectionFromTheCollectionsPageOnlyWhenChosen()
    {
        var chosen = await CreateCollectionAsync("Needs five", workCount: 3, minItems: 5);
        var unchosen = await CreateCollectionAsync("Two is fine", workCount: 2);

        var ids = (await _catalog.GetCatalogAsync(null)).Select(entry => entry.Id).ToList();

        Assert.DoesNotContain(chosen.Id, ids);
        Assert.Contains(unchosen.Id, ids);
    }

    [Fact]
    public async Task MinimumItems_CountsTheCapNotTheMembers()
    {
        var collection = await CreateCollectionAsync("Capped below minimum", workCount: 8, minItems: 5, maxItems: 4);

        Assert.DoesNotContain(await _catalog.GetCatalogAsync(null), entry => entry.Id == collection.Id);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (ObjectDisposedException) { }
        try { File.Delete(_databasePath); } catch (IOException) { }
    }

    private async Task<Collection> CreateCollectionAsync(
        string name,
        int workCount,
        int minItems = 0,
        int? maxItems = null,
        string? seasonFrom = null,
        string? seasonTo = null,
        string? sortField = null)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            IsEnabled = true,
            MinItems = minItems,
            MaxItems = maxItems,
            SeasonFrom = seasonFrom,
            SeasonTo = seasonTo,
            SortField = sortField,
        };
        collection.RestoreDefinition(
            CollectionType.Custom,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Asc,
            CollectionUniverseStatus.Unknown);

        var items = new List<CollectionItem>();
        for (var index = 0; index < workCount; index++)
        {
            var workId = await SeedOwnedBookAsync($"{name} {index:00}");
            items.Add(new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = collection.Id,
                WorkId = workId,
                SortOrder = index,
                AddedAt = DateTimeOffset.UtcNow,
            });
        }

        await _repository.CreateManagedCollectionAsync(collection, items);
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

    private async Task<List<Guid>> AllWorkIdsAsync()
    {
        using var connection = _database.CreateConnection();
        var rows = await connection.QueryAsync<byte[]>("SELECT id FROM works");
        return rows.Select(row => GuidSql.FromDb(row)).ToList();
    }

    private sealed class ClockStub(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(int year, int month, int day, int hour = 12) =>
            _now = new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero);
    }
}
