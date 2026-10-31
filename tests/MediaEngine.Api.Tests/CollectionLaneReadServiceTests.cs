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
/// Lanes (Collections and lists spec WP4): a collection with Show on Home is a lane for the profiles that may see it,
/// minus the ones that hid it, in the administrator's order, with private collections only for their owner. Read through
/// the same services the display composer uses, against a real data store.
/// </summary>
public sealed class CollectionLaneReadServiceTests : IDisposable
{
    private const string Home = CollectionLanePolicy.HomeLocation;

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly CollectionRepository _collections;
    private readonly CollectionPlacementRepository _placements;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly CollectionLaneReadService _lanes;
    private readonly Profile _adult;
    private readonly Profile _other;

    public CollectionLaneReadServiceTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_lanes_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        _collections = new CollectionRepository(_database);
        _placements = new CollectionPlacementRepository(_database);
        var catalog = new CollectionCatalogReadService(
            _collections,
            new SeriesManifestRepository(_database),
            new PersonRepository(_database),
            new ArtworkPaletteService(),
            new CollectionMediaLookupReadService(_database),
            _database,
            NullLogger<CollectionCatalogReadService>.Instance,
            _clock);
        _lanes = new CollectionLaneReadService(
            _placements, _collections, catalog, NullLogger<CollectionLaneReadService>.Instance, _clock);

        var profiles = new ProfileRepository(_database);
        _adult = profiles.GetByIdAsync(Profile.SeedProfileId).GetAwaiter().GetResult()
                 ?? throw new InvalidOperationException("The seed profile is missing.");
        var otherId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profiles (id, display_name, created_at) VALUES (@otherId, 'Other', @now);",
                new { otherId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        _other = profiles.GetByIdAsync(otherId).GetAwaiter().GetResult()
                 ?? throw new InvalidOperationException("The second profile is missing.");
    }

    [Fact]
    public async Task CollectionShownOnHome_IsALaneForAnAdultProfile()
    {
        var collection = await CreateCollectionAsync("Saturday morning", workCount: 5);
        await ShowOnAsync(collection, Home);

        var lanes = await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null);

