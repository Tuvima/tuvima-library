using System.IO.Compression;
using System.Text;
using Dapper;
using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Intelligence.Services;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Imported lists (Collections and lists spec D5, D6, 4d): each site's export is read, matched against a small seeded library
/// by identifiers first and then by title and year, kept as a collection's list source, and refreshed without touching the
/// titles a person added by hand. The fixtures are small hand-made files, never real user data.
/// </summary>
public sealed class ListImportTests : IDisposable
{
    private const string ImdbExport =
        "Const,Your Rating,Date Rated,Title,URL,Title Type,IMDb Rating,Runtime (mins),Year,Genres,Num Votes,Release Date,Directors\r\n"
        + "tt0113277,9,2020-01-02,Heat,https://www.imdb.com/title/tt0113277/,movie,8.3,170,1995,\"Crime, Drama\",700000,1995-12-15,Michael Mann\r\n"
        + "tt0133093,8,2020-01-03,The Matrix,https://www.imdb.com/title/tt0133093/,movie,8.7,136,1999,\"Action, Sci-Fi\",2000000,1999-03-31,The Wachowskis\r\n"
        + "tt0083658,8,2020-01-04,Blade Runner,https://www.imdb.com/title/tt0083658/,movie,8.1,117,1982,Sci-Fi,800000,1982-06-25,Ridley Scott\r\n"
        + "tt0078748,7,2020-01-05,Alien,https://www.imdb.com/title/tt0078748/,movie,8.5,117,1979,Horror,900000,1979-05-25,Ridley Scott\r\n";

    private const string LetterboxdExport =
        "Date,Name,Year,Letterboxd URI\r\n"
        + "2021-03-01,Heat,1995,https://boxd.it/1abc\r\n"
        + "2021-03-02,Arrival,2016,https://boxd.it/2abc\r\n"
        + "2021-03-03,Primer,2004,https://boxd.it/3abc\r\n";

    private const string TraktExport =
        "listed_at,type,title,year,imdb,tmdb,tvdb,slug\r\n"
        + "2022-01-01T10:00:00.000Z,movie,The Matrix,1999,tt0133093,603,,the-matrix-1999\r\n"
        + "2022-01-02T10:00:00.000Z,movie,Some Unseen Film,2010,tt9999999,999999,,some-unseen-film-2010\r\n";

    private const string GoodreadsExport =
        "Book Id,Title,Author,ISBN,ISBN13,My Rating,Year Published,Exclusive Shelf,Bookshelves\r\n"
        + "1,Dune,Frank Herbert,=\"0441172717\",=\"9780441172719\",5,1965,to-read,\"favourites\"\r\n"
        + "2,Neuromancer,William Gibson,=\"0441569595\",=\"9780441569595\",4,1984,to-read,\r\n"
        + "3,Hyperion,Dan Simmons,=\"\",=\"\",5,1989,read,\"favourites\"\r\n"
        + "4,Snow Crash,Neal Stephenson,=\"\",=\"\",3,1992,read,\r\n";

    private const string StoryGraphExport =
        "Title,Authors,Contributors,ISBN/UID,Format,Read Status,Date Added,Last Date Read,Dates Read,Read Count,Moods,Pace,Character- or Plot-Driven?,Strong Character Development?,Loveable Characters?,Diverse Characters?,Flawed Characters?,Star Rating,Review,Content Warnings,Content Warning Description,Tags,Owned?\r\n"
        + "Dune,Frank Herbert,,9780441172719,paperback,to-read,2023/01/01,,,0,,,,,,,,,,,,,No\r\n"
        + "Hyperion,Dan Simmons,,9780553283686,paperback,read,2023/01/02,,,1,,,,,,,,4.5,,,,,No\r\n"
        + "The Left Hand of Darkness,Ursula K. Le Guin,,9780441478125,paperback,read,2023/01/03,,,1,,,,,,,,4.0,,,,,No\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_list_import_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly CollectionListRepository _lists;
    private readonly CandidateSpy _candidates = new();
    private readonly ListImportService _service;

    private readonly Guid _heat;
    private readonly Guid _matrix;
    private readonly Guid _alien;
    private readonly Guid _arrival;
    private readonly Guid _dune;
    private readonly Guid _neuromancer;
    private readonly Guid _hyperion;

