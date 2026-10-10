using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class DisplayWorkProjectionReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public DisplayWorkProjectionReaderTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_display_work_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task LoadAsync_PrefersTheEnrichedPersonWhenDuplicateArtistNamesExist()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var localStubId = Guid.NewGuid();
        var enrichedPersonId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, curator_state)
                VALUES (@workId, 'Music', 'accepted');

                INSERT INTO editions (id, work_id, format_label)
                VALUES (@editionId, @workId, 'Audio');

                INSERT INTO media_assets (
                    id,
                    edition_id,
                    content_hash,
                    file_path_root,
                    presented_at)
                VALUES (
                    @assetId,
                    @editionId,
                    @contentHash,
                    'C:\library\music\track.flac',
                    CURRENT_TIMESTAMP);

                INSERT INTO canonical_value_arrays (
                    entity_id,
                    key,
                    ordinal,
                    value,
                    value_qid)
                VALUES (
                    @workId,
                    'artist',
                    0,
                    'Hans Zimmer',
                    'Q-WRONG');

                INSERT INTO canonical_values (
                    entity_id,
                    key,
                    value,
                    last_scored_at)
                VALUES (
                    @workId,
                    'title',
                    'Test Track',
                    CURRENT_TIMESTAMP);

                INSERT INTO persons (
                    id,
                    name,
                    wikidata_qid,
                    occupation,
                    created_at,
                    enriched_at)
                VALUES (
                    @localStubId,
                    'Hans Zimmer',
                    'Q-WRONG',
                    'writer',
                    '2026-01-01T00:00:00Z',
                    NULL);

                INSERT INTO persons (
                    id,
                    name,
                    wikidata_qid,
                    biography,
                    occupation,
                    local_headshot_path,
                    created_at,
                    enriched_at)
                VALUES (
                    @enrichedPersonId,
                    'Hans Zimmer',
                    'Q-CANONICAL',
                    'Canonical composer biography',
                    'composer',
                    'C:\assets\people\hans-zimmer.jpg',
                    '2026-02-01T00:00:00Z',
                    '2026-02-02T00:00:00Z');
                """,
                new
                {
                    workId,
                    editionId,
                    assetId,
                    contentHash = Guid.NewGuid().ToString("N"),
                    localStubId,
                    enrichedPersonId,
                });
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal(enrichedPersonId, row.ArtistPersonId);
        Assert.Equal("Hans Zimmer", row.ArtistPersonName);
    }

    [Fact]
    public async Task LoadAsync_UsesOriginalDatesAcrossMediaTypes()
    {
        var cases = new[]
        {
            new ProjectionDateCase(
                "The Hobbit",
                "Books",
                "1937",
                new Dictionary<string, string>
                {
                    ["date"] = "1937-09-21",
                    ["year"] = "2012",
                }),
            new ProjectionDateCase(
                "The Hobbit",
                "Audiobooks",
                "1937",
                new Dictionary<string, string>
                {
                    ["release_year"] = "2000",
                    ["year"] = "2007",
                }),
            new ProjectionDateCase(
                "Interstellar",
                "Movies",
                "2014",
                new Dictionary<string, string>
                {
                    ["year"] = "2014",
                    ["release_year"] = "2025",
                }),
            new ProjectionDateCase(
                "Akira",
                "Comics",
                "1984",
                new Dictionary<string, string>
                {
                    ["year"] = "1984",
                    ["release_year"] = "1988",
                }),
            new ProjectionDateCase(
                "Hunky Dory",
                "Music",
                "1971",
                new Dictionary<string, string>
                {
                    ["original_release_year"] = "1971",
                    ["year"] = "2019",
                }),
        };

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, created_at)
                VALUES (@personId, 'J. R. R. Tolkien', CURRENT_TIMESTAMP);
                """,
                new { personId = Guid.NewGuid() });

            foreach (var item in cases)
            {
                var workId = Guid.NewGuid();
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO works (id, media_type, work_kind, curator_state)
                    VALUES (@workId, @mediaType, 'standalone', 'accepted');
                    INSERT INTO editions (id, work_id)
                    VALUES (@editionId, @workId);
                    INSERT INTO media_assets (
                        id,
                        edition_id,
                        content_hash,
                        file_path_root,
                        presented_at)
                    VALUES (
                        @assetId,
                        @editionId,
                        @contentHash,
                        @filePath,
                        CURRENT_TIMESTAMP);
                    INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
                    """,
                    new
                    {
                        workId,
                        editionId,
                        assetId,
                        mediaType = item.MediaType,
                        contentHash = Guid.NewGuid().ToString("N"),
                        filePath = $"C:/library/{item.Title}.media",
                        title = item.Title,
                    });

                if (item.Title == "The Hobbit"
                    && item.MediaType is "Books" or "Audiobooks")
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                        VALUES (@assetId, 'author', 0, 'J. R. R. Tolkien');
                        """,
                        new { assetId });
                }

                foreach (var (key, value) in item.Dates)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                        VALUES (@assetId, @key, @value, CURRENT_TIMESTAMP);
                        """,
                        new { assetId, key, value });
                }
            }
        }

        var rows = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);

        Assert.Equal(cases.Length, rows.Count);
        foreach (var expected in cases)
        {
            var row = Assert.Single(
                rows,
                value => value.Title == expected.Title && value.MediaType == expected.MediaType);
            Assert.Equal(expected.ExpectedYear, row.Year);
        }
    }

    [Fact]
    public async Task LoadAsync_MusicUsesTrackWorkYearBeforeLegacyAlbumReissueYear()
    {
        var albumWorkId = Guid.NewGuid();
        var trackWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@albumWorkId, 'Music', 'parent', 'accepted');
                INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state)
                VALUES (@trackWorkId, @albumWorkId, 'Music', 'child', 'accepted');
                INSERT INTO editions (id, work_id)
                VALUES (@editionId, @trackWorkId);
                INSERT INTO media_assets (
                    id,
                    edition_id,
                    content_hash,
                    file_path_root,
                    presented_at)
                VALUES (
                    @assetId,
                    @editionId,
                    @contentHash,
                    'C:/library/music/original-track.flac',
                    CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@albumWorkId, 'title', 'A Night at the Opera', CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@albumWorkId, 'year', '2002', CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', 'Love of My Life', CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'year', '1975', CURRENT_TIMESTAMP);
                """,
                new
                {
                    albumWorkId,
                    trackWorkId,
                    editionId,
                    assetId,
                    contentHash = Guid.NewGuid().ToString("N"),
                });
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal("Love of My Life", row.Title);
        Assert.Equal("1975", row.Year);
    }

    [Fact]
    public async Task LoadAsync_PrefersExplicitCollectivePseudonymAndFormatsRealCoauthors()
    {
        var pseudonymWorkId = Guid.NewGuid();
        var coauthorWorkId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, is_pseudonym, created_at)
                VALUES (@pseudonymId, 'James S. A. Corey', 1, CURRENT_TIMESTAMP),
                       (@memberOneId, 'Daniel Abraham', 0, CURRENT_TIMESTAMP),
                       (@memberTwoId, 'Ty Franck', 0, CURRENT_TIMESTAMP),
                       (@coauthorOneId, 'Author One', 0, CURRENT_TIMESTAMP),
                       (@coauthorTwoId, 'Author Two', 0, CURRENT_TIMESTAMP);
                """,
                new
                {
                    pseudonymId = Guid.NewGuid(),
                    memberOneId = Guid.NewGuid(),
                    memberTwoId = Guid.NewGuid(),
                    coauthorOneId = Guid.NewGuid(),
                    coauthorTwoId = Guid.NewGuid(),
                });

            await InsertBookAsync(conn, pseudonymWorkId, "Leviathan Wakes",
                ["James S. A. Corey", "Daniel Abraham", "Ty Franck"]);
            await InsertBookAsync(conn, coauthorWorkId, "A Shared Book", ["Author One", "Author Two"]);
        }

        var rows = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);

        Assert.Equal("James S. A. Corey", Assert.Single(rows, row => row.WorkId == pseudonymWorkId).Author);
        Assert.Equal("Author One; Author Two", Assert.Single(rows, row => row.WorkId == coauthorWorkId).Author);
    }

    [Fact]
    public async Task LoadAsync_SearchMetadataUsesSelectedEditionAndOwnParentArrays()
    {
        var showWorkId = Guid.NewGuid();
        var episodeWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var alternateEditionId = Guid.NewGuid();
        var alternateAssetId = Guid.NewGuid();
        var stagedEditionId = Guid.NewGuid();
        var stagedAssetId = Guid.NewGuid();
        var otherLibraryEditionId = Guid.NewGuid();
        var otherLibraryAssetId = Guid.NewGuid();
        var libraryId = Guid.NewGuid().ToString("D");
        var otherLibraryId = Guid.NewGuid().ToString("D");

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@showWorkId, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state)
                VALUES (@episodeWorkId, @showWorkId, 'TV', 'child', 'accepted');
                INSERT INTO editions (id, work_id, format_label)
                VALUES (@editionId, @episodeWorkId, 'Digital');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@assetId, @editionId, @hash, 'C:/library/show/episode.mkv', CURRENT_TIMESTAMP);
                UPDATE media_assets SET library_id=@libraryId WHERE id=@assetId;

                INSERT INTO editions (id, work_id, format_label)
                VALUES (@alternateEditionId, @episodeWorkId, 'Alternate'),
                       (@stagedEditionId, @episodeWorkId, 'Staged'),
                       (@otherLibraryEditionId, @episodeWorkId, 'Other library');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id)
                VALUES (@alternateAssetId, @alternateEditionId, @alternateHash, 'C:/library/show/alternate.mkv', @libraryId),
                       (@stagedAssetId, @stagedEditionId, @stagedHash, 'C:/library/.data/staging/staged.mkv', @libraryId),
                       (@otherLibraryAssetId, @otherLibraryEditionId, @otherLibraryHash, 'C:/other-library/show/alternate.mkv', @otherLibraryId);

                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', 'Pilot', CURRENT_TIMESTAMP);

                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES
                    (@editionId, 'publisher', 0, 'Edition House'),
                    (@alternateEditionId, 'publisher', 0, 'Accessible Alternate House'),
                    (@stagedEditionId, 'publisher', 0, 'Staged Hidden House'),
                    (@otherLibraryEditionId, 'publisher', 0, 'Other Library House'),
                    (@showWorkId, 'network', 0, 'Northstar TV'),
                    (@showWorkId, 'country_of_origin', 0, 'Japan'),
                    (@showWorkId, 'franchise', 0, 'Skyward Saga');
                """,
                new
                {
                    showWorkId,
                    episodeWorkId,
                    editionId,
                    assetId,
                    alternateEditionId,
                    alternateAssetId,
                    stagedEditionId,
                    stagedAssetId,
                    otherLibraryEditionId,
                    otherLibraryAssetId,
                    libraryId,
                    otherLibraryId,
                    hash = Guid.NewGuid().ToString("N"),
                    alternateHash = Guid.NewGuid().ToString("N"),
                    stagedHash = Guid.NewGuid().ToString("N"),
                    otherLibraryHash = Guid.NewGuid().ToString("N"),
                });
        }

        var rows = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);
        var row = Assert.Single(rows, item => item.LibraryId == libraryId);

        Assert.Equal(episodeWorkId, row.WorkId);
        Assert.NotEqual(Guid.Empty, row.EditionId);
        Assert.Null(row.Publisher);
        Assert.Contains("Edition House", row.SearchPublisher);
        Assert.Contains("Accessible Alternate House", row.SearchPublisher);
        Assert.DoesNotContain("Staged Hidden House", row.SearchPublisher);
        Assert.DoesNotContain("Other Library House", row.SearchPublisher);
        Assert.Null(row.Network);
        Assert.Equal("Northstar TV", row.SearchNetwork);
        Assert.Equal("Japan", row.CountryOfOrigin);
        Assert.Equal("Skyward Saga", row.Franchise);
    }

    [Fact]
    public async Task LoadAsync_MarksWorkSettlingOnlyWhileItsIdentityJobIsNonTerminal()
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await InsertBookAsync(conn, workId, "Still Matching", ["Some Author"], assetId);
            await conn.ExecuteAsync(
                """
                INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, ingestion_run_id, state, pass, created_at, updated_at)
                VALUES (@jobId, @assetId, 'MediaAsset', 'Books', @runId, 'Queued', 'Quick', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                """,
                new { jobId, assetId, runId = Guid.NewGuid() });
        }

        var reader = new DisplayWorkProjectionReader(_db);
        Assert.True(Assert.Single(await reader.LoadAsync(CancellationToken.None)).IsUpdatingDetails);

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE identity_jobs SET state = 'ReadyWithoutUniverse' WHERE id = @jobId;", new { jobId });
        }

        Assert.False(Assert.Single(await reader.LoadAsync(CancellationToken.None)).IsUpdatingDetails);
    }

    [Fact]
    public void CardBuilder_MapsSettlingFromWorkRowsAndAnyMemberOfAGroup()
    {
        var collectionId = Guid.NewGuid();
        var rowIndex = 0;
        DisplayWorkRow Row(bool updating) => new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            CollectionId = collectionId,
            CollectionTitle = "Saga",
            MediaType = "Movies",
            Title = (rowIndex++ % 2 == 0 ? "Alpha Film" : "Beta Picture"),
            IsUpdatingDetails = updating,
        };
        var builder = new DisplayCardBuilder();

        Assert.True(builder.FromWork(Row(true), "watch", null).IsSettling);
        Assert.False(builder.FromWork(Row(false), "watch", null).IsSettling);
        Assert.True(Assert.Single(builder.BuildCollectionCards([Row(false), Row(true)], "watch")).IsSettling);
        Assert.False(Assert.Single(builder.BuildCollectionCards([Row(false), Row(false)], "watch")).IsSettling);
    }

    private static Task InsertBookAsync(System.Data.IDbConnection conn, Guid workId, string title, IReadOnlyList<string> authors, Guid? existingAssetId = null)
    {
        var editionId = Guid.NewGuid();
        var assetId = existingAssetId ?? Guid.NewGuid();
        return InsertAsync();

        async Task InsertAsync()
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state)
                VALUES (@workId, 'Books', 'standalone', 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
                """,
                new { workId, editionId, assetId, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{title}.epub", title });
            for (var index = 0; index < authors.Count; index++)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@assetId, 'author', @index, @author);",
                    new { assetId, index, author = authors[index] });
            }
        }
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed record ProjectionDateCase(
        string Title,
        string MediaType,
        string ExpectedYear,
        IReadOnlyDictionary<string, string> Dates);
}
