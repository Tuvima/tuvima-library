using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Editing a Universe's characters, places and factions in the universal editor (universal editor v4, WP11): the rail's lists, adding a
/// thing by hand through the edit session (checked first, undone with the session), and the history the editor keeps for them.
/// </summary>
public sealed class EditorUniverseEditSessionTests : IDisposable
{
    private const string UniverseQid = "Q4024";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_editor_universe_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _collections;
    private readonly FictionalEntityRepository _entities;
    private readonly EntityTimelineRepository _timeline;
    private readonly ThingAddEditSessionChangeHandler _handler;
    private readonly EditorUniverseThingsService _things;

    public EditorUniverseEditSessionTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _db = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _collections = new CollectionRepository(_db);
        _entities = new FictionalEntityRepository(_db);
        _timeline = new EntityTimelineRepository(_db);
        _handler = new ThingAddEditSessionChangeHandler(_collections, _entities, _timeline);
        _things = new EditorUniverseThingsService(_entities, new EntityAssetRepository(_db), new PluginLoreRepository(_db));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp folder goes next */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    private async Task<Guid> SeedUniverseAsync(string? qid = UniverseQid)
    {
        var collection = new Collection { Id = Guid.NewGuid(), DisplayName = "Dune", WikidataQid = qid, CreatedAt = DateTimeOffset.UtcNow };
        collection.RestoreDefinition(
            CollectionType.Universe, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        await _collections.UpsertAsync(collection);
        return collection.Id;
    }

    private async Task SeedProviderThingAsync(string label, string subType)
    {
        await _entities.CreateAsync(new FictionalEntity
        {
            Id = Guid.NewGuid(),
            WikidataQid = "Q" + Random.Shared.Next(1000, 999999),
            Label = label,
            EntitySubType = subType,
            FictionalUniverseQid = UniverseQid,
            FictionalUniverseLabel = "Dune",
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static EditSessionChange Add(Guid universe, string? name, string? kind, string? description = null)
    {
        var fields = new List<EditSessionFieldChange>();
        if (name is not null)
        {
            fields.Add(new EditSessionFieldChange { Key = EditSessionThingAdd.NameKey, Value = name });
        }

        if (kind is not null)
        {
            fields.Add(new EditSessionFieldChange { Key = EditSessionThingAdd.KindKey, Value = kind });
        }

        if (description is not null)
        {
            fields.Add(new EditSessionFieldChange { Key = EditSessionThingAdd.DescriptionKey, Value = description });
        }

        return new EditSessionChange { Id = "c" + Guid.NewGuid().ToString("N"), Kind = EditSessionChangeKinds.ThingAdd, EntityId = universe, TargetId = Guid.NewGuid().ToString("N"), Fields = fields };
    }

    [Fact]
    public async Task AddingAFaction_CreatesAHandAddedThing_InThatUniverse_AndRecordsItInHistory()
    {
        var universe = await SeedUniverseAsync();
        var change = Add(universe, "House Atreides", "factions", "Rulers of Caladan");

        Assert.Null(await _handler.CheckAsync(change));
        var undo = await _handler.ApplyAsync(change);

        var created = Assert.Single(await _entities.GetByUniverseAsync(UniverseQid));
        Assert.Equal("House Atreides", created.Label);
        Assert.Equal(FictionalEntityType.Organization, created.EntitySubType);
        Assert.True(created.AddedByUser);
        Assert.Equal("Rulers of Caladan", created.Description);
        var history = await EditorUniverseHistory.Read(_timeline, created.Id, CancellationToken.None);
        Assert.Equal("Added by you", Assert.Single(history).Label);
        Assert.NotNull(undo);
    }

    [Fact]
    public async Task UndoingTheAdd_RemovesTheThingAgain()
    {
        var universe = await SeedUniverseAsync();
        var undo = await _handler.ApplyAsync(Add(universe, "House Atreides", "factions"));
        Assert.Single(await _entities.GetByUniverseAsync(UniverseQid));

        await undo!(CancellationToken.None);

        Assert.Empty(await _entities.GetByUniverseAsync(UniverseQid));
    }

    [Theory]
    [InlineData(null, "factions", "A name is required.")]
    [InlineData("   ", "factions", "A name is required.")]
    [InlineData("House Atreides", null, "Pick what kind of thing this is.")]
    [InlineData("House Atreides", "vehicles", "Pick what kind of thing this is.")]
    public async Task ABadAdd_IsRefusedBeforeAnythingIsWritten(string? name, string? kind, string reason)
    {
        var universe = await SeedUniverseAsync();

        Assert.Equal(reason, await _handler.CheckAsync(Add(universe, name, kind)));
        Assert.Empty(await _entities.GetByUniverseAsync(UniverseQid));
    }

    [Fact]
    public async Task ATooLongNameOrDescription_IsRefused()
    {
        var universe = await SeedUniverseAsync();

        Assert.NotNull(await _handler.CheckAsync(Add(universe, new string('x', 201), "factions")));
        Assert.NotNull(await _handler.CheckAsync(Add(universe, "House", "factions", new string('y', 2001))));
    }

    [Fact]
    public async Task TheSameNameInTheSameKind_IsRefused_ButAnotherKindMayShareIt()
    {
        var universe = await SeedUniverseAsync();
        await SeedProviderThingAsync("Arrakis", FictionalEntityType.Location);

        var duplicate = await _handler.CheckAsync(Add(universe, "arrakis", "places"));
        var otherKind = await _handler.CheckAsync(Add(universe, "Arrakis", "characters"));

        Assert.Contains("already has a place called arrakis", duplicate, StringComparison.Ordinal);
        Assert.Null(otherKind);
    }

    [Fact]
    public async Task AnUnknownUniverse_OrOneWithoutAWikidataId_IsGone()
    {
        var withoutQid = await SeedUniverseAsync(qid: null);

        Assert.Equal(EditSessionReasons.Gone, await _handler.CheckAsync(Add(Guid.NewGuid(), "House", "factions")));
        Assert.Equal(EditSessionReasons.Gone, await _handler.CheckAsync(Add(withoutQid, "House", "factions")));
    }

    [Fact]
    public async Task TheRail_ListsEveryKindWithItsCount_AndHidesConceptsUntilThereIsOne()
    {
        var universe = await SeedUniverseAsync();
        await SeedProviderThingAsync("Paul", FictionalEntityType.Character);
        await SeedProviderThingAsync("Leto", FictionalEntityType.Character);
        await SeedProviderThingAsync("Arrakis", FictionalEntityType.Location);

        var page = await _things.GetAsync(universe, UniverseQid, "Dune", null, null, 0, 40);

        Assert.Equal(universe, page.UniverseId);
        Assert.Equal("Dune", page.UniverseName);
        Assert.Equal(["characters", "factions", "places", "events", "objects", "species", "concepts"], page.Kinds.Select(kind => kind.Key));
        Assert.Equal(2, page.Kinds.Single(kind => kind.Key == "characters").Count);
        Assert.Equal("Place", page.Kinds.Single(kind => kind.Key == "places").Singular);
        Assert.False(page.Kinds.Single(kind => kind.Key == "concepts").Available);
        Assert.Empty(page.Things);
    }

    [Fact]
    public async Task AKind_IsSearchedAndPaged_InNameOrder()
    {
        var universe = await SeedUniverseAsync();
        foreach (var name in new[] { "Paul", "Leto", "Jessica", "Duncan", "Gurney" })
        {
            await SeedProviderThingAsync(name, FictionalEntityType.Character);
        }

        var first = await _things.GetAsync(universe, UniverseQid, "Dune", "characters", null, 0, 2);
        var second = await _things.GetAsync(universe, UniverseQid, "Dune", "characters", null, 2, 2);
        var searched = await _things.GetAsync(universe, UniverseQid, "Dune", "characters", "ess", 0, 40);

        Assert.Equal(["Duncan", "Gurney"], first.Things.Select(thing => thing.Label));
        Assert.True(first.HasMore);
        Assert.Equal(5, first.Total);
        Assert.Equal(["Jessica", "Leto"], second.Things.Select(thing => thing.Label));
        Assert.Equal(["Jessica"], searched.Things.Select(thing => thing.Label));
        Assert.False(searched.HasMore);
    }

    [Fact]
    public async Task AHandAddedThing_IsListedWithItsMark_AndAConceptShowsOnceThereIsOne()
    {
        var universe = await SeedUniverseAsync();
        await _handler.ApplyAsync(Add(universe, "The Spice", "concepts"));

        var page = await _things.GetAsync(universe, UniverseQid, "Dune", "concepts", null, 0, 40);

        Assert.True(page.Kinds.Single(kind => kind.Key == "concepts").Available);
        var thing = Assert.Single(page.Things);
        Assert.Equal("The Spice", thing.Label);
        Assert.True(thing.AddedByUser);
    }

    [Fact]
    public async Task History_KeepsOnlyEditorEdits_NewestFirst_AsYou()
    {
        var thing = Guid.NewGuid();
        await EditorUniverseHistory.Record(_timeline, thing, EditorSubjectKinds.FictionalEntity, EditorUniverseHistory.FieldEvent, "Name changed.", CancellationToken.None);
        await EditorUniverseHistory.Record(_timeline, thing, EditorSubjectKinds.FictionalEntity, EditorUniverseHistory.ArtworkEvent, "Main picture changed.", CancellationToken.None);

        var rows = await EditorUniverseHistory.Read(_timeline, thing, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("You", row.ActorLabel));
        Assert.Contains(rows, row => row.Label == "Picture changed" && row.Category == "artwork");
        Assert.Contains(rows, row => row.Label == "Details changed" && row.Category == "manual");
        Assert.True(EditorUniverseHistory.Keeps(EditorSubjectKinds.Universe));
        Assert.True(EditorUniverseHistory.Keeps(EditorSubjectKinds.FictionalEntity));
        Assert.False(EditorUniverseHistory.Keeps(EditorSubjectKinds.Person));
    }
}