    public ListImportTests()
    {
        Directory.CreateDirectory(_root);
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _lists = new CollectionListRepository(_db);
        _service = new ListImportService(
            _lists,
            _candidates,
            new FuzzyMatchingService(),
            new ConfigurationDirectoryLoader(Path.Combine(_root, "config")),
            new LookupStub());

        _heat = SeedWork("Movies", "Heat", 1995, "Michael Mann", ("imdb_id", "tt0113277"));
        _matrix = SeedWork("Movies", "The Matrix", 1999, "The Wachowskis", ("imdb_id", "tt0133093"), ("tmdb_id", "603"));
        _alien = SeedWork("Movies", "Alien", 1979, "Ridley Scott");
        _arrival = SeedWork("Movies", "Arrival", 2016, "Denis Villeneuve");
        _dune = SeedWork("Books", "Dune", 1965, "Frank Herbert", ("isbn", "9780441172719"));
        _neuromancer = SeedWork("Books", "Neuromancer", 1984, "William Gibson");
        _hyperion = SeedWork("Books", "Hyperion", 1989, "Dan Simmons");
    }

    // ── Reading each site's export ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnImdbExport_IsReadWithItsIdentifiersAndYears()
    {
        var parsed = Parse("ratings.csv", ImdbExport);

        Assert.Equal(ListSourceKinds.Imdb, parsed.SourceKind);
        Assert.Equal(4, parsed.Entries.Count);
        Assert.Equal("Heat", parsed.Entries[0].Title);
        Assert.Equal(1995, parsed.Entries[0].Year);
        Assert.Equal("tt0113277", parsed.Entries[0].Ids[ListIdentifierKeys.Imdb]);
        Assert.Equal(["Movies"], parsed.Entries[0].MediaTypes);
    }

    [Fact]
    public void ALetterboxdExport_IsRecognisedByItsAddressColumn()
    {
        var parsed = Parse("watched.csv", LetterboxdExport);

        Assert.Equal(ListSourceKinds.Letterboxd, parsed.SourceKind);
        Assert.Equal(["Heat", "Arrival", "Primer"], parsed.Entries.Select(entry => entry.Title).ToArray());
        Assert.Equal(2016, parsed.Entries[1].Year);
    }

    [Fact]
    public void ALetterboxdZip_ReadsItsWatchlistAndListsAsShelves()
    {
        var zip = BuildZip(
            ("watchlist.csv", "Date,Name,Year,Letterboxd URI\r\n2021-03-01,Heat,1995,https://boxd.it/1abc\r\n"),
            ("lists/road-trip.csv", "Letterboxd list export v7\r\nDate,Name,Tags,URL,Description\r\n2021-04-01,Road trip,,https://boxd.it/xyz,\r\n\r\nPosition,Name,Year,URL,Description\r\n1,Arrival,2016,https://boxd.it/2abc,\r\n2,Alien,1979,https://boxd.it/4abc,\r\n"),
            ("profile.csv", "Date Joined,Username\r\n2020-01-01,someone\r\n"));

        var parsed = ListFileParser.Parse("letterboxd-export.zip", zip);

        Assert.Equal(ListSourceKinds.Letterboxd, parsed.SourceKind);
        Assert.Contains(parsed.Entries, entry => entry.Title == "Heat");
        Assert.Contains(parsed.Entries, entry => entry.Title == "Arrival");
        Assert.NotEmpty(parsed.Shelves);
    }

    [Fact]
    public void ATraktExport_IsReadByItsHeaderNames()
    {
        var parsed = Parse("trakt-watchlist.csv", TraktExport);

        Assert.Equal(ListSourceKinds.Trakt, parsed.SourceKind);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("tt0133093", parsed.Entries[0].Ids[ListIdentifierKeys.Imdb]);
        Assert.Equal("603", parsed.Entries[0].Ids[ListIdentifierKeys.Tmdb]);
    }

    [Fact]
    public void AGoodreadsExport_OffersEachShelfAndReadsSpreadsheetStyleIsbns()
    {
        var parsed = Parse("goodreads_library_export.csv", GoodreadsExport);

        Assert.Equal(ListSourceKinds.Goodreads, parsed.SourceKind);
        Assert.Equal(4, parsed.Entries.Count);
        Assert.Equal("9780441172719", parsed.Entries[0].Ids[ListIdentifierKeys.Isbn]);
        Assert.Contains(parsed.Shelves, shelf => shelf.Name == "to-read" && shelf.Count == 2);
        Assert.Contains(parsed.Shelves, shelf => shelf.Name == "read" && shelf.Count == 2);
        Assert.Contains(parsed.Shelves, shelf => shelf.Name == "favourites" && shelf.Count == 2);
    }

