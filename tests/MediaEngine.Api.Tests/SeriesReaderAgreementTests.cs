using System.Globalization;
using System.Reflection;
using Dapper;
using MediaEngine.Api.Services.Details;
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
