using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class DisplayVisibleWorkReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly Guid _libraryA = Guid.NewGuid();
    private readonly Guid _libraryB = Guid.NewGuid();
    private readonly Guid _provider = Guid.NewGuid();

    public DisplayVisibleWorkReaderTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_visible_works_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO metadata_providers(id,name,version,is_enabled) VALUES(@provider,'visible-works-test','1',1);",
            new { provider = _provider });
    }

    [Fact]
    public async Task LoadAsync_ReturnsTheSameWorkLibraryKindAndRatingAsTheFullProjection()
    {
        var seeded = Seed();

        var full = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);
        var light = await new DisplayVisibleWorkReader(_db).LoadAsync(CancellationToken.None);

        Assert.Equal(
            full.Select(row => Key(row.WorkId, row.LibraryId, row.MediaType, row.ContentRating)).Order(StringComparer.Ordinal),
            light.Select(row => Key(row.WorkId, row.LibraryId, row.MediaType, row.ContentRating)).Order(StringComparer.Ordinal));

        // The fixture must actually exercise each rule, or equality would prove nothing.
        var lightByWork = light.GroupBy(row => row.WorkId).ToDictionary(group => group.Key, group => group.ToList());
        Assert.Equal(2, lightByWork[seeded.TwoLibraries].Count);
        Assert.Single(lightByWork[seeded.TwoAssets]);
        Assert.Equal("TV-14", lightByWork[seeded.EpisodeUnderRatedShow].Single().ContentRating);
        Assert.Equal("R", lightByWork[seeded.RatingOnlyOnFile].Single().ContentRating);
        Assert.Equal("PG", lightByWork[seeded.RatingOnlyOnEpubFile].Single().ContentRating);
        Assert.Null(lightByWork[seeded.Unrated].Single().ContentRating);
        Assert.False(lightByWork.ContainsKey(seeded.NotReady));
        Assert.False(lightByWork.ContainsKey(seeded.OrphanedFile));
        Assert.False(lightByWork.ContainsKey(seeded.RemovedFile));
        Assert.Equal(_libraryA.ToString("D"), lightByWork[seeded.MixedFiles].Single().LibraryId);
        Assert.False(lightByWork.ContainsKey(seeded.ParentShow));
    }

    [Fact]
    public async Task LoadAsync_PicksTheSameStoredRatingNameAsTheFullProjectionWhenBothNamesExist()
    {
        var seeded = Seed();

        var full = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);
        var light = await new DisplayVisibleWorkReader(_db).LoadAsync(CancellationToken.None);

        var expected = full.Single(row => row.WorkId == seeded.BothRatingNames).ContentRating;
        Assert.Equal(expected, light.Single(row => row.WorkId == seeded.BothRatingNames).ContentRating);
        // The data store returns the alphabetically first stored name, so 'certification' wins over 'content_rating'.
        Assert.Equal("TV-MA", expected);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheRatingOfTheSameFileTheFullProjectionRanksFirst()
    {
        var seeded = Seed();

        var light = await new DisplayVisibleWorkReader(_db).LoadAsync(CancellationToken.None);
        var full = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);

        // An earlier metadata claim ranks the second movie file first, and only that file carries a rating.
        Assert.Equal("NC-17", full.Single(row => row.WorkId == seeded.TwoAssets).ContentRating);
        Assert.Equal("NC-17", light.Single(row => row.WorkId == seeded.TwoAssets).ContentRating);
    }

    public void Dispose()
    {
        _db.Dispose();
        MediaEngine.TestSupport.TestTemp.ReleaseSqlitePool(_dbPath);
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private static string Key(Guid workId, string? libraryId, string mediaType, string? rating) =>
        $"{workId:N}|{libraryId}|{mediaType}|{rating ?? "<none>"}";

    private sealed record Seeded(
        Guid TwoLibraries,
        Guid TwoAssets,
        Guid EpisodeUnderRatedShow,
        Guid RatingOnlyOnFile,
        Guid RatingOnlyOnEpubFile,
        Guid Unrated,
        Guid BothRatingNames,
        Guid NotReady,
        Guid OrphanedFile,
        Guid RemovedFile,
        Guid MixedFiles,
        Guid ParentShow);

    private Seeded Seed()
    {
        using var connection = _db.CreateConnection();

        Guid Work(string mediaType, string kind = "standalone", Guid? parent = null, bool ready = true)
        {
            var id = Guid.NewGuid();
            connection.Execute(
                "INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state) VALUES(@id,@mediaType,@kind,@parent,'accepted');",
                new { id, mediaType, kind, parent });
            if (!ready)
            {
                connection.Execute("UPDATE works SET ready_at = NULL WHERE id = @id;", new { id });
            }

            return id;
        }

        Guid Asset(Guid workId, Guid library, string extension, string? claimedAt = null, string status = "Normal", bool orphaned = false)
        {
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO editions(id,work_id) VALUES(@edition,@workId);
                INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id,status,is_orphaned)
                VALUES(@asset,@edition,@hash,@path,CURRENT_TIMESTAMP,@library,@status,@orphaned);
                """,
                new
                {
                    edition,
                    workId,
                    asset,
                    hash = asset.ToString("N"),
                    path = $"C:/library/{asset:N}.{extension}",
                    library = library.ToString("D"),
                    status,
                    orphaned = orphaned ? 1 : 0,
                });
            if (claimedAt is not null)
            {
                connection.Execute(
                    "INSERT INTO metadata_claims(id,entity_id,provider_id,claim_key,claim_value,confidence,claimed_at) VALUES(@id,@asset,@provider,'title','x',1.0,@claimedAt);",
                    new { id = Guid.NewGuid(), asset, provider = _provider, claimedAt });
            }

            return asset;
        }

        void Value(Guid entityId, string key, string value) =>
            connection.Execute(
                "INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@entityId,@key,@value,CURRENT_TIMESTAMP);",
                new { entityId, key, value });

        // Plain works in two libraries, one work present in both.
        var book = Work("Books");
        Asset(book, _libraryA, "epub");
        var twoLibraries = Work("Movies");
        Asset(twoLibraries, _libraryA, "mkv");
        Asset(twoLibraries, _libraryB, "mkv");
        Value(twoLibraries, "content_rating", "PG-13");
        var unrated = Work("Music");
        Asset(unrated, _libraryB, "flac");

        // A show with a rating only on the show; its episode inherits it through the season.
        var show = Work("TV", "parent");
        Value(show, "content_rating", "TV-14");
        var season = Work("TV", "parent", show);
        var episode = Work("TV", "child", season);
        Asset(episode, _libraryA, "mkv");

        // One work with both stored rating names, which the data store resolves by name order.
        var bothNames = Work("Movies");
        Asset(bothNames, _libraryA, "mkv");
        Value(bothNames, "content_rating", "PG");
        Value(bothNames, "certification", "TV-MA");

        // A rating only on the file.
        var onlyOnFile = Work("Movies");
        var onlyOnFileAsset = Asset(onlyOnFile, _libraryB, "mkv");
        Value(onlyOnFileAsset, "certification", "R");

        // A book whose epub ranks first; the rating sits on the epub, not the other file.
        var epubBook = Work("Books");
        var azw = Asset(epubBook, _libraryA, "azw3");
        var epub = Asset(epubBook, _libraryA, "epub");
        Value(epub, "content_rating", "PG");
        Value(azw, "content_rating", "X");

        // Two movie files in one library: the second has the earlier claim, so it ranks first and its rating is the one read.
        var twoAssets = Work("Movies");
        var firstFile = Asset(twoAssets, _libraryA, "mkv", claimedAt: "2026-02-01 00:00:00");
        var secondFile = Asset(twoAssets, _libraryA, "mkv", claimedAt: "2026-01-01 00:00:00");
        Value(firstFile, "content_rating", "G");
        Value(secondFile, "content_rating", "NC-17");

        // Works that must not appear.
        var notReady = Work("Movies", ready: false);
        Asset(notReady, _libraryA, "mkv");
        var orphaned = Work("Movies");
        Asset(orphaned, _libraryA, "mkv", orphaned: true);
        var removed = Work("Movies");
        Asset(removed, _libraryA, "mkv", status: "Conflicted");

        // A work whose files in another library are orphaned or not Normal is only visible where a good file exists.
        var mixed = Work("Movies");
        Asset(mixed, _libraryA, "mkv");
        Asset(mixed, _libraryB, "mkv", orphaned: true);
        Asset(mixed, _libraryB, "mkv", status: "Conflicted");

        return new Seeded(
            twoLibraries,
            twoAssets,
            episode,
            onlyOnFile,
            epubBook,
            unrated,
            bothNames,
            notReady,
            orphaned,
            removed,
            mixed,
            show);
    }
}