    [Fact]
    public void AStoryGraphExport_UsesReadStatusAsItsShelves()
    {
        var parsed = Parse("storygraph.csv", StoryGraphExport);

        Assert.Equal(ListSourceKinds.StoryGraph, parsed.SourceKind);
        Assert.Equal(3, parsed.Entries.Count);
        Assert.Contains(parsed.Shelves, shelf => shelf.Name == "to-read" && shelf.Count == 1);
        Assert.Contains(parsed.Shelves, shelf => shelf.Name == "read" && shelf.Count == 2);
    }

    [Fact]
    public void PlainText_TakesOneTitlePerLineWithAnOptionalYear()
    {
        var parsed = Parse("watch-later.txt", "Heat (1995)\r\nAlien\r\n\r\nNothing Here (2001)\r\n");

        Assert.Equal(ListSourceKinds.Text, parsed.SourceKind);
        Assert.Equal(["Heat", "Alien", "Nothing Here"], parsed.Entries.Select(entry => entry.Title).ToArray());
        Assert.Equal(1995, parsed.Entries[0].Year);
        Assert.Null(parsed.Entries[1].Year);
    }

    [Fact]
    public void AFileThatIsNotAList_GetsTheStandardSentence()
    {
        var error = Assert.Throws<ListFileNotUnderstoodException>(() => ListFileParser.Parse("empty.csv", Encoding.UTF8.GetBytes("Title,Year\r\n")));

        Assert.Equal(ListFileNotUnderstoodException.StandardMessage, error.Message);
    }

    // ── Matching ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnImdbExport_MatchesByIdentifierThenByTitleAndYear()
    {
        var preview = await PreviewAsync("ratings.csv", ImdbExport);

        // Heat and The Matrix by their IMDb ids, Alien by title and year; Blade Runner is not in the library.
        Assert.Equal(4, preview.EntryCount);
        Assert.Equal(3, preview.OwnedCount);
        Assert.Equal(1, preview.UnmatchedCount);
        var unmatched = Assert.Single(preview.Entries, entry => entry.Status == ListImportChoices.StatusUnmatched);
        Assert.Equal("Blade Runner", unmatched.Title);
    }

    [Fact]
    public async Task ALetterboxdExport_MatchesByTitleAndYear()
    {
        var preview = await PreviewAsync("watched.csv", LetterboxdExport);

        Assert.Equal(3, preview.EntryCount);
        Assert.Equal(2, preview.OwnedCount);
        Assert.Equal(1, preview.UnmatchedCount);
        Assert.Equal("Primer", Assert.Single(preview.Entries).Title);
    }

    [Fact]
    public async Task ATraktExport_MatchesByIdentifiers()
    {
        var preview = await PreviewAsync("trakt-watchlist.csv", TraktExport);

        Assert.Equal(1, preview.OwnedCount);
        Assert.Equal(1, preview.UnmatchedCount);
        Assert.Equal("Some Unseen Film", Assert.Single(preview.Entries).Title);
    }

    [Fact]
    public async Task AGoodreadsExport_MatchesBooksAndCountsTheShelves()
    {
        var all = await PreviewAsync("goodreads.csv", GoodreadsExport);
        var toRead = await PreviewAsync("goodreads.csv", GoodreadsExport, shelf: "to-read");

        Assert.Equal(4, all.EntryCount);
        Assert.Equal(3, all.OwnedCount);
        Assert.Equal(1, all.UnmatchedCount);
        Assert.Equal(2, toRead.EntryCount);
        Assert.Equal(2, toRead.OwnedCount);
        Assert.Empty(toRead.Entries);
        Assert.Equal("Snow Crash", Assert.Single(all.Entries).Title);
    }

    [Fact]
    public async Task AStoryGraphExport_MatchesByIsbnAndByTitle()
    {
        var preview = await PreviewAsync("storygraph.csv", StoryGraphExport);

        // Dune by ISBN; Hyperion has a different ISBN here so it matches on title and author; the third book is not owned.
        Assert.Equal(3, preview.EntryCount);
        Assert.Equal(2, preview.OwnedCount);
        Assert.Equal(1, preview.UnmatchedCount);
    }

