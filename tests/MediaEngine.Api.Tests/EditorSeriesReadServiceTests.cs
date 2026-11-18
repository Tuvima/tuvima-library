using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Series editor's header reads (universal editor v8 D1 and D6): the badge's two halves, the kinds of title a Series holds, and one
/// kind's titles, with titles the person does not own listed only when the viewing profile has "Show missing" on for the Series. Real
/// temp SQLite and the real repositories; nothing is mocked.
/// </summary>
public sealed class EditorSeriesReadServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-series-read-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _collections;
    private readonly ProfileSequencePreferencesRepository _preferences;
    private readonly EditorSeriesReadService _service;
    private readonly Guid _series;
    private readonly Guid _profile = Guid.NewGuid();

    public EditorSeriesReadServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _collections = new CollectionRepository(_db);
        _preferences = new ProfileSequencePreferencesRepository(_db);
        _service = new EditorSeriesReadService(_collections, _db, new SeriesManifestRepository(_db), _preferences);
        _series = SeedSeries("Dune");
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, role, created_at) VALUES (@id, 'Owner', 'Administrator', @createdAt);",
            new { id = _profile, createdAt = DateTimeOffset.UtcNow.ToString("O") });
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    [Fact]
    public async Task MediaTypes_ListsOnlyTheKindsTheSeriesHas_InTheLibrarysOrder()
    {
        SeedWork(_series, "Movies", "Dune: Part Two", 2);
        SeedWork(_series, "Books", "Dune", 1);
        SeedWork(_series, "Books", "Dune Messiah", 2);

        var types = await _service.ReadMediaTypesAsync(_series);

        Assert.Equal(["Books", "Movies"], types.Select(type => type.Key));
        Assert.Equal([2, 1], types.Select(type => type.OwnedCount));
        Assert.Equal(["Books", "Movies"], types.Select(type => type.Label));
    }

    [Fact]
    public async Task MediaTypes_CallsTelevisionTV_AndLeavesOutAKindWithNothingOwned()
    {
        SeedWork(_series, "TV", "Dune: Prophecy", 1);
        SeedWork(_series, "Books", "Dune", 1, catalogOnly: true);

        var types = await _service.ReadMediaTypesAsync(_series);

        Assert.Equal("TV", Assert.Single(types).Label);
    }

    [Fact]
    public async Task Titles_ListsEveryOwnedTitleOfThatKind_InTheSeriesOrder_AndNoOtherKind()
    {
        var two = SeedWork(_series, "Books", "Dune Messiah", 2);
        var one = SeedWork(_series, "Books", "Dune", 1);
        SeedWork(_series, "Movies", "Dune: Part One", 1);

        var titles = (await _service.GetTitlesAsync(_series, "Books", null, 0, _profile))!;

        Assert.Equal("Books", titles.MediaType);
        Assert.Equal(2, titles.Total);
        Assert.Equal([one, two], titles.Items.Select(item => item.Id!.Value));
        Assert.Equal(["1", "2"], titles.Items.Select(item => item.Number));
        Assert.All(titles.Items, item => Assert.True(item.Owned));
        Assert.False(titles.ShowingMissing);
    }

    [Fact]
    public async Task Titles_LeavesOutTitlesNotOwned_UntilTheProfileTurnsShowMissingOn()
    {
        SeedWork(_series, "Books", "Dune", 1);
        SeedWork(_series, "Books", "Children of Dune", 3, catalogOnly: true);

        var hidden = (await _service.GetTitlesAsync(_series, "Books", null, 0, _profile))!;
        await _preferences.SaveAsync(_profile, "books", _series.ToString("D"), showMissing: true);
        var shown = (await _service.GetTitlesAsync(_series, "Books", null, 0, _profile))!;

        Assert.Equal(["Dune"], hidden.Items.Select(item => item.Title));
        Assert.Equal(["Dune", "Children of Dune"], shown.Items.Select(item => item.Title));
        Assert.True(shown.ShowingMissing);
        var missing = shown.Items.Last();
        Assert.False(missing.Owned);
        Assert.Null(missing.Id);
    }

    [Fact]
    public async Task Titles_TheTickBelongsToTheProfileAndTheKind_SoAnotherKindOrProfileStaysHidden()
    {
        SeedWork(_series, "Books", "Children of Dune", 3, catalogOnly: true);
        SeedWork(_series, "Movies", "Dune: Part Three", 3, catalogOnly: true);
        await _preferences.SaveAsync(_profile, "books", _series.ToString("D"), showMissing: true);

        var movies = (await _service.GetTitlesAsync(_series, "Movies", null, 0, _profile))!;
        var anonymous = (await _service.GetTitlesAsync(_series, "Books", null, 0, profileId: null))!;

        Assert.Empty(movies.Items);
        Assert.Empty(anonymous.Items);
    }

    [Fact]
    public async Task Titles_AreSearchedAndPaged_SoABigSeriesIsNeverLoadedWhole()
    {
        for (var number = 1; number <= EditorSeriesReadService.PageSize + 5; number++)
        {
            SeedWork(_series, "Books", $"Volume {number:000}", number);
        }

        var first = (await _service.GetTitlesAsync(_series, "Books", null, 0, _profile))!;
        var second = (await _service.GetTitlesAsync(_series, "Books", null, EditorSeriesReadService.PageSize, _profile))!;
        var found = (await _service.GetTitlesAsync(_series, "Books", "volume 054", 0, _profile))!;

        Assert.Equal(EditorSeriesReadService.PageSize + 5, first.Total);
        Assert.Equal(EditorSeriesReadService.PageSize, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Equal(["Volume 054"], found.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task Titles_AreNull_ForAnUnknownKind_OrASeriesThatIsGone()
    {
        Assert.Null(await _service.GetTitlesAsync(_series, "Gadgets", null, 0, _profile));
        Assert.Null(await _service.GetTitlesAsync(Guid.NewGuid(), "Books", null, 0, _profile));
    }

    [Fact]
    public async Task Badge_ReadsRetailFromTheMembersMatchesAndCanonicalFromTheSeriesWikidataLink()
    {
        var film = SeedWork(_series, "Movies", "Dune: Part One", 1);
        SeedValue(film, "tmdb_id", "438631");

        var both = await _service.ReadBadgeAsync(_series, "Q190192");
        var retailOnly = await _service.ReadBadgeAsync(_series, null);

        Assert.True(both.RetailMatched);
        Assert.True(both.CanonicalMatched);
        Assert.NotNull(both.ProviderName);
        Assert.True(retailOnly.RetailMatched);
        Assert.False(retailOnly.CanonicalMatched);
    }

    [Fact]
    public async Task Badge_ReadsRetailAsNotMatched_WhenAnyOwnedMemberHasNoProviderMatch_OrTheSeriesHasNoMembers()
    {
        var matched = SeedWork(_series, "Movies", "Dune: Part One", 1);
        SeedValue(matched, "tmdb_id", "438631");
        var empty = SeedSeries("Empty");

        Assert.True((await _service.ReadBadgeAsync(_series, "Q1")).RetailMatched);
        SeedWork(_series, "Movies", "Dune: Part Two", 2);

        var mixed = await _service.ReadBadgeAsync(_series, "Q1");
        var none = await _service.ReadBadgeAsync(empty, null);

        Assert.False(mixed.RetailMatched);
        Assert.True(mixed.CanonicalMatched);
        Assert.False(none.RetailMatched);
        Assert.False(none.CanonicalMatched);
    }

    private Guid SeedSeries(string name)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.Series, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        return _collections.UpsertAsync(collection).GetAwaiter().GetResult();
    }

    private Guid SeedWork(Guid collection, string mediaType, string title, int ordinal, bool catalogOnly = false)
    {
        var id = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, collection_id, ordinal, is_catalog_only)
            VALUES (@id, @mediaType, 'standalone', @collection, @ordinal, @catalogOnly);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'title', @title, @now);
            """,
            new
            {
                id = GuidSql.ToBlob(id),
                collection = GuidSql.ToBlob(collection),
                mediaType,
                ordinal,
                catalogOnly = catalogOnly ? 1 : 0,
                title,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return id;
    }

    private void SeedValue(Guid entity, string key, string value)
    {
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, @key, @value, @now);",
            new { id = GuidSql.ToBlob(entity), key, value, now = DateTimeOffset.UtcNow.ToString("O") });
    }
}