        var lane = Assert.Single(lanes);
        Assert.Equal(collection.Id, lane.CollectionId);
        Assert.Equal("Saturday morning", lane.Name);
        Assert.Equal(5, lane.Items.Count);
        Assert.True(lane.IsShared);
    }

    [Fact]
    public async Task CollectionNotShownOnHome_IsNotALane()
    {
        await CreateCollectionAsync("Only on the Collections page", workCount: 5);

        Assert.Empty(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));
    }

    [Fact]
    public async Task LaneAProfileHid_IsAbsentForThatProfileOnlyAndComesBackWhenShownAgain()
    {
        var collection = await CreateCollectionAsync("Hidden by one", workCount: 4);
        await ShowOnAsync(collection, Home);

        await _placements.SetProfileHiddenAsync(_adult.Id, collection.Id, Home, hidden: true);

        Assert.Empty(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));
        Assert.Single(await _lanes.GetLanesAsync(_other, Home, allowedWorkIds: null));
        Assert.Equal([collection.Id], (await _lanes.GetListingsAsync(_adult, Home, includeHiddenShared: false)).HiddenByMe.Select(lane => lane.CollectionId));

        await _placements.SetProfileHiddenAsync(_adult.Id, collection.Id, Home, hidden: false);

        Assert.Single(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));
    }

    [Fact]
    public async Task HidingALaneOnHome_DoesNotHideItOnAnAreaPage()
    {
        var collection = await CreateCollectionAsync("Home and Watch", workCount: 4);
        await ShowOnAsync(collection, Home, CollectionLanePolicy.WatchLocation);

        await _placements.SetProfileHiddenAsync(_adult.Id, collection.Id, Home, hidden: true);

        Assert.Empty(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));
        Assert.Single(await _lanes.GetLanesAsync(_adult, CollectionLanePolicy.WatchLocation, allowedWorkIds: null));
    }

    [Fact]
    public async Task PrivateCollection_IsALaneOnlyForItsOwner()
    {
        var mine = await CreateCollectionAsync("My private shelf", workCount: 4, owner: _adult);
        await ShowOnAsync(mine, Home);

        var forOwner = Assert.Single(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));
        Assert.Equal(mine.Id, forOwner.CollectionId);
        Assert.False(forOwner.IsShared);
        Assert.Empty(await _lanes.GetLanesAsync(_other, Home, allowedWorkIds: null));
        Assert.Empty(await _lanes.GetLanesAsync(profile: null, Home, allowedWorkIds: null));
    }

    [Fact]
    public async Task SharedLanesKeepTheAdministratorsOrder_AndTheProfilesOwnFollowInTheirOwnersOrder()
    {
        var first = await CreateCollectionAsync("First shared", workCount: 3);
        var second = await CreateCollectionAsync("Second shared", workCount: 3);
        var privateB = await CreateCollectionAsync("Private B", workCount: 3, owner: _adult);
        var privateA = await CreateCollectionAsync("Private A", workCount: 3, owner: _adult);
        await ShowOnAsync(privateB, Home);
        await ShowOnAsync(first, Home);
        await ShowOnAsync(privateA, Home);
        await ShowOnAsync(second, Home);

        Assert.Equal(
            ["First shared", "Second shared", "Private B", "Private A"],
            (await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null)).Select(lane => lane.Name));

        await _placements.SaveSharedOrderAsync(Home, [(second.Id, true), (first.Id, true)]);
        await _placements.SaveSharedOrderAsync(Home, [(privateA.Id, true), (privateB.Id, true)]);

        Assert.Equal(
            ["Second shared", "First shared", "Private A", "Private B"],
            (await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null)).Select(lane => lane.Name));
    }

    [Fact]
    public async Task LaneSwitchedOffForEveryone_IsSkippedButStillListedForTheAdministrator()
    {
        var collection = await CreateCollectionAsync("Off for everyone", workCount: 4);
        await ShowOnAsync(collection, Home);

        await _placements.SaveSharedOrderAsync(Home, [(collection.Id, false)]);

        Assert.Empty(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));
        var forAdministrator = (await _lanes.GetListingsAsync(_adult, Home, includeHiddenShared: true)).Shared;
        Assert.False(Assert.Single(forAdministrator).Visible);
        Assert.Empty((await _lanes.GetListingsAsync(_adult, Home, includeHiddenShared: false)).Shared);
    }

    [Fact]
    public async Task LaneUnderItsMinimum_IsSkippedSilently_AndTheChosenMinimumWins()
    {
        var short1 = await CreateCollectionAsync("Two titles", workCount: 2);
        var chosen = await CreateCollectionAsync("Two is enough", workCount: 2, minItems: 2);
        await ShowOnAsync(short1, Home);
        await ShowOnAsync(chosen, Home);

        var lanes = await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null);

        Assert.Equal(["Two is enough"], lanes.Select(lane => lane.Name));
    }

    [Fact]
    public async Task KidsLevels_ShrinkALaneToWhatTheProfileMaySee_AndDropItUnderTheMinimum()
    {
        var collection = await CreateCollectionAsync("Family night", workCount: 5);
        await ShowOnAsync(collection, Home);
        var all = await AllWorkIdsAsync();

        var four = await _lanes.GetLanesAsync(_adult, Home, all.Take(4).ToHashSet());
        var two = await _lanes.GetLanesAsync(_adult, Home, all.Take(2).ToHashSet());

        Assert.Equal(4, Assert.Single(four).Items.Count);
        Assert.Empty(two);
    }

    [Fact]
    public async Task Lane_HoldsAtMostTwentyItems()
    {
        var collection = await CreateCollectionAsync("Thirty", workCount: 30);
        await ShowOnAsync(collection, Home);

        Assert.Equal(20, Assert.Single(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null)).Items.Count);
    }

    [Fact]
    public async Task SeasonalLane_IsAbsentOutsideItsWindowAndPresentInside()
    {
        var collection = await CreateCollectionAsync("Halloween", workCount: 4, seasonFrom: "10-01", seasonTo: "11-01");
        await ShowOnAsync(collection, Home);

        _clock.Set(2026, 10, 15);
        Assert.Single(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));

        _clock.Set(2026, 11, 15);
        Assert.Empty(await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null));
    }

    [Fact]
    public async Task ShowOnSync_AddsRemovesAndLeavesOtherLocationsAlone_AndForgetsHidesOfRemovedLanes()
    {
        var collection = await CreateCollectionAsync("Synced", workCount: 4);
        await _placements.UpsertAsync(new CollectionPlacement
        {
            Id = Guid.NewGuid(),
            CollectionId = collection.Id,
            Location = "collections",
            DisplayMode = "grid",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await _placements.SyncSharedLocationsAsync(
            collection.Id, [Home, CollectionLanePolicy.ReadLocation], CollectionLanePolicy.Locations, 20);
        await _placements.SetProfileHiddenAsync(_adult.Id, collection.Id, CollectionLanePolicy.ReadLocation, hidden: true);
        Assert.Equal(
            [CollectionLanePolicy.ReadLocation, "collections", Home],
            (await _placements.GetByCollectionIdAsync(collection.Id)).Select(placement => placement.Location).Order(StringComparer.Ordinal));

        await _placements.SyncSharedLocationsAsync(
            collection.Id, [Home, CollectionLanePolicy.ListenLocation], CollectionLanePolicy.Locations, 20);

        Assert.Equal(
            [CollectionLanePolicy.ListenLocation, "collections", Home],
            (await _placements.GetByCollectionIdAsync(collection.Id)).Select(placement => placement.Location).Order(StringComparer.Ordinal));
        Assert.Empty(await _placements.GetHiddenCollectionIdsAsync(_adult.Id, CollectionLanePolicy.ReadLocation));
    }

    [Fact]
    public async Task NewLane_JoinsTheEndOfItsLocation()
    {
        var first = await CreateCollectionAsync("Earlier", workCount: 3);
        var second = await CreateCollectionAsync("Later", workCount: 3);

        await _placements.SyncSharedLocationsAsync(first.Id, [Home], CollectionLanePolicy.Locations, 20);
        await _placements.SyncSharedLocationsAsync(second.Id, [Home], CollectionLanePolicy.Locations, 20);

        Assert.Equal(
            ["Earlier", "Later"],
            (await _lanes.GetLanesAsync(_adult, Home, allowedWorkIds: null)).Select(lane => lane.Name));
    }

    [Theory]
    [InlineData("home", "home")]
    [InlineData("Watch", "area:watch")]
    [InlineData(" read ", "area:read")]
    [InlineData("listen", "area:listen")]
    public void AreaKeys_MapToTheStoredLocations(string key, string location) =>
        Assert.Equal(location, CollectionLanePolicy.LocationFor(key));

    [Fact]
    public void ShowOnRequest_WithAnUnknownPlace_IsRefused()
    {
        Assert.False(CollectionLanePolicy.TryParseShowOn(["home", "collections"], out _));
        Assert.True(CollectionLanePolicy.TryParseShowOn(["listen", "home", "home"], out var parsed));
        Assert.Equal([Home, CollectionLanePolicy.ListenLocation], parsed);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (ObjectDisposedException) { }
        try { File.Delete(_databasePath); } catch (IOException) { }
    }

    private async Task ShowOnAsync(Collection collection, params string[] locations) =>
        await _placements.SyncSharedLocationsAsync(collection.Id, locations, CollectionLanePolicy.Locations, 20);

    private async Task<Collection> CreateCollectionAsync(
        string name,
        int workCount,
        int minItems = 0,
        string? seasonFrom = null,
        string? seasonTo = null,
        Profile? owner = null)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            IsEnabled = true,
            MinItems = minItems,
            SeasonFrom = seasonFrom,
            SeasonTo = seasonTo,
        };
        collection.RestoreDefinition(
            CollectionType.Custom,
            owner is null ? CollectionScope.Library : CollectionScope.User,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Asc,
            CollectionUniverseStatus.Unknown);
        if (owner is not null)
        {
            collection.OwnerKind = ContainerOwnerKind.Profile;
            collection.Audience = ContainerAudience.Private;
            collection.ProfileId = owner.Id;
        }

        var items = new List<CollectionItem>();
        for (var index = 0; index < workCount; index++)
        {
            items.Add(new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = collection.Id,
                WorkId = await SeedOwnedBookAsync($"{name} {index:00}"),
                SortOrder = index,
                AddedAt = DateTimeOffset.UtcNow,
            });
        }

        await _collections.CreateManagedCollectionAsync(collection, items);
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
        var rows = await connection.QueryAsync<byte[]>("SELECT id FROM works ORDER BY rowid");
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