    [Fact]
    public async Task ATitleWithTheWrongYear_IsNeverMatchedBelowTheLine()
    {
        var preview = await PreviewAsync("list.txt", "Heat (1972)\r\nAlien (1999)\r\n");

        Assert.Equal(0, preview.OwnedCount);
        Assert.Equal(2, preview.UnmatchedCount);
    }

    [Fact]
    public async Task APickedTitleAndAnIgnoredEntry_OverrideTheAutomaticMatch()
    {
        var preview = await _service.PreviewAsync(
            "ratings.csv",
            Parse("ratings.csv", ImdbExport),
            shelf: null,
            overrides:
            [
                new ListEntryOverrideDto { Position = 3, WorkId = _arrival },
                new ListEntryOverrideDto { Position = 4, Ignore = true },
            ],
            allowedWorkIds: null,
            CancellationToken.None);

        var handFixed = Assert.Single(preview.Entries, entry => entry.Status == ListImportChoices.StatusHandFixed);
        Assert.Equal("Blade Runner", handFixed.Title);
        Assert.Equal(_arrival, handFixed.MatchedWorkId);
        Assert.Equal("Alien", Assert.Single(preview.Entries, entry => entry.Status == ListImportChoices.StatusIgnored).Title);
        Assert.Equal(0, preview.UnmatchedCount);
    }

    // ── Import and refresh ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Importing_FillsTheCollectionWithTheMatchedTitlesInListOrder()
    {
        var collectionId = SeedCollection();

        var result = await ImportAsync(collectionId, "ratings.csv", ImdbExport);

        Assert.Equal(4, result.EntryCount);
        Assert.Equal(3, result.MatchedCount);
        Assert.Equal(1, result.UnmatchedCount);
        Assert.Equal([_heat, _matrix, _alien], ItemWorkIds(collectionId));
        var source = await _service.GetSourceAsync(collectionId, CancellationToken.None);
        Assert.NotNull(source);
        Assert.Equal("Blade Runner", Assert.Single(source!.Unmatched).Title);
    }

    [Fact]
    public async Task TheOrderChoice_IsReadFromTheListReleaseDateOrTitle()
    {
        var byTitle = SeedCollection();
        var byRelease = SeedCollection();

        await ImportAsync(byTitle, "ratings.csv", ImdbExport, order: ListImportChoices.OrderTitle);
        await ImportAsync(byRelease, "ratings.csv", ImdbExport, order: ListImportChoices.OrderRelease);

        Assert.Equal([_alien, _heat, _matrix], ItemWorkIds(byTitle));
        Assert.Equal([_alien, _heat, _matrix], ItemWorkIds(byRelease));
    }

    [Fact]
    public async Task RefreshingWithFollowTheList_RemovesADroppedEntryAndKeepsAHandAddedTitle()
    {
        var collectionId = SeedCollection();
        await ImportAsync(collectionId, "ratings.csv", ImdbExport);
        AddByHand(collectionId, _arrival);

        // Heat is dropped from the list.
        var newer = Parse("ratings.csv", ImdbExport.Replace(
            "tt0113277,9,2020-01-02,Heat,https://www.imdb.com/title/tt0113277/,movie,8.3,170,1995,\"Crime, Drama\",700000,1995-12-15,Michael Mann\r\n",
            string.Empty,
            StringComparison.Ordinal));

        var result = await _service.RefreshAsync(collectionId, newer, allowedWorkIds: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Removed);
        var works = ItemWorkIds(collectionId);
        Assert.DoesNotContain(_heat, works);
        Assert.Contains(_arrival, works);
        Assert.Contains(_matrix, works);
        Assert.Contains(_alien, works);
    }

    [Fact]
    public async Task RefreshingWithKeepEverythingEverAdded_KeepsTheTitleThatLeftTheList()
    {
        var collectionId = SeedCollection();
        await ImportAsync(collectionId, "ratings.csv", ImdbExport, sync: ListImportChoices.SyncKeep);

        var newer = Parse("ratings.csv", ImdbExport.Replace(
            "tt0113277,9,2020-01-02,Heat,https://www.imdb.com/title/tt0113277/,movie,8.3,170,1995,\"Crime, Drama\",700000,1995-12-15,Michael Mann\r\n",
            string.Empty,
            StringComparison.Ordinal));

        await _service.RefreshAsync(collectionId, newer, allowedWorkIds: null, CancellationToken.None);

        Assert.Contains(_heat, ItemWorkIds(collectionId));
    }

