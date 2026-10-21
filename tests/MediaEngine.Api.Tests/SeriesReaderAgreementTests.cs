using System.Globalization;
using System.Reflection;
using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Series shelf (native items), the item page and the Series page all order and number one Series the same way:
/// the numbered run first (decimals intact), then the unnumbered tail, with supplementary, collected-content and
/// broader-context members listed apart as "Also in this world" and never numbered with the run.
/// </summary>
public sealed class SeriesReaderAgreementTests : IDisposable
{
    private const string Expanse = "QExpanse";

    // Retail position text per title; the manifest lists the three collected-content novellas.
    private static readonly (string Title, string Position, bool CollectedContent)[] Expanse9 =
    [
        ("Leviathan Wakes", "1", false),
        ("Caliban's War", "2", false),
        ("Abaddon's Gate", "3", false),
        ("Cibola Burn", "4", false),
        ("Nemesis Games", "5", false),
        ("Babylon's Ashes", "6", false),
        ("Persepolis Rising", "7", false),
        ("Tiamat's Wrath", "8", false),
        ("Leviathan Falls", "9", false),
        ("The Butcher of Anderson Station", "0.5", false),
        ("Strange Dog", "6.5", false),
        ("The Churn", "0.2", true),
        ("Gods of Risk", "2.5", true),
        ("The Vital Abyss", "5.5", true),
    ];

    private static readonly string[] ExpectedRun =
    [
        "The Butcher of Anderson Station:0.5", "Leviathan Wakes:1", "Caliban's War:2", "Abaddon's Gate:3", "Cibola Burn:4",
        "Nemesis Games:5", "Babylon's Ashes:6", "Strange Dog:6.5", "Persepolis Rising:7", "Tiamat's Wrath:8", "Leviathan Falls:9",
    ];

    private static readonly string[] ExpectedAlso =
    [
        "The Churn:0.2", "Gods of Risk:2.5", "The Vital Abyss:5.5",
    ];

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly Dictionary<string, Guid> _workIds = new();

