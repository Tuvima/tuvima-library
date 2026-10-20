using Dapper;
using MediaEngine.Intelligence;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// One Universe record, one rule. A book, its audiobook and its TV adaptation run through the real shelf assignment, the
/// real Universe shelf decision and the real Work-level narrative root, against a temp data store, in either order. They
/// must end in one Universe shelf holding three Series shelves, all naming the same Universe record, and every Work
/// must root at that record.
/// </summary>
public sealed class UniverseRecordTests : IDisposable
{
    private const string Expanse = "QExpanse";
    private const string ExpanseTv = "QExpanseTv";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly CollectionRepository _collections;
    private readonly NarrativeRootRepository _roots;
    private readonly CollectionAssignmentService _assignment;
    private readonly ParentCollectionResolver _parents;
    private readonly NarrativeRootResolver _narrativeRoots;
    private readonly Dictionary<string, (Guid WorkId, Guid AssetId)> _works = new();

    public UniverseRecordTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_universe_record_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _collections = new CollectionRepository(_db);
        _roots = new NarrativeRootRepository(_db);
        var canonicals = new CanonicalValueRepository(_db);
        var arrays = new CanonicalValueArrayRepository(_db);
        var labels = new QidLabelRepository(_db);
        _assignment = new CollectionAssignmentService(
            _collections,
            canonicals,
            new WorkRepository(_db),
            NullLogger<CollectionAssignmentService>.Instance,
            arrays);
        _parents = new ParentCollectionResolver(
            _collections,
            _roots,
            canonicals,
            NullLogger<ParentCollectionResolver>.Instance,
            labels);
        _narrativeRoots = new NarrativeRootResolver(
            canonicals,
            arrays,
            _roots,
            _collections,
            labels,
            new SystemActivityRepository(_db),
            NullLogger<NarrativeRootResolver>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABookItsAudiobookAndItsTvAdaptation_EndUpUnderOneUniverseRecord_InEitherOrder(bool tvFirst)
    {
        AddBook("Leviathan Wakes", "Books");
        AddBook("Leviathan Wakes (audiobook)", "Audiobooks");
        AddTvAdaptation("The Expanse (TV)");
        string[] order = tvFirst
            ? ["The Expanse (TV)", "Leviathan Wakes", "Leviathan Wakes (audiobook)"]
            : ["Leviathan Wakes", "Leviathan Wakes (audiobook)", "The Expanse (TV)"];

        await AssignAsync(order);

        await AssertOneUniverseAsync();

        // Running everything again changes nothing.
        await AssignAsync(order);
        await AssignAsync(order.Reverse().ToArray());
        await AssertOneUniverseAsync();
    }

    [Fact]
    public async Task ABookAndItsAudiobookAlone_HaveAUniverseRecordButNoUniverseShelf()
    {
        AddBook("Leviathan Wakes", "Books");
        AddBook("Leviathan Wakes (audiobook)", "Audiobooks");

        await AssignAsync("Leviathan Wakes", "Leviathan Wakes (audiobook)");

        Assert.Empty(await _collections.GetByTypeAsync("Universe"));
        var books = (await _collections.FindSeriesShelfAsync(Expanse, "Books"))!;
        var audiobooks = (await _collections.FindSeriesShelfAsync(Expanse, "Audiobooks"))!;
        Assert.Equal(Expanse, books.UniverseQid);
        Assert.Equal(Expanse, audiobooks.UniverseQid);
        Assert.Null(books.ParentCollectionId);
        Assert.NotNull(await _roots.FindByQidAsync(Expanse));
        Assert.Equal(Expanse, (await _narrativeRoots.ResolveAsync(_works["Leviathan Wakes"].WorkId))!.Qid);
    }

    [Fact]
    public async Task BooksNamingAFictionalUniverse_BringTheirAdaptationIntoIt_EvenWhenTheAdaptationCameFirst()
    {
        const string ExpanseUniverse = "Q900";
        const string Holden = "Q500";
        AddBook("Leviathan Wakes", "Books");
        AddBook("Leviathan Wakes (audiobook)", "Audiobooks");
        AddTvAdaptation("The Expanse (TV)");
        foreach (var title in new[] { "Leviathan Wakes", "Leviathan Wakes (audiobook)" })
        {
            // Wikidata stores P1434 as a multi-valued field. One value is really a character of the book (a data slip
            // the Universe rule must ignore); the real fictional universe is the other one.
            AddArray(title, "fictional_universe", ("James Holden", Holden), ("The Expanse universe", ExpanseUniverse));
            AddArray(title, "characters", ("James Holden", Holden));
        }

        await AssignAsync("The Expanse (TV)", "Leviathan Wakes", "Leviathan Wakes (audiobook)");

        var universe = Assert.Single(await _collections.GetByTypeAsync("Universe"));
        Assert.Equal(ExpanseUniverse, universe.UniverseQid);
        var television = (await _collections.FindSeriesShelfAsync(ExpanseTv, "TV"))!;
        Assert.Equal(ExpanseUniverse, television.UniverseQid);
        Assert.Equal(universe.Id, television.ParentCollectionId);
        Assert.Equal(3, (await _collections.GetChildCollectionsAsync(universe.Id)).Count);
        foreach (var (workId, _) in _works.Values)
        {
            Assert.Equal(ExpanseUniverse, (await _narrativeRoots.ResolveAsync(workId))!.Qid);
        }
    }

    private void AddArray(string title, string key, params (string Value, string Qid)[] entries)
    {
        using var conn = _db.CreateConnection();
        for (var ordinal = 0; ordinal < entries.Length; ordinal++)
        {
            conn.Execute(
                "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid) VALUES (@workId, @key, @ordinal, @value, @qid);",
                new { workId = _works[title].WorkId, key, ordinal, value = entries[ordinal].Value, qid = entries[ordinal].Qid });
        }
    }

    private async Task AssertOneUniverseAsync()
    {
        var universes = await _collections.GetByTypeAsync("Universe");
        var universe = Assert.Single(universes);
        Assert.Equal(Expanse, universe.UniverseQid);
        Assert.Equal(Expanse, universe.WikidataQid);

        var record = await _roots.FindByQidAsync(Expanse);
        Assert.NotNull(record);

        var books = (await _collections.FindSeriesShelfAsync(Expanse, "Books"))!;
        var audiobooks = (await _collections.FindSeriesShelfAsync(Expanse, "Audiobooks"))!;
        var television = (await _collections.FindSeriesShelfAsync(ExpanseTv, "TV"))!;
        Assert.Equal(3, new[] { books.Id, audiobooks.Id, television.Id }.Distinct().Count());
        foreach (var shelf in new[] { books, audiobooks, television })
        {
            Assert.Equal(Expanse, shelf.UniverseQid);
            Assert.Equal(universe.Id, shelf.ParentCollectionId);
        }

        Assert.Equal(3, (await _collections.GetChildCollectionsAsync(universe.Id)).Count);

        // The graph roots every Work at the same record the Library presents.
        foreach (var (workId, _) in _works.Values)
        {
            var root = await _narrativeRoots.ResolveAsync(workId);
            Assert.Equal(Expanse, root!.Qid);
        }
    }

    private async Task AssignAsync(params string[] titles)
    {
        foreach (var title in titles)
        {
            // The same two steps the quick hydration worker runs: place the work, then decide the Series shelf's Universe.
            var result = await _assignment.AssignAsync(_works[title].AssetId);
            Assert.NotNull(result.CollectionId);
            await _parents.ResolveParentCollectionAsync(result.CollectionId!.Value);
        }
    }

    private void AddBook(string title, string mediaType) =>
        AddWork(title, mediaType, new Dictionary<string, string>
        {
            ["series"] = "The Expanse",
            ["series_qid"] = Expanse,
        });

    private void AddTvAdaptation(string title) =>
        AddWork(title, "TV", new Dictionary<string, string>
        {
            ["series"] = "The Expanse (TV series)",
            ["series_qid"] = ExpanseTv,
            ["based_on"] = "The Expanse",
            ["based_on_qid"] = Expanse,
        });

    private void AddWork(string title, string mediaType, Dictionary<string, string> claims)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, collection_id, media_type, work_kind, wikidata_qid, external_identifiers)
            VALUES (@workId, NULL, @mediaType, 'standalone', NULL, '{}');
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Edition');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @contentHash, @filePath);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                mediaType,
                contentHash = Guid.NewGuid().ToString("N"),
                filePath = $"{title}.bin",
            });

        var values = new Dictionary<string, string>(claims)
        {
            ["title"] = title,
            ["media_type"] = mediaType,
        };
        foreach (var (key, value) in values)
        {
            conn.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, @key, @value, @now);",
                new { workId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        _works[title] = (workId, assetId);
    }
}