    [Fact]
    public async Task RefreshingWithoutAFile_PicksUpATitleTheLibraryGainedSince()
    {
        var collectionId = SeedCollection();
        await ImportAsync(collectionId, "ratings.csv", ImdbExport);
        Assert.DoesNotContain(Guid.Empty, ItemWorkIds(collectionId));
        var bladeRunner = SeedWork("Movies", "Blade Runner", 1982, "Ridley Scott", ("imdb_id", "tt0083658"));

        var result = await _service.RefreshAsync(collectionId, fresh: null, allowedWorkIds: null, CancellationToken.None);

        Assert.Equal(4, result!.Matched);
        Assert.Equal(0, result.Unmatched);
        Assert.Contains(bladeRunner, ItemWorkIds(collectionId));
    }

    [Fact]
    public async Task HandFixingAnEntry_AddsThePickedTitleAndSurvivesARefresh()
    {
        var collectionId = SeedCollection();
        await ImportAsync(collectionId, "ratings.csv", ImdbExport);
        var source = await _service.GetSourceAsync(collectionId, CancellationToken.None);
        var entry = Assert.Single(source!.Unmatched);

        Assert.True(await _service.FixEntryAsync(collectionId, entry.Id!.Value, "pick", _arrival, allowedWorkIds: null, CancellationToken.None));
        await _service.RefreshAsync(collectionId, fresh: null, allowedWorkIds: null, CancellationToken.None);

        Assert.Contains(_arrival, ItemWorkIds(collectionId));
        Assert.Empty((await _service.GetSourceAsync(collectionId, CancellationToken.None))!.Unmatched);
    }

    [Fact]
    public async Task IgnoringAnEntry_TakesItOffTheUnmatchedListAndOffTheCount()
    {
        var collectionId = SeedCollection();
        await ImportAsync(collectionId, "ratings.csv", ImdbExport);
        var entry = Assert.Single((await _service.GetSourceAsync(collectionId, CancellationToken.None))!.Unmatched);

        Assert.True(await _service.FixEntryAsync(collectionId, entry.Id!.Value, "ignore", workId: null, allowedWorkIds: null, CancellationToken.None));

        var source = (await _service.GetSourceAsync(collectionId, CancellationToken.None))!;
        // The ignored entry stays listed, so it can be put back, but it no longer counts as missing.
        Assert.Equal(ListImportChoices.StatusIgnored, Assert.Single(source.Unmatched).Status);
        Assert.Equal(0, source.UnmatchedCount);
        Assert.Equal(3, source.EntryCount);
    }

