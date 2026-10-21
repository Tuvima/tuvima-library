using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Intelligence;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Intelligence.Tests;

public sealed class ParentCollectionResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _repo;
    private readonly NarrativeRootRepository _roots;
    private readonly CanonicalValueRepository _canonicalValues;
    private readonly WorkRepository _works;
    private readonly ParentCollectionResolver _resolver;

    public ParentCollectionResolverTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_parent_collection_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new CollectionRepository(_db);
        _roots = new NarrativeRootRepository(_db);
        _canonicalValues = new CanonicalValueRepository(_db);
        _works = new WorkRepository(_db);
        _resolver = new ParentCollectionResolver(
            _repo,
            _roots,
            _canonicalValues,
            NullLogger<ParentCollectionResolver>.Instance,
            new QidLabelRepository(_db));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_CreatesParentForSharedBroaderFranchiseQid()
    {
        var books = CreateCollection("Dune book series", "ContentGroup", "Q6095696");
        var movies = CreateCollection("Dune film series", "ContentGroup", "Q109300883");
        await _repo.UpsertAsync(books);
        await _repo.UpsertAsync(movies);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(books.Id, "franchise", "Q18011049", "Dune"),
            CreateRelationship(movies.Id, "franchise", "Q18011049", "Dune"),
        ]);

        await ResolveAsync(books, movies);

        var refreshedBooks = await _repo.GetByIdAsync(books.Id);
        var refreshedMovies = await _repo.GetByIdAsync(movies.Id);
        Assert.NotNull(refreshedBooks);
        Assert.NotNull(refreshedBooks!.ParentCollectionId);
        Assert.NotNull(refreshedMovies);
        Assert.Equal(refreshedBooks.ParentCollectionId, refreshedMovies!.ParentCollectionId);

        var parent = await _repo.GetByIdAsync(refreshedBooks.ParentCollectionId!.Value);
        Assert.NotNull(parent);
        Assert.Equal(CollectionType.Universe, parent!.CollectionType);
        Assert.Equal("Dune", parent.DisplayName);
        Assert.Equal("Q18011049", parent.WikidataQid);

        var children = await _repo.GetChildCollectionsAsync(parent.Id);
        Assert.Contains(children, candidate => candidate.Id == books.Id);
        Assert.Contains(children, candidate => candidate.Id == movies.Id);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_DoesNotCreateParentForSingleBroaderFranchiseShelf()
    {
        var child = CreateCollection("The Matrix film series", "ContentGroup", "Q1228705");
        await _repo.UpsertAsync(child);
        await _repo.InsertRelationshipsAsync([CreateRelationship(child.Id, "franchise", "Q83495", "The Matrix")]);

        await _resolver.ResolveParentCollectionAsync(child.Id);

        var refreshedChild = await _repo.GetByIdAsync(child.Id);
        Assert.NotNull(refreshedChild);
        Assert.Null(refreshedChild!.ParentCollectionId);
        Assert.Null(await _repo.FindUniverseShelfByUniverseQidAsync("Q83495"));

        // The shelf still names its Universe, even though it is the only Series in it.
        Assert.Equal("Q83495", refreshedChild.UniverseQid);
        Assert.NotNull(await _roots.FindByQidAsync("Q83495"));
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_CreatesParentForSharedBroaderSeriesQid()
    {
        var lotr = CreateCollection("The Lord of the Rings", "ContentGroup", "Q190214");
        var hobbit = CreateCollection("The Hobbit trilogy", "ContentGroup", "Q74331");
        await _repo.UpsertAsync(lotr);
        await _repo.UpsertAsync(hobbit);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(lotr.Id, "series", "Q26214973", "Peter Jackson's Middle-earth film series"),
            CreateRelationship(hobbit.Id, "series", "Q26214973", "Peter Jackson's Middle-earth film series"),
        ]);

        await ResolveAsync(lotr, hobbit);

        var refreshedLotr = await _repo.GetByIdAsync(lotr.Id);
        var refreshedHobbit = await _repo.GetByIdAsync(hobbit.Id);
        Assert.NotNull(refreshedLotr);
        Assert.NotNull(refreshedHobbit);
        Assert.NotNull(refreshedHobbit!.ParentCollectionId);
        Assert.Equal(refreshedHobbit.ParentCollectionId, refreshedLotr!.ParentCollectionId);

        var parent = await _repo.GetByIdAsync(refreshedHobbit.ParentCollectionId!.Value);
        Assert.NotNull(parent);
        Assert.Equal("Peter Jackson's Middle-earth film series", parent!.DisplayName);
        Assert.Equal("Q26214973", parent.WikidataQid);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_CreatesParentForSharedBasedOnQid()
    {
        var comics = CreateCollection("Batman", "ContentGroup", "Q2633138");
        var movies = CreateCollection("Batman in film", "ContentGroup", "Q2111133");
        await _repo.UpsertAsync(comics);
        await _repo.UpsertAsync(movies);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(comics.Id, "based_on", "Q2695156", "Batman"),
            CreateRelationship(movies.Id, "based_on", "Q2695156", "Batman"),
        ]);

        await ResolveAsync(comics, movies);

        var refreshedComics = await _repo.GetByIdAsync(comics.Id);
        var refreshedMovies = await _repo.GetByIdAsync(movies.Id);
        Assert.NotNull(refreshedComics);
        Assert.NotNull(refreshedMovies);
        Assert.NotNull(refreshedComics!.ParentCollectionId);
        Assert.Equal(refreshedComics.ParentCollectionId, refreshedMovies!.ParentCollectionId);

        var parent = await _repo.GetByIdAsync(refreshedComics.ParentCollectionId!.Value);
        Assert.NotNull(parent);
        Assert.Equal("Batman", parent!.DisplayName);
        Assert.Equal("Q2695156", parent.WikidataQid);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_RollsBookSeriesAndTvAdaptationIntoFranchiseAcrossRelationshipTypes()
    {
        var books = CreateCollection("The Expanse books", "ContentGroup", "Q19610143", MediaType.Books);
        var television = CreateCollection("The Expanse TV series", "ContentGroup", "Q18389644", MediaType.TV);
        await _repo.UpsertAsync(books);
        await _repo.UpsertAsync(television);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(books.Id, "series", "Q19610143", "The Expanse"),
            CreateRelationship(television.Id, "based_on", "Q19610143", "The Expanse"),
        ]);

        await ResolveAsync(books, television);

        var refreshedBooks = await _repo.GetByIdAsync(books.Id);
        var refreshedTelevision = await _repo.GetByIdAsync(television.Id);
        Assert.NotNull(refreshedBooks?.ParentCollectionId);
        Assert.Equal(refreshedBooks!.ParentCollectionId, refreshedTelevision!.ParentCollectionId);

        var parent = await _repo.GetByIdAsync(refreshedBooks.ParentCollectionId!.Value);
        Assert.Equal("The Expanse", parent!.DisplayName);
        Assert.Equal("Q19610143", parent.WikidataQid);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_DoesNotCreateParentWhenRelationshipMatchesCollectionQid()
    {
        var child = CreateCollection("The Expanse", "ContentGroup", "Q19610143");
        await _repo.UpsertAsync(child);
        await _repo.InsertRelationshipsAsync([CreateRelationship(child.Id, "franchise", "Q19610143", "The Expanse")]);

        await _resolver.ResolveParentCollectionAsync(child.Id);

        var refreshedChild = await _repo.GetByIdAsync(child.Id);
        Assert.NotNull(refreshedChild);
        Assert.Null(refreshedChild!.ParentCollectionId);
        Assert.Null(await _repo.FindUniverseShelfByUniverseQidAsync("Q19610143"));
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_DoesNotMakeAUniverseFromTheBookAndAudiobookSeriesOfOneQid()
    {
        var books = CreateCollection("The Expanse", "ContentGroup", "Q19610143", MediaType.Books);
        var audiobooks = CreateCollection("The Expanse (audiobooks)", "ContentGroup", "Q19610143", MediaType.Audiobooks);
        await _repo.UpsertAsync(books);
        await _repo.UpsertAsync(audiobooks);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(books.Id, "series", "Q19610143", "The Expanse"),
            CreateRelationship(audiobooks.Id, "series", "Q19610143", "The Expanse"),
        ]);

        await ResolveAsync(books, audiobooks);

        var refreshedBooks = await _repo.GetByIdAsync(books.Id);
        var refreshedAudiobooks = await _repo.GetByIdAsync(audiobooks.Id);
        Assert.Null(refreshedBooks!.ParentCollectionId);
        Assert.Null(refreshedAudiobooks!.ParentCollectionId);
        Assert.Equal("Q19610143", refreshedBooks.UniverseQid);
        Assert.Equal("Q19610143", refreshedAudiobooks.UniverseQid);
        Assert.Null(await _repo.FindUniverseShelfByUniverseQidAsync("Q19610143"));
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_MakesAUniverseFromABookSeriesAndATvSeriesThatShareOneQid()
    {
        var books = CreateCollection("The Expanse", "ContentGroup", "Q19610143", MediaType.Books);
        var television = CreateCollection("The Expanse", "ContentGroup", "Q19610143", MediaType.TV);
        await _repo.UpsertAsync(books);
        await _repo.UpsertAsync(television);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(books.Id, "series", "Q19610143", "The Expanse"),
            CreateRelationship(television.Id, "series", "Q19610143", "The Expanse"),
        ]);

        await ResolveAsync(books, television);

        var universe = await _repo.FindUniverseShelfByUniverseQidAsync("Q19610143");
        Assert.NotNull(universe);
        var children = await _repo.GetChildCollectionsAsync(universe!.Id);
        Assert.Equal(2, children.Count);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_RunTwice_ChangesNothing()
    {
        var books = CreateCollection("Dune book series", "ContentGroup", "Q6095696");
        var movies = CreateCollection("Dune film series", "ContentGroup", "Q109300883");
        await _repo.UpsertAsync(books);
        await _repo.UpsertAsync(movies);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(books.Id, "franchise", "Q18011049", "Dune"),
            CreateRelationship(movies.Id, "franchise", "Q18011049", "Dune"),
        ]);
        await ResolveAsync(books, movies);
        var universeBefore = await _repo.FindUniverseShelfByUniverseQidAsync("Q18011049");

        await ResolveAsync(books, movies);
        await ResolveAsync(movies, books);

        var universeAfter = await _repo.FindUniverseShelfByUniverseQidAsync("Q18011049");
        Assert.Equal(universeBefore!.Id, universeAfter!.Id);
        Assert.Single(await _repo.GetByTypeAsync("Universe"));
        Assert.Equal(2, (await _repo.GetChildCollectionsAsync(universeAfter.Id)).Count);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_UniverseShelfPresentsTheRecordItNames()
    {
        var books = CreateCollection("Dune book series", "ContentGroup", "Q6095696");
        var movies = CreateCollection("Dune film series", "ContentGroup", "Q109300883");
        await _repo.UpsertAsync(books);
        await _repo.UpsertAsync(movies);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(books.Id, "franchise", "Q18011049", "Dune"),
            CreateRelationship(movies.Id, "franchise", "Q18011049", "Dune"),
        ]);

        await ResolveAsync(books, movies);

        var universe = await _repo.FindUniverseShelfByUniverseQidAsync("Q18011049");
        Assert.NotNull(universe);
        Assert.Equal(universe!.WikidataQid, universe.UniverseQid);
        var record = await _roots.FindByQidAsync("Q18011049");
        Assert.NotNull(record);
        Assert.Equal("Dune", record!.Label);
        Assert.Equal(record.Label, universe.DisplayName);
        Assert.All(await _repo.ListSeriesShelvesByUniverseQidAsync("Q18011049"), shelf => Assert.Equal("Q18011049", shelf.UniverseQid));
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_TvAssignedBeforeBooksWhoseWorksCarryAUniverse_EndsInTheBooksUniverse()
    {
        var television = CreateCollection("The Expanse TV series", "ContentGroup", "Q18389644", MediaType.TV);
        var books = CreateCollection("The Expanse books", "ContentGroup", "Q19610143", MediaType.Books);
        await _repo.UpsertAsync(television);
        await _repo.UpsertAsync(books);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(television.Id, "based_on", "Q19610143", "The Expanse"),
            CreateRelationship(books.Id, "series", "Q19610143", "The Expanse"),
            CreateRelationship(books.Id, "fictional_universe", "Q1000", "The Expanse universe"),
        ]);

        // TV first: nothing else is known yet, so it sits in the book Series' own record.
        await _resolver.ResolveParentCollectionAsync(television.Id);
        Assert.Equal("Q19610143", (await _repo.GetByIdAsync(television.Id))!.UniverseQid);

        await _resolver.ResolveParentCollectionAsync(books.Id);

        var refreshedTelevision = await _repo.GetByIdAsync(television.Id);
        var refreshedBooks = await _repo.GetByIdAsync(books.Id);
        Assert.Equal("Q1000", refreshedBooks!.UniverseQid);
        Assert.Equal("Q1000", refreshedTelevision!.UniverseQid);
        var universe = await _repo.FindUniverseShelfByUniverseQidAsync("Q1000");
        Assert.NotNull(universe);
        Assert.Equal(universe!.Id, refreshedBooks.ParentCollectionId);
        Assert.Equal(universe.Id, refreshedTelevision.ParentCollectionId);
        Assert.Equal("The Expanse universe", universe.DisplayName);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_BooksAssignedBeforeTv_EndsInTheSameUniverse()
    {
        var television = CreateCollection("The Expanse TV series", "ContentGroup", "Q18389644", MediaType.TV);
        var books = CreateCollection("The Expanse books", "ContentGroup", "Q19610143", MediaType.Books);
        await _repo.UpsertAsync(television);
        await _repo.UpsertAsync(books);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(television.Id, "based_on", "Q19610143", "The Expanse"),
            CreateRelationship(books.Id, "series", "Q19610143", "The Expanse"),
            CreateRelationship(books.Id, "fictional_universe", "Q1000", "The Expanse universe"),
        ]);

        await ResolveAsync(books, television);

        var universe = await _repo.FindUniverseShelfByUniverseQidAsync("Q1000");
        Assert.NotNull(universe);
        Assert.Equal(universe!.Id, (await _repo.GetByIdAsync(books.Id))!.ParentCollectionId);
        Assert.Equal(universe.Id, (await _repo.GetByIdAsync(television.Id))!.ParentCollectionId);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_ShelfMovingUniverse_LeavesNoStaleParentAndRemovesTheUnusedUniverseShelf()
    {
        var first = CreateCollection("Dune book series", "ContentGroup", "Q6095696");
        var second = CreateCollection("Dune film series", "ContentGroup", "Q109300883");
        await _repo.UpsertAsync(first);
        await _repo.UpsertAsync(second);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(first.Id, "franchise", "Q18011049", "Dune"),
            CreateRelationship(second.Id, "franchise", "Q18011049", "Dune"),
        ]);
        await ResolveAsync(first, second);
        var oldUniverse = await _repo.FindUniverseShelfByUniverseQidAsync("Q18011049");
        Assert.NotNull(oldUniverse);

        // Wikidata now puts the second shelf in a fictional universe, which outranks the franchise.
        await _repo.InsertRelationshipsAsync([CreateRelationship(second.Id, "fictional_universe", "Q3041974", "Arrakis")]);
        await _resolver.ResolveParentCollectionAsync(second.Id);

        var refreshedFirst = await _repo.GetByIdAsync(first.Id);
        var refreshedSecond = await _repo.GetByIdAsync(second.Id);
        Assert.Equal("Q18011049", refreshedFirst!.UniverseQid);
        Assert.Null(refreshedFirst.ParentCollectionId);
        Assert.Equal("Q3041974", refreshedSecond!.UniverseQid);
        Assert.Null(refreshedSecond.ParentCollectionId);
        Assert.Null(await _repo.GetByIdAsync(oldUniverse!.Id));
        Assert.Null(await _repo.FindUniverseShelfByUniverseQidAsync("Q3041974"));
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_ShelfMovingUniverse_KeepsAnEditedUniverseShelf()
    {
        var first = CreateCollection("Dune book series", "ContentGroup", "Q6095696");
        var second = CreateCollection("Dune film series", "ContentGroup", "Q109300883");
        await _repo.UpsertAsync(first);
        await _repo.UpsertAsync(second);
        await _repo.InsertRelationshipsAsync(
        [
            CreateRelationship(first.Id, "franchise", "Q18011049", "Dune"),
            CreateRelationship(second.Id, "franchise", "Q18011049", "Dune"),
        ]);
        await ResolveAsync(first, second);
        var oldUniverse = await _repo.FindUniverseShelfByUniverseQidAsync("Q18011049");
        oldUniverse!.Description = "Curated by the owner.";
        await _repo.UpsertAsync(oldUniverse);

        await _repo.InsertRelationshipsAsync([CreateRelationship(second.Id, "fictional_universe", "Q3041974", "Arrakis")]);
        await _resolver.ResolveParentCollectionAsync(second.Id);

        var kept = await _repo.GetByIdAsync(oldUniverse.Id);
        Assert.NotNull(kept);
        Assert.Empty(await _repo.GetChildCollectionsAsync(kept!.Id));
        Assert.Null((await _repo.GetByIdAsync(first.Id))!.ParentCollectionId);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_ShelfMovingUniverse_RewritesTheNarrativeRootOnItsWorks()
    {
        var shelf = CreateCollection("Dune film series", "ContentGroup", "Q109300883");
        await _repo.UpsertAsync(shelf);
        var workId = await _works.InsertParentAsync(MediaType.Movies, "movie:dune", null, null);
        await _repo.AssignWorkToCollectionAsync(workId, shelf.Id);
        await _canonicalValues.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = workId, Key = "narrative_root_qid", Value = "Q18011049", LastScoredAt = DateTimeOffset.UtcNow },
        ]);
        await _repo.InsertRelationshipsAsync([CreateRelationship(shelf.Id, "fictional_universe", "Q3041974", "Arrakis")]);

        await _resolver.ResolveParentCollectionAsync(shelf.Id);

        var values = await _canonicalValues.GetByEntityAsync(workId);
        Assert.Equal("Q3041974", values.Single(value => value.Key == "narrative_root_qid").Value);
        Assert.Equal("Arrakis", values.Single(value => value.Key == "narrative_root_label").Value);
    }

    [Fact]
    public async Task ResolveParentCollectionAsync_ShelfWithNoSeriesQidAndNoRollup_NamesNoUniverse()
    {
        await _roots.EnsureAsync(new MediaEngine.Domain.Models.NarrativeRoot { Qid = "Q5", Label = "Old universe", Level = NarrativeLevel.Universe });
        var loose = CreateCollection("Loose shelf", "ContentGroup", null);
        await _repo.UpsertAsync(loose);
        await _repo.SetUniverseAsync(loose.Id, "Q5", null);

        await _resolver.ResolveParentCollectionAsync(loose.Id);

        var refreshed = await _repo.GetByIdAsync(loose.Id);
        Assert.Null(refreshed!.UniverseQid);
        Assert.Null(refreshed.ParentCollectionId);
    }

    private async Task ResolveAsync(params Collection[] shelves)
    {
        foreach (var shelf in shelves)
        {
            await _resolver.ResolveParentCollectionAsync(shelf.Id);
        }
    }

    private static Collection CreateCollection(string name, string type, string? qid, MediaType? mediaType = null)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            WikidataQid = qid,
            MediaType = mediaType?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        collection.RestoreDefinition(
            AggregateStateSerializer.ParseCollectionType(type),
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Desc,
            CollectionUniverseStatus.Unknown);
        return collection;
    }

    private static CollectionRelationship CreateRelationship(Guid collectionId, string relType, string qid, string label) => new()
    {
        Id = Guid.NewGuid(),
        CollectionId = collectionId,
        RelType = relType,
        RelQid = qid,
        RelLabel = label,
        Confidence = 1.0,
        DiscoveredAt = DateTimeOffset.UtcNow,
    };
}