    public SeriesReaderAgreementTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_series_agreement_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Fact]
    public async Task TheShelf_ListsTheNumberedRunThenAlsoInThisWorld_WithDecimalsIntact()
    {
        var (collectionId, workIds) = await SeedShelfAsync();

        var items = await ReadNativeItemsAsync(collectionId, workIds);

        Assert.Equal(ExpectedRun, Run(items.Select(Describe)));
        Assert.Equal(ExpectedAlso, Also(items.Select(Describe)));
        Assert.Equal(
            Enumerable.Range(0, items.Count),
            items.Select(item => item.SortOrder));
        Assert.All(items.Where(i => ExpectedAlso.Any(e => e.StartsWith(i.Title, StringComparison.Ordinal))),
            item => Assert.Equal(SeriesMembershipScopeNames.CollectedContent, item.MembershipScope));
    }

    [Fact]
    public async Task TheShelf_TheItemPage_AndTheSeriesPage_AgreeOnOrderAndNumbers()
    {
        var (collectionId, workIds) = await SeedShelfAsync();

        var shelf = await ReadNativeItemsAsync(collectionId, workIds);
        var itemPage = ItemPageOrder();
        var seriesPage = SeriesPageOrder();

        // Same Series, same order, same numbers: the Series run and "Also in this world" match one for one.
        Assert.Equal(ExpectedRun, Run(shelf.Select(Describe)));
        Assert.Equal(ExpectedRun, itemPage.Run);
        Assert.Equal(ExpectedRun, seriesPage.Run);
        Assert.Equal(ExpectedAlso, Also(shelf.Select(Describe)));
        Assert.Equal(ExpectedAlso, itemPage.Also);
        Assert.Equal(ExpectedAlso, seriesPage.Also);
    }

    [Fact]
    public void TheItemPage_NeverNumbersAMemberOfAlsoInThisWorld_WhenItIsTheCurrentWork()
    {
        var (run, also) = SplitItemPage(currentTitle: "Gods of Risk");

        Assert.DoesNotContain(run, item => item.IsCurrent);
        Assert.Contains(also, item => item.IsCurrent && item.Title == "Gods of Risk");
        Assert.All(also, item => Assert.NotEqual(SeriesMembershipScopeNames.MainSequence, item.MembershipScope));
    }

    [Fact]
    public void WhenEveryMemberIsInAlsoInThisWorldAndNoneIsCurrent_NavigationStillResolves_AndAnEmptyPageResolvesToNothing()
    {
        var also = new List<SequenceItemViewModel>
        {
            new() { Id = "a", Title = "A", MembershipScope = SeriesMembershipScopeNames.Supplementary },
            new() { Id = "b", Title = "B", MembershipScope = SeriesMembershipScopeNames.CollectedContent },
        };

        var resolved = Assert.IsType<(List<SequenceItemViewModel>, int, bool)>(
            InvokeNullable("ResolveSequenceNavigation", new List<SequenceItemViewModel>(), also));
        Assert.Equal(0, resolved.Item2);
        Assert.True(resolved.Item3);
        Assert.Equal("A", resolved.Item1[0].Title);

        var current = Assert.IsType<(List<SequenceItemViewModel>, int, bool)>(
            InvokeNullable("ResolveSequenceNavigation", new List<SequenceItemViewModel>(), new List<SequenceItemViewModel>
            {
                new() { Id = "x", Title = "X", MembershipScope = SeriesMembershipScopeNames.Supplementary },
                new() { Id = "y", Title = "Y", MembershipScope = SeriesMembershipScopeNames.Supplementary, IsCurrent = true },
            }));
        Assert.Equal(1, current.Item2);

        var nothing = InvokeNullable("ResolveSequenceNavigation", new List<SequenceItemViewModel>(), new List<SequenceItemViewModel>());
        Assert.Null(nothing);
    }

    [Fact]
    public void AnUnpositionedMemberWithARetailPosition_ShowsAfterTheNumberedRunWithoutANumber()
    {
        var items = new List<SequenceItemViewModel>
        {
            new() { Id = "u", Title = "Unpositioned", MembershipScope = SeriesMembershipScopeNames.Unpositioned, PositionLabel = "0.5", PositionSort = 0.5 },
            new() { Id = "n", Title = "Numbered", MembershipScope = SeriesMembershipScopeNames.MainSequence, PositionLabel = "9", PositionSort = 9, PositionNumber = 9 },
        };

        var (run, also) = InvokeStatic<(List<SequenceItemViewModel>, List<SequenceItemViewModel>)>("SplitSeriesRun", items);

        Assert.Empty(also);
        Assert.Equal(["Numbered", "Unpositioned"], run.Select(item => item.Title));
        Assert.Null(run[1].PositionLabel);
        Assert.Null(run[1].PositionSort);
    }

    [Theory]
    [InlineData(DetailEntityType.Book, "Books")]
    [InlineData(DetailEntityType.BookSeries, "Books")]
    [InlineData(DetailEntityType.Audiobook, "Audiobooks")]
    [InlineData(DetailEntityType.ComicIssue, "Comics")]
    [InlineData(DetailEntityType.MovieSeries, "Movies")]
    [InlineData(DetailEntityType.TvEpisode, "TV")]
    [InlineData(DetailEntityType.MusicAlbum, null)]
    public void TheItemPageReadsManifestRowsFromTheShelfOfTheMediaItShows(DetailEntityType entityType, string? expected)
    {
        var method = typeof(DetailComposerService).Assembly
            .GetType("MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator", throwOnError: true)!
            .GetMethod("SeriesMediaTypeName", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(expected, (string?)method.Invoke(null, [entityType]));
    }

    [Fact]
    public void TheNativeUniversePath_ReadsEveryMemberShelfsManifestRowsInOneCall()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MediaEngine.Api", "Services", "ReadServices", "UniverseClientReadService.cs"));
        // The placements are read by the shared Series split, which the shelf's see-all list uses too.
        var split = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MediaEngine.Api", "Services", "Display", "SeriesShelfRows.cs"));

        Assert.Contains("SeriesShelfSplit.LoadPlacementsAsync(manifests, members.Select(member => member.Id).ToList(), ct)", source);
        Assert.Contains("GetLinkedItemsByCollectionIdsAsync", split);
        Assert.DoesNotContain("GetViewByCollectionIdAsync", source);
        Assert.DoesNotContain("GetViewByCollectionIdAsync", split);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // A Work in two Series: each Series shows that Series' own number
    // ----------------------------------------------------------------------------------------------------------------

    private const string Trilogy = "Q111";
    private const string Cycle = "Q222";

    [Fact]
    public async Task AWorkInTwoSeries_ShowsEachSeriesItsOwnNumber_OnEachShelf_WithDecimalsIntact()
    {
        var seeded = await SeedTwoSeriesAsync();

        var trilogy = await ReadNativeItemsAsync(seeded.TrilogyShelf, [seeded.Opener, seeded.Both]);
        var cycle = await ReadNativeItemsAsync(seeded.CycleShelf, [seeded.Sibling, seeded.Both]);

        Assert.Equal(["Opener:1", "Both:3"], Run(trilogy.Select(Describe)));
        // 7.5 after 7 even though the Work's single stored number (3) would put it first.
        Assert.Equal(["Sibling:7", "Both:7.5"], Run(cycle.Select(Describe)));
    }

    [Fact]
    public async Task AWorkInTwoSeries_ShowsEachSeriesItsOwnNumber_OnTheSeriesPage()
    {
        var seeded = await SeedTwoSeriesAsync();
        var composer = new MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator(
            _db, null!, null!, null!, null!, null!, null!, null!);

        var trilogy = await LoadSeriesPageWorkAsync(composer, seeded.TrilogyShelf, [seeded.Opener, seeded.Both], "Both");
        var cycle = await LoadSeriesPageWorkAsync(composer, seeded.CycleShelf, [seeded.Sibling, seeded.Both], "Both");

        Assert.Equal(("3", 3d), trilogy);
        Assert.Equal(("7.5", 7.5d), cycle);
    }

    [Fact]
    public async Task AWorkInTwoSeries_ShowsTheSelectedSeriesNumber_OnTheItemPage()
    {
        var seeded = await SeedTwoSeriesAsync();
        var composer = new MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator(
            _db, null!, null!, null!, null!, new SeriesManifestRepository(_db), null!, null!);
        var detail = new LibraryItemDetail { EntityId = seeded.Both, Title = "Both", MediaType = "Books", Series = "Trilogy" };

        var onTrilogy = await BuildItemPagePlacementAsync(composer, seeded.Both, detail, Trilogy);
        var onCycle = await BuildItemPagePlacementAsync(composer, seeded.Both, detail, Cycle);

        Assert.Equal(("3", 3d), (onTrilogy.CurrentItem.PositionLabel, onTrilogy.CurrentItem.PositionSort));
        Assert.Equal(("7.5", 7.5d), (onCycle.CurrentItem.PositionLabel, onCycle.CurrentItem.PositionSort));
    }

    [Fact]
    public async Task AWorkInTwoSeries_IsOrderedByEachSeriesOwnNumber_InTheHelperAppUniverse()
    {
        var seeded = await SeedTwoSeriesAsync();
        var collections = new CollectionRepository(_db);
        var universe = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = "World",
            WikidataQid = "Q333",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        universe.RestoreDefinition(
            CollectionType.Universe, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        await collections.UpsertAsync(universe);
        await collections.SetParentCollectionAsync(seeded.TrilogyShelf, universe.Id);
        await collections.SetParentCollectionAsync(seeded.CycleShelf, universe.Id);

        var display = new DisplayRowsStub(
        [
            HelperRow(seeded.Opener, "Opener", seeded.TrilogyShelf, "1"),
            HelperRow(seeded.Both, "Both", seeded.TrilogyShelf, "3"),
            HelperRow(seeded.Sibling, "Sibling", seeded.CycleShelf, "7"),
            HelperRow(seeded.Both, "Both", seeded.CycleShelf, "3"),
        ]);
        var connections = new UniverseConnectionsReadService(
            new FictionalEntityRepository(_db),
            new EntityRelationshipRepository(_db),
            new NarrativeRootRepository(_db),
            new PersonRepository(_db),
            new PluginLoreRepository(_db),
            SpoilerTestKit.Create(display, _db));
        var universes = new UniverseClientReadService(
            collections, display, new DisplayCardBuilder(), connections,
            new SeriesManifestRepository(_db), arrays: new CanonicalValueArrayRepository(_db));

        var detail = await universes.LoadAsync("Q333", profile: null);

        Assert.NotNull(detail);
        var cycle = detail!.Shelves.Single(shelf => shelf.CollectionId == seeded.CycleShelf);
        // The Work's single stored number (3) would list it before Sibling (7); the Cycle's own number is 7.5.
        Assert.Equal([seeded.Sibling, seeded.Both], cycle.Shelf.Items.Select(card => card.WorkId!.Value));
    }

    private static DisplayWorkRow HelperRow(Guid workId, string title, Guid collectionId, string position) => new()
    {
        WorkId = workId,
        RootWorkId = workId,
        AssetId = Guid.NewGuid(),
        EditionId = Guid.NewGuid(),
        CollectionId = collectionId,
        MediaType = "Books",
        Title = title,
        SeriesPosition = position,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class DisplayRowsStub(IReadOnlyList<DisplayWorkRow> rows) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(rows);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    [Fact]
    public async Task AWorkWithNoPairForTheShelfsSeries_KeepsTodaysSingleNumber()
    {
        var seeded = await SeedTwoSeriesAsync();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM canonical_value_arrays WHERE entity_id = @id;", new { id = seeded.Both });
        }

        var cycle = await ReadNativeItemsAsync(seeded.CycleShelf, [seeded.Sibling, seeded.Both]);

        Assert.Equal(["Both:3", "Sibling:7"], Run(cycle.Select(Describe)));
    }

    private static async Task<(string? Label, double? Sort)> LoadSeriesPageWorkAsync(
        object composer, Guid collectionId, IReadOnlyList<Guid> workIds, string title)
    {
        var method = composer.GetType().GetMethod("LoadCollectionWorksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var load = (Task)method.Invoke(composer, [collectionId, null, CancellationToken.None, workIds, null, null])!;
        await load;
        var works = ((System.Collections.IEnumerable)load.GetType().GetProperty("Result")!.GetValue(load)!).Cast<object>();
        var work = works.Single(candidate => (string?)candidate.GetType().GetProperty("Title")!.GetValue(candidate) == title);
        return (
            (string?)work.GetType().GetProperty("RetailSeriesPosition")!.GetValue(work),
            (double?)work.GetType().GetProperty("OrdinalSort")!.GetValue(work));
    }

    private static async Task<SequencePlacementViewModel> BuildItemPagePlacementAsync(
        object composer, Guid workId, LibraryItemDetail detail, string requestedContainerId)
    {
        var method = composer.GetType().GetMethod("BuildSequencePlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(composer, [workId, detail, DetailEntityType.Book, requestedContainerId, null, detail.Title, CancellationToken.None])!;
        await task;
        return Assert.IsType<SequencePlacementViewModel>(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    private sealed record TwoSeries(Guid TrilogyShelf, Guid CycleShelf, Guid Opener, Guid Both, Guid Sibling);

    /// <summary>
    /// Two Series shelves. "Both" sits in each: number 3 in the Trilogy and 7.5 in the Cycle (its single stored number
    /// is the Trilogy's, 3). "Opener" is the Trilogy's 1 and "Sibling" the Cycle's 7.
    /// </summary>
    private async Task<TwoSeries> SeedTwoSeriesAsync()
    {
        var collections = new CollectionRepository(_db);
        var manifests = new SeriesManifestRepository(_db);
        var trilogy = await UpsertShelfAsync(collections, "Trilogy", Trilogy);
        var cycle = await UpsertShelfAsync(collections, "Cycle", Cycle);

        var opener = Guid.NewGuid();
        var both = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        InsertTwoSeriesWork(opener, "Opener", trilogy, 1d, (Trilogy, "1"));
        InsertTwoSeriesWork(both, "Both", trilogy, 3d, (Trilogy, "3"), (Cycle, "7.5"));
        InsertTwoSeriesWork(sibling, "Sibling", cycle, 7d, (Cycle, "7"));

        await UpsertManifestAsync(manifests, trilogy, Trilogy, "Trilogy", (opener, "Opener"), (both, "Both"));
        await UpsertManifestAsync(manifests, cycle, Cycle, "Cycle", (sibling, "Sibling"), (both, "Both"));
        return new TwoSeries(trilogy, cycle, opener, both, sibling);
    }

    private static async Task<Guid> UpsertShelfAsync(CollectionRepository collections, string name, string qid)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            WikidataQid = qid,
            MediaType = "Books",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        await collections.UpsertAsync(collection);
        return collection.Id;
    }

    private void InsertTwoSeriesWork(Guid id, string title, Guid collectionId, double sort, params (string Qid, string Position)[] pairs)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, collection_id, media_type, work_kind, ordinal, ordinal_sort)
            VALUES (@id, @collectionId, 'Books', 'standalone', @ordinal, @sort);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'title', @title, @now);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'series_position', @position, @now);
            INSERT INTO editions (id, work_id) VALUES (@edition, @id);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
            """,
            new
            {
                id,
                collectionId,
                ordinal = ToWhole(sort),
                sort,
                title,
                position = pairs[0].Position,
                now = DateTimeOffset.UtcNow.ToString("O"),
                edition = Guid.NewGuid(),
                asset = Guid.NewGuid(),
                hash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{Guid.NewGuid():N}.epub",
            });

        for (var index = 0; index < pairs.Length; index++)
        {
            conn.Execute(
                "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid) VALUES (@id, 'series_placement', @index, @position, @qid);",
                new { id, index, position = pairs[index].Position, qid = pairs[index].Qid });
        }
    }

    private static Task UpsertManifestAsync(
        SeriesManifestRepository manifests, Guid collectionId, string seriesQid, string label, params (Guid WorkId, string Title)[] members)
        => manifests.UpsertManifestAsync(
            new SeriesManifestHydration
            {
                SeriesQid = seriesQid,
                CollectionId = collectionId,
                SeriesLabel = label,
                WarningsJson = "[]",
                ApiMetadataJson = "{}",
                LastHydratedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            members.Select((member, index) => new SeriesManifestItemRecord
            {
                Id = Guid.NewGuid(),
                CollectionId = collectionId,
                SeriesQid = seriesQid,
                ItemQid = "Q-" + member.Title,
                ItemLabel = member.Title,
                MediaType = "Books",
                SortOrder = index + 1,
                OrderSource = "SeriesOrdinal",
                OwnershipState = "Owned",
                LinkedWorkId = member.WorkId,
                MembershipScope = SeriesMembershipScopeNames.MainSequence,
                SourcePropertiesJson = "[]",
                RelationshipsJson = "[]",
                LastHydratedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ToList());

    // ----------------------------------------------------------------------------------------------------------------
    // The three readers
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>The native <c>/api/v1/collections/{id}/items</c> read: real SQL against a real data store.</summary>
    private async Task<List<CollectionItemDto>> ReadNativeItemsAsync(Guid collectionId, IReadOnlyList<Guid> workIds)
    {
        var service = new CollectionCatalogReadService(
            collectionRepo: null!,
            manifestRepo: null!,
            personRepo: null!,
            artworkPaletteService: null!,
            mediaLookupReadService: null!,
            db: _db,
            logger: NullLogger<CollectionCatalogReadService>.Instance);
        var method = typeof(CollectionCatalogReadService).GetMethod(
            "ResolveCollectionWorkIdsToItemsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Request the works in a scrambled order: the Series ordering, not the request order, decides.
        var scrambled = workIds.OrderBy(id => id).ToList();
        var task = (Task<List<CollectionItemDto>>)method!.Invoke(service, [collectionId, scrambled, false, true, null, CancellationToken.None])!;
        return await task;
    }

    /// <summary>The item page: owned works merged with the Wikidata manifest, then split into the run and "Also in this world".</summary>
    private (List<string> Run, List<string> Also) ItemPageOrder()
    {
        var (run, also) = SplitItemPage(currentTitle: "Leviathan Wakes");
        return (
            run.Select(item => $"{item.Title}:{item.PositionLabel}").ToList(),
            also.Select(item => $"{item.Title}:{item.PositionLabel}").ToList());
    }

    private (List<SequenceItemViewModel> Run, List<SequenceItemViewModel> Also) SplitItemPage(string currentTitle)
    {
        var current = Guid.NewGuid();
        var owned = Expanse9.Select(book => new SequenceItemViewModel
        {
            Id = book.Title == currentTitle ? current.ToString("D") : StableId(book.Title).ToString("D"),
            EntityType = DetailEntityType.Book,
            Title = book.Title,
            PositionLabel = book.Position,
            PositionSort = double.Parse(book.Position, CultureInfo.InvariantCulture),
            PositionNumber = ToWhole(double.Parse(book.Position, CultureInfo.InvariantCulture)),
            IsCurrent = book.Title == currentTitle,
            IsOwned = true,
        }).ToList();

        var manifest = Expanse9.Select(book => new SeriesManifestItemRecord
        {
            Id = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            SeriesQid = Expanse,
            ItemQid = "Q-" + book.Title,
            ItemLabel = book.Title,
            RawOrdinal = book.CollectedContent || IsRetailOnly(book.Title) ? null : book.Position,
            ParsedOrdinal = book.CollectedContent || IsRetailOnly(book.Title) ? null : double.Parse(book.Position, CultureInfo.InvariantCulture),
            OrdinalScopeQid = book.CollectedContent || IsRetailOnly(book.Title) ? null : Expanse,
            SortOrder = 99,
            OrderSource = "SeriesOrdinal",
            OwnershipState = "Owned",
            LinkedWorkId = book.Title == currentTitle ? current : StableId(book.Title),
            MembershipScope = book.CollectedContent ? SeriesMembershipScopeNames.CollectedContent : SeriesMembershipScopeNames.MainSequence,
        }).Where(row => !IsRetailOnly(row.ItemLabel!)).ToList();

        var merged = InvokeStatic<List<SequenceItemViewModel>>(
            "MergeManifestItems", owned, manifest, null, current, DetailEntityType.Book);
        return InvokeStatic<(List<SequenceItemViewModel>, List<SequenceItemViewModel>)>("SplitSeriesRun", merged);
    }

    /// <summary>The Series page: owned works plus manifest placement through the Series-page builder.</summary>
    private (List<string> Run, List<string> Also) SeriesPageOrder()
    {
        var orchestrator = typeof(DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator", throwOnError: true)!;
        var summaryType = orchestrator.GetNestedType("CollectionWorkSummary", BindingFlags.NonPublic)!;

        var works = Expanse9.Select(book =>
        {
            var sort = double.Parse(book.Position, CultureInfo.InvariantCulture);
            var summary = Activator.CreateInstance(
                summaryType,
                StableId(book.Title).ToString("D"), "Books", ToWhole(sort), book.Title, null, null, null, null, null, null, null, null,
                false, null, null, true, "Owned", false, null, null, null)!;
            summaryType.GetProperty("OrdinalSort")!.SetValue(summary, sort);
            summaryType.GetProperty("RetailSeriesPosition")!.SetValue(summary, book.Position);
            return summary;
        }).ToList();
        var typedWorks = Cast(summaryType, works);

        var manifest = new MediaEngine.Domain.Models.SeriesManifestViewDto
        {
            CollectionId = Guid.NewGuid(),
            SeriesQid = Expanse,
            Items = Expanse9.Where(book => !IsRetailOnly(book.Title)).Select(book => new MediaEngine.Domain.Models.SeriesManifestItemDto
            {
                Id = Guid.NewGuid(),
                ItemQid = "Q-" + book.Title,
                SeriesQid = Expanse,
                ItemLabel = book.Title,
                RawOrdinal = book.CollectedContent ? null : book.Position,
                ParsedOrdinal = book.CollectedContent ? null : double.Parse(book.Position, CultureInfo.InvariantCulture),
                OrdinalScopeQid = book.CollectedContent ? null : Expanse,
                SortOrder = 99,
                OrderSource = "SeriesOrdinal",
                OwnershipState = "Owned",
                LinkedWorkId = StableId(book.Title),
                MembershipScope = book.CollectedContent ? SeriesMembershipScopeNames.CollectedContent : SeriesMembershipScopeNames.MainSequence,
            }).ToList(),
        };

        var merged = orchestrator.GetMethod("MergeCollectionManifestPlaceholders", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [DetailEntityType.BookSeries, typedWorks, manifest])!;
        var placement = (SequencePlacementViewModel)orchestrator.GetMethod("BuildCollectionSequencePlacement", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, BindingFlags.OptionalParamBinding, null,
            [
                Guid.NewGuid(), DetailEntityType.BookSeries, "The Expanse", Expanse, null, merged, null, null, null,
                Type.Missing, Type.Missing, Type.Missing, Type.Missing,
            ], CultureInfo.InvariantCulture)!;

        return (
            placement.OrderedItems.Select(item => $"{item.Title}:{item.PositionLabel}").ToList(),
            placement.AlsoInThisWorld.Select(item => $"{item.Title}:{item.PositionLabel}").ToList());
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Seeding the real data store for the native read
    // ----------------------------------------------------------------------------------------------------------------

    private async Task<(Guid CollectionId, List<Guid> WorkIds)> SeedShelfAsync()
    {
        var collections = new CollectionRepository(_db);
        var manifests = new SeriesManifestRepository(_db);
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = "The Expanse",
            WikidataQid = Expanse,
            MediaType = "Books",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        await collections.UpsertAsync(collection);

        var rows = new List<SeriesManifestItemRecord>();
        using (var conn = _db.CreateConnection())
        {
            foreach (var book in Expanse9)
            {
                var id = StableId(book.Title);
                _workIds[book.Title] = id;
                var sort = double.Parse(book.Position, CultureInfo.InvariantCulture);
                conn.Execute(
                    """
                    INSERT INTO works (id, collection_id, media_type, work_kind, ordinal, ordinal_sort)
                    VALUES (@id, @collectionId, 'Books', 'standalone', @ordinal, @sort);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'title', @title, @now);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@id, 'series_position', @position, @now);
                    INSERT INTO editions (id, work_id) VALUES (@edition, @id);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                    VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
                    """,
                    new
                    {
                        id,
                        collectionId = collection.Id,
                        ordinal = ToWhole(sort),
                        sort,
                        title = book.Title,
                        position = book.Position,
                        now = DateTimeOffset.UtcNow.ToString("O"),
                        edition = Guid.NewGuid(),
                        asset = Guid.NewGuid(),
                        hash = Guid.NewGuid().ToString("N"),
                        path = $"C:/library/{Guid.NewGuid():N}.epub",
                    });

                if (IsRetailOnly(book.Title))
                {
                    continue; // The manifest does not list it: a retail-only main-sequence member.
                }

                rows.Add(new SeriesManifestItemRecord
                {
                    Id = Guid.NewGuid(),
                    CollectionId = collection.Id,
                    SeriesQid = Expanse,
                    ItemQid = "Q-" + book.Title,
                    ItemLabel = book.Title,
                    MediaType = "Books",
                    RawOrdinal = book.CollectedContent ? null : book.Position,
                    ParsedOrdinal = book.CollectedContent ? null : sort,
                    OrdinalScopeQid = book.CollectedContent ? null : Expanse,
                    SortOrder = 99,
                    OrderSource = "SeriesOrdinal",
                    OwnershipState = "Owned",
                    LinkedWorkId = id,
                    MembershipScope = book.CollectedContent ? SeriesMembershipScopeNames.CollectedContent : SeriesMembershipScopeNames.MainSequence,
                    IsExpandedFromCollection = book.CollectedContent,
                    ParentCollectionQid = book.CollectedContent ? "QMemorysLegion" : null,
                    SourcePropertiesJson = "[]",
                    RelationshipsJson = "[]",
                    LastHydratedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        await manifests.UpsertManifestAsync(
            new SeriesManifestHydration
            {
                SeriesQid = Expanse,
                CollectionId = collection.Id,
                SeriesLabel = "The Expanse",
                WarningsJson = "[]",
                ApiMetadataJson = "{}",
                LastHydratedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            rows);
        return (collection.Id, _workIds.Values.ToList());
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------------------------

    private static bool IsRetailOnly(string title) => title is "The Butcher of Anderson Station" or "Strange Dog";

    private static int? ToWhole(double value) => Math.Abs(value - Math.Round(value)) < 0.0001 ? (int)Math.Round(value) : null;

    private static Guid StableId(string title)
    {
        var bytes = new byte[16];
        System.Text.Encoding.UTF8.GetBytes(title.PadRight(16)[..16]).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static string Describe(CollectionItemDto item) => $"{item.Title}:{item.PositionLabel}|{item.MembershipScope}";

    private static List<string> Run(IEnumerable<string> described)
        => described.Where(entry => entry.EndsWith("|" + SeriesMembershipScopeNames.MainSequence, StringComparison.Ordinal))
            .Select(entry => entry[..entry.IndexOf('|')]).ToList();

    private static List<string> Also(IEnumerable<string> described)
        => described.Where(entry => !entry.EndsWith("|" + SeriesMembershipScopeNames.MainSequence, StringComparison.Ordinal))
            .Select(entry => entry[..entry.IndexOf('|')]).ToList();

    private static object Cast(Type itemType, List<object> items)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static object? InvokeNullable(string name, params object?[] args)
    {
        var orchestrator = typeof(DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator", throwOnError: true)!;
        var method = orchestrator.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args);
    }

    private static T InvokeStatic<T>(string name, params object?[] args)
    {
        var orchestrator = typeof(DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator", throwOnError: true)!;
        var method = orchestrator.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(null, args));
    }
}