    [Fact]
    public async Task TheRefreshJob_RefreshesOnlyListsThatAreDue()
    {
        var due = SeedCollection();
        var never = SeedCollection();
        await ImportAsync(due, "ratings.csv", ImdbExport, refresh: ListImportChoices.RefreshDaily);
        await ImportAsync(never, "ratings.csv", ImdbExport, refresh: ListImportChoices.RefreshNever);
        var bladeRunner = SeedWork("Movies", "Blade Runner", 1982, "Ridley Scott", ("imdb_id", "tt0083658"));
        using (var connection = _db.CreateConnection())
        {
            connection.Execute("UPDATE collection_list_sources SET next_refresh_at = @past WHERE collection_id = @due",
                new { past = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"), due });
        }

        var worker = new CollectionListRefreshWorker(
            _lists, _service, [], new RecorderStub(), Microsoft.Extensions.Logging.Abstractions.NullLogger<CollectionListRefreshWorker>.Instance);
        var refreshed = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, refreshed);
        Assert.Contains(bladeRunner, ItemWorkIds(due));
        Assert.DoesNotContain(bladeRunner, ItemWorkIds(never));
    }

    [Fact]
    public async Task ACollectionMadeFromAList_IsNotRemovedByTheEmptyCollectionCleanup()
    {
        var collectionId = SeedCollection();
        await ImportAsync(collectionId, "none.txt", "Nothing Here (2001)\r\n");

        var removed = await new CollectionRepository(_db).DeleteEmptyCuratedCollectionsAsync();

        Assert.Empty(removed);
        Assert.NotNull(await new CollectionRepository(_db).GetByIdAsync(collectionId));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ParsedList Parse(string fileName, string text) => ListFileParser.Parse(fileName, Encoding.UTF8.GetBytes(text));

    private async Task<ListImportPreviewDto> PreviewAsync(string fileName, string text, string? shelf = null) =>
        await _service.PreviewAsync(fileName, Parse(fileName, text), shelf, [], allowedWorkIds: null, CancellationToken.None);

    private Task<ListImportResultDto> ImportAsync(
        Guid collectionId,
        string fileName,
        string text,
        string order = ListImportChoices.OrderList,
        string refresh = ListImportChoices.RefreshNever,
        string sync = ListImportChoices.SyncFollow) =>
        _service.ImportAsync(
            collectionId, fileName, Parse(fileName, text), shelf: null, order, refresh, sync, [], allowedWorkIds: null, CancellationToken.None);

    private static byte[] BuildZip(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private Guid SeedWork(string mediaType, string title, int year, string creator, params (string Key, string Value)[] ids)
    {
        var workId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, @mediaType, 'standalone', 'Owned');",
            new { workId, mediaType });
        _candidates.Add(new ListImportCandidate(
            workId, mediaType, title, year, creator, ids.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        return workId;
    }

    private Guid SeedCollection()
    {
        var collectionId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
            VALUES (@collectionId, 'Imported', 'Custom', 'library', 'materialized', 'Manual', 'Library', 'Everyone');
            """, new { collectionId });
        return collectionId;
    }

    private void AddByHand(Guid collectionId, Guid workId)
    {
        using var connection = _db.CreateConnection();
        connection.Execute("""
            INSERT INTO collection_items (id, collection_id, work_id, sort_order, added_by)
            VALUES (@id, @collectionId, @workId, 99, 'hand');
            """, new { id = Guid.NewGuid(), collectionId, workId });
    }

    private List<Guid> ItemWorkIds(Guid collectionId)
    {
        using var connection = _db.CreateConnection();
        return connection.Query<Guid>(
            "SELECT work_id FROM collection_items WHERE collection_id = @collectionId ORDER BY sort_order",
            new { collectionId }).ToList();
    }

    public void Dispose()
    {
        _db.Dispose();
        using (var pool = new SqliteConnection($"Data Source={Path.Combine(_root, "library.db")}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is cleaned up by the operating system later.
        }
    }

    private sealed class CandidateSpy : IListImportCandidateRepository
    {
        private readonly List<ListImportCandidate> _all = [];

        public void Add(ListImportCandidate candidate) => _all.Add(candidate);

        public Task<IReadOnlyList<ListImportCandidate>> GetCandidatesAsync(IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ListImportCandidate>>(
                _all.Where(candidate => allowedWorkIds is null || allowedWorkIds.Contains(candidate.WorkId)).ToList());
    }

    private sealed class LookupStub : ICollectionMediaLookupReadService
    {
        public Task<List<CollectionMediaLookupDto>> LookupAsync(
            string? query, string? mediaTypes, IReadOnlySet<Guid> existingWorkIds, IReadOnlySet<Guid> allowedWorkIds,
            int? offset, int? limit, CancellationToken ct) => Task.FromResult(new List<CollectionMediaLookupDto>());

        public Task<List<CollectionItemDto>> ResolveItemsAsync(
            Guid collectionId, IReadOnlyList<CollectionItem> items, CancellationToken ct, IReadOnlySet<Guid>? allowedWorkIds = null) =>
            Task.FromResult(new List<CollectionItemDto>());

        public Task<List<CollectionResolvedItemDto>> ResolveMetadataAsync(IReadOnlyList<Guid> workIds, CancellationToken ct) =>
            Task.FromResult(new List<CollectionResolvedItemDto>());

        public Task<Dictionary<string, int>> CountMediaTypesAsync(IReadOnlyList<Guid> workIds, CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, int>());
    }

    private sealed class RecorderStub : IRecurringTaskRecorder
    {
        public Task RecordAsync(string taskKey, Func<CancellationToken, Task<int?>> run, CancellationToken ct) => run(ct);
    }
}
