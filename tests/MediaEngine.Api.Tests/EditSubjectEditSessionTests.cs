using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// People, Series, curated collections and Universes saved through the edit session (universal editor spec D17, D17a): each kind is
/// checked, written through its own existing write path, undone when a later change fails, and a Universe's or Series' canonical
/// value is never touched and comes back when the local one is removed.
/// </summary>
public sealed class EditSubjectEditSessionTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_edit_subjects_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly PersonRepository _persons;
    private readonly CollectionRepository _collections;
    private readonly CollectionDisplayOverrideRepository _overrides;
    private readonly FictionalEntityRepository _entities;
    private readonly ArtworkAssetService _artwork;
    private readonly EditSubjectDirectory _subjects;
    private readonly DetailsEditSessionChangeHandler _details;
    private readonly ArtworkEditSessionChangeHandler _artworkHandler;

    public EditSubjectEditSessionTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _db = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _persons = new PersonRepository(_db);
        _collections = new CollectionRepository(_db);
        _overrides = new CollectionDisplayOverrideRepository(_db);
        _entities = new FictionalEntityRepository(_db);
        _artwork = new ArtworkAssetService(_db, new AssetPathService(_root), new NoHttpClientFactory());
        _subjects = new EditSubjectDirectory(
        [
            new PersonEditSubject(_persons, new PersonEditorReadService(_db, _persons)),
            new SeriesEditSubject(_collections, _overrides),
            new UniverseEditSubject(_collections, _overrides),
            new CuratedCollectionEditSubject(_collections),
            new FictionalEntityEditSubject(_entities),
        ]);
        var items = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
        var activity = new SystemActivityRepository(_db);
        _details = new DetailsEditSessionChangeHandler(items, activity, _subjects);
        _artworkHandler = new ArtworkEditSessionChangeHandler(_artwork, items, activity, _subjects);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp folder goes next */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    // -- Person ------------------------------------------------------------------

    [Fact]
    public async Task Person_Opens_WithItsFieldsFactsAndPhotoSlot()
    {
        var person = await SeedPersonAsync();

        var subject = await _subjects.LoadAsync(person);

        Assert.NotNull(subject);
        Assert.Equal(EditorSubjectKinds.Person, subject.Kind);
        Assert.Equal(["name", "biography", "sort_name", "custom_tags"], subject.Fields.Select(field => field.Key));
        Assert.Equal("Herbert, Frank", subject.Fields.Single(field => field.Key == "sort_name").Value);
        Assert.Equal(["Headshot"], subject.ArtworkSlots);
        Assert.Contains(subject.Facts, fact => fact.Label == "Occupation" && fact.Value == "Writer");
    }

    [Theory]
    [InlineData("occupation", "Chef")]
    [InlineData("custom_tags", "one;two;three;four;five;six;seven;eight;nine;ten;eleven;twelve;thirteen;fourteen;fifteen;sixteen;seventeen;eighteen;nineteen;twenty;21;22;23;24;25;26;27;28;29;30;31")]
    public async Task Person_Check_RefusesAFieldItCannotTakeAndWritesNothing(string key, string value)
    {
        var person = await SeedPersonAsync();

        var result = await CreateService().RunAsync(Request(previewOnly: false, DetailsChange("d1", person, (key, value))), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(EditSessionChangeStatuses.Invalid, Assert.Single(result.Changes).Status);
        Assert.Equal(0, await StoredOverrideCountAsync("persons", person));
    }

    [Fact]
    public async Task Person_Apply_SavesLocalOverridesAndTagsAndLeavesTheProviderNameAlone()
    {
        var person = await SeedPersonAsync();

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d1", person, ("name", "F. Herbert"), ("biography", "Wrote Dune."), ("custom_tags", "sci-fi; classics"))),
            NoRefusals);

        Assert.True(result.DataSaved);
        var subject = (await _subjects.LoadAsync(person))!;
        var name = subject.Fields.Single(field => field.Key == "name");
        Assert.Equal("F. Herbert", name.Value);
        Assert.True(name.IsOverridden);
        Assert.Equal("Frank Herbert", name.CanonicalValue);
        Assert.Equal("sci-fi; classics", subject.Fields.Single(field => field.Key == "custom_tags").Value);
        Assert.Equal("Frank Herbert", (await _persons.FindByIdAsync(person))!.Name);
    }

    [Fact]
    public async Task Person_Apply_WithAnEmptyValue_BringsTheProviderValueBack()
    {
        var person = await SeedPersonAsync();
        await CreateService().RunAsync(Request(previewOnly: false, DetailsChange("d1", person, ("name", "F. Herbert"))), NoRefusals);

        var result = await CreateService().RunAsync(Request(previewOnly: false, DetailsChange("d2", person, ("name", string.Empty))), NoRefusals);

        Assert.True(result.DataSaved);
        var name = (await _subjects.LoadAsync(person))!.Fields.Single(field => field.Key == "name");
        Assert.Equal("Frank Herbert", name.Value);
        Assert.False(name.IsOverridden);
    }

    [Fact]
    public async Task Person_Undo_PutsThePreviousOverridesBack()
    {
        var person = await SeedPersonAsync();
        await CreateService().RunAsync(Request(previewOnly: false, DetailsChange("d1", person, ("name", "F. Herbert"))), NoRefusals);

        var undo = await _details.ApplyAsync(DetailsChange("d2", person, ("name", "Someone Else"), ("custom_tags", "x")));
        Assert.NotNull(undo);
        await undo!(CancellationToken.None);

        var subject = (await _subjects.LoadAsync(person))!;
        Assert.Equal("F. Herbert", subject.Fields.Single(field => field.Key == "name").Value);
        Assert.Equal(string.Empty, subject.Fields.Single(field => field.Key == "custom_tags").Value);
    }

    [Fact]
    public async Task Person_Check_WhenThePersonIsGone_SaysSo()
    {
        var result = await CreateService().RunAsync(
            Request(previewOnly: true, DetailsChange("d1", Guid.NewGuid(), ("title", "Nobody"))), NoRefusals);

        Assert.Equal(EditSessionReasons.Gone, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Person_Artwork_ChoosesTheSavedPhotoForTheHeadshotSlot()
    {
        var person = await SeedPersonAsync();
        var photo = SeedAsset("photo");
        // A saved photo of this person (stored for them, not chosen yet), which is the only kind of picture a save may choose.
        await _artwork.LinkAsync("Person", person, new(photo, "Portrait", Preferred: false, SourceAssetType: "Headshot"), CancellationToken.None);

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, new EditSessionChange
            {
                Id = "a1",
                Kind = EditSessionChangeKinds.Artwork,
                EntityId = person,
                Slot = "Headshot",
                TargetId = photo.ToString(),
            }),
            NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(photo, (await _artwork.GetChosenAsync("Person", person, "Portrait", CancellationToken.None))?.ArtworkAssetId);
    }

    // -- Series ------------------------------------------------------------------

    [Fact]
    public async Task Series_Apply_StoresALocalOverrideAndLeavesTheCanonicalNameAlone()
    {
        var series = SeedGrouping(CollectionType.ContentGroup, "The Expanse", "Canonical description.");

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d1", series, ("title", "Expanse"), ("description", "Mine."))), NoRefusals);

        Assert.True(result.DataSaved);
        var subject = (await _subjects.LoadAsync(series))!;
        Assert.Equal(EditorSubjectKinds.Series, subject.Kind);
        Assert.Equal(["CoverArt", "Background"], subject.ArtworkSlots);
        var title = subject.Fields.Single(field => field.Key == "title");
        Assert.Equal("Expanse", title.Value);
        Assert.Equal("The Expanse", title.CanonicalValue);
        Assert.True(title.IsOverridden);
        Assert.Equal("The Expanse", (await _collections.GetByIdAsync(series))!.DisplayName);
    }

    [Fact]
    public async Task Series_Check_RefusesAnUnknownField()
    {
        var series = SeedGrouping(CollectionType.Series, "The Expanse", null);

        var result = await CreateService().RunAsync(Request(previewOnly: true, DetailsChange("d1", series, ("genre", "Sci-fi"))), NoRefusals);

        Assert.False(result.Valid);
    }

    // -- Universe (D17a) ---------------------------------------------------------

    [Fact]
    public async Task Universe_Override_IsLocalAndRestorableToTheCanonicalValue()
    {
        var universe = SeedGrouping(CollectionType.Universe, "Dune universe", "Canonical lore.");

        await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d1", universe, ("title", "Arrakis"), ("description", "My words."))), NoRefusals);

        var local = (await _subjects.LoadAsync(universe))!;
        Assert.Equal(EditorSubjectKinds.Universe, local.Kind);
        Assert.Equal("Arrakis", local.Fields.Single(field => field.Key == "title").Value);
        Assert.True(local.Fields.Single(field => field.Key == "description").IsOverridden);
        var stored = await _collections.GetByIdAsync(universe);
        Assert.Equal("Dune universe", stored!.DisplayName);
        Assert.Equal("Canonical lore.", stored.Description);

        // "Reset to canonical value": an empty value takes the local one away and the canonical one shows again.
        var reset = await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d2", universe, ("title", string.Empty), ("description", string.Empty))), NoRefusals);

        Assert.True(reset.DataSaved);
        var restored = (await _subjects.LoadAsync(universe))!;
        Assert.Equal("Dune universe", restored.Fields.Single(field => field.Key == "title").Value);
        Assert.Equal("Canonical lore.", restored.Fields.Single(field => field.Key == "description").Value);
        Assert.All(restored.Fields, field => Assert.False(field.IsOverridden));
        Assert.Empty(await _overrides.LoadAsync(universe) ?? new Dictionary<string, string>());
    }

    [Fact]
    public async Task Universe_Undo_PutsTheLocalValuesBackExactly()
    {
        var universe = SeedGrouping(CollectionType.Universe, "Dune universe", null);
        await CreateService().RunAsync(Request(previewOnly: false, DetailsChange("d1", universe, ("title", "Arrakis"))), NoRefusals);

        var undo = await _details.ApplyAsync(DetailsChange("d2", universe, ("title", "Other"), ("description", "Extra")));
        Assert.NotNull(undo);
        await undo!(CancellationToken.None);

        var stored = await _overrides.LoadAsync(universe);
        Assert.Equal("Arrakis", stored!["title"]);
        Assert.False(stored.ContainsKey("description"));
    }

    [Fact]
    public async Task Universe_Page_ShowsTheLocalNameAndDescription_ThenTheCanonicalOnesAgain()
    {
        var universe = SeedGrouping(CollectionType.Universe, "Dune universe", "Canonical lore.", qid: "Q555");
        var shelf = SeedGrouping(CollectionType.ContentGroup, "Dune", null);
        await _collections.SetParentCollectionAsync(shelf, universe);
        var reader = new UniverseClientReadService(
            _collections,
            new OneRowDisplay(shelf),
            new MediaEngine.Api.Services.Display.DisplayCardBuilder(),
            new UniverseConnectionsReadService(
                new FictionalEntityRepository(_db),
                new EntityRelationshipRepository(_db),
                new NarrativeRootRepository(_db),
                _persons,
                new PluginLoreRepository(_db),
                SpoilerTestKit.Create(new OneRowDisplay(shelf), _db)),
            displayOverrides: _overrides);

        await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d1", universe, ("title", "Arrakis"), ("description", "My words."))), NoRefusals);
        var local = await reader.LoadAsync("Q555", profile: null);
        await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d2", universe, ("title", string.Empty), ("description", string.Empty))), NoRefusals);
        var restored = await reader.LoadAsync("Q555", profile: null);

        Assert.Equal(("Arrakis", "My words."), (local!.Name, local.Description));
        Assert.Equal(("Dune universe", "Canonical lore."), (restored!.Name, restored.Description));
    }

    [Fact]
    public async Task Universe_Artwork_ChoosesACoverAndABackground()
    {
        var universe = SeedGrouping(CollectionType.Universe, "Dune universe", null);
        var cover = SeedAsset("cover");
        var background = SeedAsset("background");
        await _artwork.LinkAsync("Collection", universe, new(cover, "Primary", Preferred: false, SourceAssetType: "CoverArt"), CancellationToken.None);
        await _artwork.LinkAsync("Collection", universe, new(background, "Background", Preferred: false, SourceAssetType: "Background"), CancellationToken.None);

        var result = await CreateService().RunAsync(
            Request(
                previewOnly: false,
                ArtworkChange("a1", universe, "CoverArt", cover),
                ArtworkChange("a2", universe, "Background", background)),
            NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(cover, (await _artwork.GetChosenAsync("Collection", universe, "Primary", CancellationToken.None))?.ArtworkAssetId);
        Assert.Equal(background, (await _artwork.GetChosenAsync("Collection", universe, "Background", CancellationToken.None))?.ArtworkAssetId);
        // The Universe page reads the chosen pictures from the Universe's own record, so they show once saved.
        var stored = (await _collections.GetByIdAsync(universe))!;
        Assert.False(string.IsNullOrWhiteSpace(stored.CoverArtworkPath));
        Assert.False(string.IsNullOrWhiteSpace(stored.BackgroundArtworkPath));
    }

    // -- Character, place or organisation (Chronicle explorer) ---------------------

    [Fact]
    public async Task FictionalEntity_Opens_WithNameDescriptionFactsAndPortraitSlots()
    {
        var entity = await SeedEntityAsync("Paul Atreides", "Heir to House Atreides.");

        var subject = await _subjects.LoadAsync(entity);

        Assert.NotNull(subject);
        Assert.Equal(EditorSubjectKinds.FictionalEntity, subject.Kind);
        Assert.Equal(["name", "description"], subject.Fields.Select(field => field.Key));
        Assert.Equal(["CharacterPortrait", "Background"], subject.ArtworkSlots);
        Assert.Contains(subject.Facts, fact => fact.Label == "Kind" && fact.Value == "Character");
    }

    [Fact]
    public async Task FictionalEntity_Apply_SavesALocalNameAndDescription()
    {
        var entity = await SeedEntityAsync("Paul Atreides", "Heir to House Atreides.");

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d1", entity, ("name", "Muad'Dib"), ("description", string.Empty))), NoRefusals);

        Assert.True(result.DataSaved);
        var stored = (await _entities.FindByIdAsync(entity))!;
        Assert.Equal("Muad'Dib", stored.Label);
        Assert.True(string.IsNullOrEmpty(stored.Description));
    }

    [Fact]
    public async Task FictionalEntity_SavingOnlyTheName_LeavesTheDescriptionFollowingTheProvider()
    {
        var entity = await SeedEntityAsync("Paul Atreides", "Heir to House Atreides.");

        await _details.ApplyAsync(DetailsChange("d1", entity, ("name", "Muad'Dib")));

        var stored = await _entities.GetUserDetailsAsync(entity);
        Assert.Equal("Muad'Dib", stored!.Value.Label);
        Assert.Null(stored.Value.Description);
        Assert.Equal("Heir to House Atreides.", (await _entities.FindByIdAsync(entity))!.Description);
    }

    [Fact]
    public async Task FictionalEntity_SavingOnlyTheDescription_LeavesTheNameFollowingTheProvider_AndUndoRemovesIt()
    {
        var entity = await SeedEntityAsync("Paul Atreides", "Heir.");

        var undo = await _details.ApplyAsync(DetailsChange("d1", entity, ("description", "The Kwisatz Haderach.")));

        var stored = await _entities.GetUserDetailsAsync(entity);
        Assert.Null(stored!.Value.Label);
        Assert.Equal("The Kwisatz Haderach.", stored.Value.Description);

        await undo!(CancellationToken.None);

        var restored = await _entities.GetUserDetailsAsync(entity);
        Assert.Null(restored!.Value.Label);
        Assert.Null(restored.Value.Description);
        Assert.Equal("Heir.", (await _entities.FindByIdAsync(entity))!.Description);
    }

    [Fact]
    public async Task FictionalEntity_Check_RefusesAnEmptyNameAndAFieldItDoesNotHave()
    {
        var entity = await SeedEntityAsync("Paul Atreides", null);

        var empty = await CreateService().RunAsync(Request(previewOnly: true, DetailsChange("d1", entity, ("name", " "))), NoRefusals);
        var unknown = await CreateService().RunAsync(Request(previewOnly: true, DetailsChange("d2", entity, ("species", "Human"))), NoRefusals);

        Assert.False(empty.Valid);
        Assert.False(unknown.Valid);
        Assert.Equal("Paul Atreides", (await _entities.FindByIdAsync(entity))!.Label);
    }

    [Fact]
    public async Task FictionalEntity_Undo_PutsTheNameBack()
    {
        var entity = await SeedEntityAsync("Paul Atreides", "Heir.");

        var undo = await _details.ApplyAsync(DetailsChange("d1", entity, ("name", "Someone Else")));
        Assert.Equal("Someone Else", (await _entities.FindByIdAsync(entity))!.Label);
        await undo!(CancellationToken.None);

        Assert.Equal("Paul Atreides", (await _entities.FindByIdAsync(entity))!.Label);
    }

    [Fact]
    public async Task FictionalEntity_Artwork_ChoosesAPortrait()
    {
        var entity = await SeedEntityAsync("Paul Atreides", null);
        var portrait = SeedAsset("portrait");
        await _artwork.LinkAsync("FictionalEntity", entity, new(portrait, "Portrait", Preferred: false, SourceAssetType: "CharacterPortrait"), CancellationToken.None);

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, ArtworkChange("a1", entity, "CharacterPortrait", portrait)), NoRefusals);

        Assert.True(result.DataSaved);
        Assert.Equal(portrait, (await _artwork.GetChosenAsync("FictionalEntity", entity, "Portrait", CancellationToken.None))?.ArtworkAssetId);
    }

    // -- Curated collection ------------------------------------------------------

    [Fact]
    public async Task Collection_Apply_RenamesTheCollectionAndChangesItsDescription()
    {
        var collection = await SeedCollectionAsync(CollectionResolution.Query, "Road trip", "Songs.");

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d1", collection, ("title", "Long drive"), ("description", string.Empty))), NoRefusals);

        Assert.True(result.DataSaved);
        var stored = (await _collections.GetByIdAsync(collection))!;
        Assert.Equal("Long drive", stored.DisplayName);
        Assert.True(string.IsNullOrEmpty(stored.Description));
        Assert.Equal(EditorSubjectKinds.Collection, (await _subjects.LoadAsync(collection))!.Kind);
    }

    [Fact]
    public async Task Collection_Check_RefusesAnEmptyName()
    {
        var collection = await SeedCollectionAsync(CollectionResolution.Query, "Road trip", null);

        var result = await CreateService().RunAsync(Request(previewOnly: true, DetailsChange("d1", collection, ("title", " "))), NoRefusals);

        Assert.False(result.Valid);
        Assert.Equal(CuratedCollectionEditSubject.NameRequired, Assert.Single(result.Changes).Reason);
    }

    [Fact]
    public async Task Collection_Apply_NeverLeavesACuratedCollectionWithoutItsItems()
    {
        var curated = await SeedCollectionAsync(CollectionResolution.Materialized, "Empty shelf", null);

        var result = await CreateService().RunAsync(
            Request(previewOnly: false, DetailsChange("d1", curated, ("title", "Renamed"))), NoRefusals);

        Assert.False(result.DataSaved);
        Assert.Equal(EditSessionChangeStatuses.Failed, Assert.Single(result.Changes).Status);
        Assert.Equal("Empty shelf", (await _collections.GetByIdAsync(curated))!.DisplayName);
    }

    [Fact]
    public async Task Collection_Undo_PutsTheNameAndDescriptionBack()
    {
        var collection = await SeedCollectionAsync(CollectionResolution.Query, "Road trip", "Songs.");

        var undo = await _details.ApplyAsync(DetailsChange("d1", collection, ("title", "Long drive"), ("description", "Other")));
        Assert.NotNull(undo);
        await undo!(CancellationToken.None);

        var stored = (await _collections.GetByIdAsync(collection))!;
        Assert.Equal("Road trip", stored.DisplayName);
        Assert.Equal("Songs.", stored.Description);
    }

    // -- Helpers ---------------------------------------------------------------

    private EditSessionService CreateService() =>
        new([_details, _artworkHandler], NullLogger<EditSessionService>.Instance);

    private static EditSessionRequest Request(bool previewOnly, params EditSessionChange[] changes) =>
        new() { PreviewOnly = previewOnly, Changes = [.. changes] };

    private static EditSessionChange DetailsChange(string id, Guid entity, params (string Key, string Value)[] fields) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Details,
        EntityId = entity,
        Fields = fields.Select(field => new EditSessionFieldChange { Key = field.Key, Value = field.Value }).ToList(),
    };

    private static EditSessionChange ArtworkChange(string id, Guid entity, string slot, Guid asset) => new()
    {
        Id = id,
        Kind = EditSessionChangeKinds.Artwork,
        EntityId = entity,
        Slot = slot,
        TargetId = asset.ToString(),
    };

    private async Task<Guid> SeedEntityAsync(string label, string? description)
    {
        var entity = new FictionalEntity
        {
            Id = Guid.NewGuid(),
            WikidataQid = "Q" + Random.Shared.Next(1000, 999999),
            Label = label,
            Description = description,
            EntitySubType = "Character",
            FictionalUniverseQid = "Q3041974",
            FictionalUniverseLabel = "Dune universe",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _entities.CreateAsync(entity);
        return entity.Id;
    }

    private async Task<Guid> SeedPersonAsync()
    {
        var created = await _persons.CreateAsync(new Person
        {
            Id = Guid.NewGuid(),
            Name = "Frank Herbert",
            Biography = "Author of Dune.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        using var connection = _db.CreateConnection();
        connection.Execute("UPDATE persons SET occupation = 'Writer' WHERE id = @id;", new { id = created.Id });
        return created.Id;
    }

    private Guid SeedGrouping(CollectionType type, string name, string? description, string? qid = null)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Description = description,
            WikidataQid = qid,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            type, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        _collections.UpsertAsync(collection).GetAwaiter().GetResult();
        return collection.Id;
    }

    private async Task<Guid> SeedCollectionAsync(CollectionResolution resolution, string name, string? description)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Description = description,
            MembershipMode = resolution == CollectionResolution.Materialized ? ContainerMembershipMode.Manual : ContainerMembershipMode.Smart,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.Custom, CollectionScope.Library, resolution,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        await _collections.UpsertAsync(collection);
        return collection.Id;
    }

    private Guid SeedAsset(string name)
    {
        var assetId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO artwork_assets (id, content_hash, original_path, width_px, height_px, aspect_class, source_provider, created_at)
            VALUES (@assetId, @hash, @path, 1000, 1500, 'Portrait', 'test', @createdAt);
            """,
            new
            {
                assetId,
                hash = $"hash-{name}-{assetId:N}",
                path = Path.Combine(_root, $"{assetId:N}.jpg"),
                createdAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        return assetId;
    }

    private async Task<int> StoredOverrideCountAsync(string table, Guid id)
    {
        using var connection = _db.CreateConnection();
        var json = await connection.ExecuteScalarAsync<string?>(
            $"SELECT display_overrides_json FROM {table} WHERE id = @id;", new { id });
        return string.IsNullOrWhiteSpace(json) ? 0 : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!.Count;
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class OneRowDisplay(Guid shelf) : Api.Services.Display.IDisplayProjectionReadService
    {
        private readonly IReadOnlyList<Api.Services.Display.DisplayWorkRow> _rows =
        [
            new()
            {
                WorkId = Guid.NewGuid(),
                RootWorkId = Guid.NewGuid(),
                AssetId = Guid.NewGuid(),
                EditionId = Guid.NewGuid(),
                CollectionId = shelf,
                MediaType = "Books",
                Title = "Dune",
                SeriesPosition = "1",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        ];

        public Task<IReadOnlyList<Api.Services.Display.DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(_rows);
        public Task<IReadOnlyList<Api.Services.Display.DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<Api.Services.Display.DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<Api.Services.Display.DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<Api.Services.Display.DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<Api.Services.Display.DisplayHomeCollectionRow>>([]);
    }
}
