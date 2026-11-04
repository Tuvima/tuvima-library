using Dapper;
using MediaEngine.Contracts.Editing;
using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

/// <summary>The totals behind the universal editor's Quick Facts (v4 spec D4, section 5): owned of total, file count, size, runtime, quality.</summary>
public sealed class ItemQuickFactsRepositoryTests : IDisposable
{
    private const string Hdr4k = """{"Width":3840,"Height":2160,"ColorTransfer":"smpte2084","VideoCodec":"hevc"}""";
    private const string Hd = """{"Width":1920,"Height":1080,"ColorTransfer":"bt709"}""";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_quick_facts_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public ItemQuickFactsRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A temp file another process still holds is cleaned up by the system temp sweep.
        }
    }

    private Guid Work(string mediaType, string kind, Guid? parent = null, bool owned = true, string? collection = null, Guid? collectionId = null)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, work_kind, parent_work_id, ownership, is_catalog_only, collection_id) VALUES (@id, @mediaType, @kind, @parent, @ownership, @catalog, @collectionId);",
            new { id, mediaType, kind, parent, ownership = owned ? "Owned" : "Unowned", catalog = owned ? 0 : 1, collectionId });
        return id;
    }

    private Guid Asset(Guid workId, string path, long? size, double? seconds, string? probeJson)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = Guid.NewGuid().ToString("N");
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @hash, @path);",
            new { assetId, editionId, hash, path });
        connection.Execute(
            "INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, file_size, duration_secs, metadata_json) VALUES (@assetId, @hash, datetime('now'), @size, @seconds, @probeJson);",
            new { assetId, hash, size, seconds, probeJson });
        return assetId;
    }

    private Guid Collection(string type, string membership = "Manual", string ownerKind = "Library", Guid? profileId = null, Guid? universeId = null)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO collections (id, collection_type, membership_mode, owner_kind, profile_id, universe_id) VALUES (@id, @type, @membership, @ownerKind, @profileId, @universeId);",
            new { id, type, membership, ownerKind, profileId, universeId });
        return id;
    }

    private Task<ItemQuickFactsRead?> ReadAsync(Guid id) => new ItemQuickFactsRepository(_database).GetAsync(id, CancellationToken.None);

    [Fact]
    public async Task AShow_CountsSeasonsAndEpisodesOwnedOfTotal_AndSumsFilesSizeRuntimeAndQuality()
    {
        var show = Work("TV", "parent");
        var season1 = Work("TV", "parent", show);
        var season2 = Work("TV", "parent", show);
        var s1e1 = Work("TV", "child", season1);
        var s1e2 = Work("TV", "child", season1);
        var s2e1 = Work("TV", "child", season2, owned: false);
        Asset(s1e1, "/tv/Show/S01E01.mkv", 1_000, 2400, Hdr4k);
        Asset(s1e2, "/tv/Show/S01E02.mkv", 2_000, 2500, Hd);

        var read = await ReadAsync(show);

        Assert.NotNull(read);
        var facts = read.Facts;
        Assert.Equal(ItemQuickFactsKinds.Work, facts.Kind);
        Assert.Equal(2, facts.DirectOwned);
        Assert.Equal(2, facts.DirectTotal);
        Assert.Equal(2, facts.LeafOwned);
        Assert.Equal(3, facts.LeafTotal);
        Assert.Equal(2, facts.FileCount);
        Assert.Equal(3_000, facts.SizeBytes);
        Assert.Equal(4900, facts.RuntimeSeconds);
        Assert.Equal("4K HDR", facts.BestQuality);
        Assert.Equal(2, read.FilePaths.Count);
        Assert.NotEqual(Guid.Empty, s2e1);
    }

    [Fact]
    public async Task ASeason_CountsItsOwnEpisodesOnly()
    {
        var show = Work("TV", "parent");
        var season1 = Work("TV", "parent", show);
        var season2 = Work("TV", "parent", show);
        var a = Work("TV", "child", season1);
        Work("TV", "child", season1, owned: false);
        var b = Work("TV", "child", season2);
        Asset(a, "/tv/a.mkv", 10, 60, null);
        Asset(b, "/tv/b.mkv", 99, 60, null);

        var facts = (await ReadAsync(season1))!.Facts;

        Assert.Equal(1, facts.DirectOwned);
        Assert.Equal(2, facts.DirectTotal);
        Assert.Equal(1, facts.FileCount);
        Assert.Equal(10, facts.SizeBytes);
        Assert.Null(facts.BestQuality);
    }

    [Fact]
    public async Task AFilm_FoundByItsFileId_ReportsItsOwnFileFacts()
    {
        var film = Work("Movies", "standalone");
        var assetId = Asset(film, "/movies/Dune.mkv", 62_000_000_000, 9300, Hdr4k);

        var facts = (await ReadAsync(assetId))!.Facts;

        Assert.Equal(1, facts.FileCount);
        Assert.Equal(62_000_000_000, facts.SizeBytes);
        Assert.Equal(9300, facts.RuntimeSeconds);
        Assert.Equal("4K HDR", facts.BestQuality);
        Assert.Equal(0, facts.DirectTotal);
        Assert.Equal(0, facts.LeafTotal);
    }

    [Fact]
    public async Task AnItemWithNoStoredSizeOrLength_LeavesThemOut()
    {
        var film = Work("Movies", "standalone");
        Asset(film, "/movies/x.mkv", null, null, null);

        var facts = (await ReadAsync(film))!.Facts;

        Assert.Equal(1, facts.FileCount);
        Assert.Null(facts.SizeBytes);
        Assert.Null(facts.RuntimeSeconds);
    }

    [Fact]
    public async Task ASong_ReportsALosslessCodecAsItsQuality()
    {
        var song = Work("Music", "child");
        Asset(song, "/music/a.flac", 30_000_000, 240, """{"AudioCodec":"flac","AudioBitrate":900}""");

        Assert.Equal("FLAC", (await ReadAsync(song))!.Facts.BestQuality);
    }

    [Fact]
    public async Task APerson_ReportsTheirRolesAndTheWorksThatCreditThem()
    {
        var personId = Guid.NewGuid();
        var book1 = Work("Books", "standalone");
        var book2 = Work("Books", "standalone");
        var asset1 = Asset(book1, "/books/1.epub", 1, null, null);
        var asset2 = Asset(book2, "/books/2.epub", 1, null, null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO persons (id, name, created_at) VALUES (@personId, 'Frank Herbert', datetime('now'));", new { personId });
            connection.Execute("INSERT INTO person_roles (person_id, role) VALUES (@personId, 'Author'), (@personId, 'Screenwriter');", new { personId });
            connection.Execute(
                "INSERT INTO person_media_links (media_asset_id, person_id, role) VALUES (@asset1, @personId, 'Author'), (@asset2, @personId, 'Author');",
                new { asset1, asset2, personId });
        }

        var facts = (await ReadAsync(personId))!.Facts;

        Assert.Equal(ItemQuickFactsKinds.Person, facts.Kind);
        Assert.Equal(["Author", "Screenwriter"], facts.Roles);
        Assert.Equal(2, facts.ItemCount);
    }

    [Fact]
    public async Task ACuratedCollection_ReportsItsItemsAndOwner()
    {
        var profileId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO profiles (id, display_name, role, created_at) VALUES (@profileId, 'Shy', 'Administrator', datetime('now'));", new { profileId });
        }

        var mine = Collection("Playlist", ownerKind: "Profile", profileId: profileId);
        var shared = Collection("Playlist");
        var workA = Work("Movies", "standalone");
        var workB = Work("Movies", "standalone");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id) VALUES (@a, @mine, @workA), (@b, @mine, @workB);",
                new { a = Guid.NewGuid(), b = Guid.NewGuid(), mine, workA, workB });
        }

        var ours = (await ReadAsync(mine))!.Facts;
        var everyones = (await ReadAsync(shared))!.Facts;

        Assert.Equal(ItemQuickFactsKinds.Collection, ours.Kind);
        Assert.Equal(2, ours.ItemCount);
        Assert.Equal("Shy", ours.Owner);
        Assert.Equal("Library", everyones.Owner);
        Assert.Equal(0, everyones.ItemCount);
    }

    [Fact]
    public async Task ASeriesRecord_CountsItsOwnedTitles()
    {
        var series = Collection("Series", "Smart");
        Work("Books", "standalone", collectionId: series);
        Work("Books", "standalone", collectionId: series);
        Work("Books", "standalone", owned: false, collectionId: series);

        var facts = (await ReadAsync(series))!.Facts;

        Assert.Equal(2, facts.ItemCount);
        Assert.Equal("Books", Assert.Single(facts.CountsByKind).Label);
    }

    [Fact]
    public async Task AUniverse_CountsItsTitlesAcrossItsSeriesAndKinds()
    {
        var universe = Collection("Universe", "Smart");
        var series = Collection("Series", "Smart", universeId: universe);
        Work("Books", "standalone", collectionId: series);
        Work("Books", "standalone", collectionId: series);
        Work("Movies", "standalone", collectionId: universe);
        var tvShow = Work("TV", "parent", collectionId: universe);
        Work("TV", "child", tvShow, collectionId: universe);

        var facts = (await ReadAsync(universe))!.Facts;

        Assert.Equal(4, facts.ItemCount);
        Assert.Equal(["Books", "Films", "Shows"], facts.CountsByKind.Select(count => count.Label).ToArray());
        Assert.Equal(2, facts.CountsByKind[0].Count);
    }

    [Fact]
    public async Task AnUnknownId_HasNoFacts()
    {
        Assert.Null(await ReadAsync(Guid.NewGuid()));
    }
}
