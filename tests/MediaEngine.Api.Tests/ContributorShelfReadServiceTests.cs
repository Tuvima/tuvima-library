using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class ContributorShelfReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public ContributorShelfReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_contributor_shelves_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task LoadAsync_BuildsOnlyMultiWorkCanonicalContributorShelves()
    {
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO persons (id, name, created_at) VALUES (@id, 'Ursula Test Author', CURRENT_TIMESTAMP);",
                new { id = Guid.NewGuid() });
            await InsertOwnedWorkAsync(conn, "Books", "A Wizard", "author", "Ursula Test Author");
            await InsertOwnedWorkAsync(conn, "Books", "The Tombs", "author", "Ursula Test Author");
            await InsertOwnedWorkAsync(conn, "TV", "One Episode", "director", "Ursula Test Author");
        }

        var shelves = await CreateService().LoadAsync(CancellationToken.None);

        var shelf = Assert.Single(shelves);
        Assert.Equal("BooksByAuthor", shelf.ShelfType);
        Assert.Equal("Books by Ursula Test Author", shelf.Title);
        Assert.Equal(2, shelf.OwnedCount);
        Assert.All(shelf.Items, item => Assert.Equal("Books", item.MediaType));
    }

    [Fact]
    public async Task LoadAsync_CollapsesTracksIntoDistinctAlbums()
    {
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO persons (id, name, created_at) VALUES (@id, 'Album Artist', CURRENT_TIMESTAMP);",
                new { id = Guid.NewGuid() });
            await InsertAlbumAsync(conn, "First Album", 2);
            await InsertAlbumAsync(conn, "Second Album", 3);
        }

        var shelf = Assert.Single(await CreateService().LoadAsync(CancellationToken.None));

        Assert.Equal("AlbumsByArtist", shelf.ShelfType);
        Assert.Equal(2, shelf.OwnedCount);
        Assert.Equal(["First Album", "Second Album"], shelf.Items.Select(item => item.Title).Order().ToArray());
    }

    [Fact]
    public async Task LoadAsync_MergesDuplicateNamedPeopleAndPrefersTheEnrichedIdentity()
    {
        var enrichedPersonId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, wikidata_qid, headshot_url, created_at)
                VALUES (@enrichedPersonId, 'Hans Zimmer', 'Q76364', 'https://images.example/hans.jpg', CURRENT_TIMESTAMP);
                INSERT INTO persons (id, name, wikidata_qid, created_at)
                VALUES (@thinPersonId, 'Hans Zimmer', 'Q999999999', CURRENT_TIMESTAMP);
                """,
                new { enrichedPersonId, thinPersonId = Guid.NewGuid() });
            await InsertAlbumAsync(conn, "Dune", 2, "Hans Zimmer");
            await InsertAlbumAsync(conn, "Interstellar", 2, "Hans Zimmer");
        }

        var shelf = Assert.Single(await CreateService().LoadAsync(CancellationToken.None));

        Assert.Equal(enrichedPersonId, shelf.PersonId);
        Assert.Equal("Albums by Hans Zimmer", shelf.Title);
        Assert.Equal(2, shelf.OwnedCount);
        Assert.NotNull(shelf.HeadshotUrl);
    }

    private ContributorShelfReadService CreateService()
        => new(new TestProjection(new DisplayWorkProjectionReader(_db)), _db);

    private sealed class TestProjection(DisplayWorkProjectionReader works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => works.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => works.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    private static async Task InsertOwnedWorkAsync(
        System.Data.IDbConnection conn,
        string mediaType,
        string title,
        string creditKey,
        string personName)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@workId, @mediaType, 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@assetId, @creditKey, 0, @personName);
            """,
            new { workId, mediaType, editionId, assetId, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{title}.media", title, creditKey, personName });
    }

    private static async Task InsertAlbumAsync(
        System.Data.IDbConnection conn,
        string title,
        int trackCount,
        string artist = "Album Artist")
    {
        var albumId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@albumId, 'Music', 'parent', 'accepted');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@albumId, 'title', @title, CURRENT_TIMESTAMP);
            """,
            new { albumId, title });

        for (var track = 1; track <= trackCount; track++)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal, curator_state)
                VALUES (@workId, @albumId, 'Music', 'child', @track, 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', @trackTitle, CURRENT_TIMESTAMP),
                       (@assetId, 'album', @title, CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@assetId, 'artist', 0, @artist);
                """,
                new
                {
                    workId,
                    albumId,
                    track,
                    editionId,
                    assetId,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"C:/library/{title}/{track:00}.flac",
                    trackTitle = $"Track {track}",
                    title,
                    artist,
                });
        }
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }
}
