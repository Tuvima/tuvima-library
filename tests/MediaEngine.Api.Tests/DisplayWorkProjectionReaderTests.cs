using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain;
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
        ReadyWorks.TreatEveryWorkAsReady(_db);
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
                INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, pass, created_at, updated_at)
                VALUES (@jobId, @assetId, 'MediaAsset', 'Books', 'Queued', 'Quick', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                """,
                new { jobId, assetId });
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
    public async Task LoadAsync_AudiobookWithoutAYearBorrowsTheMatchingBooksYear()
    {
        var bookWorkId = Guid.NewGuid();
        var audiobookWorkId = Guid.NewGuid();
        var otherAudiobookWorkId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, is_pseudonym, created_at)
                VALUES (@personId, 'Frank Herbert', 0, CURRENT_TIMESTAMP);
                """,
                new { personId = Guid.NewGuid() });
            await InsertBookAsync(conn, bookWorkId, "Dune", ["Frank Herbert"]);
            await conn.ExecuteAsync(
                """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@bookWorkId, 'original_publication_year', '1965', CURRENT_TIMESTAMP);
                """,
                new { bookWorkId });
            await InsertAudiobookAsync(conn, audiobookWorkId, " dune ", "Frank Herbert");
            await InsertAudiobookAsync(conn, otherAudiobookWorkId, "Dune Messiah", "Frank Herbert");
        }

        var rows = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);

        Assert.Equal("1965", Assert.Single(rows, row => row.WorkId == audiobookWorkId).Year);
        Assert.Null(Assert.Single(rows, row => row.WorkId == otherAudiobookWorkId).Year);
        Assert.Equal("1965", Assert.Single(rows, row => row.WorkId == bookWorkId).Year);
    }

    [Fact]
    public async Task LoadAsync_OneAudiobookFileCoveringTwoWorks_EachWorkBorrowsItsOwnBooksYear()
    {
        var duneBookId = Guid.NewGuid();
        var messiahBookId = Guid.NewGuid();
        var firstWorkId = Guid.NewGuid();
        var secondWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO persons (id, name, is_pseudonym, created_at) VALUES (@personId, 'Frank Herbert', 0, CURRENT_TIMESTAMP);",
                new { personId = Guid.NewGuid() });
            await InsertBookAsync(conn, duneBookId, "Dune", ["Frank Herbert"]);
            await InsertBookAsync(conn, messiahBookId, "Dune Messiah", ["Frank Herbert"]);
            await conn.ExecuteAsync(
                """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@duneBookId, 'original_publication_year', '1965', CURRENT_TIMESTAMP),
                       (@messiahBookId, 'original_publication_year', '1969', CURRENT_TIMESTAMP);
                """,
                new { duneBookId, messiahBookId });

            // One combined audiobook file with no title of its own: its edition belongs to the first work and it also
            // covers the second, so each work's own title picks the book whose year it borrows.
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@firstWorkId, 'Audiobooks', 'standalone', 'accepted');
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@secondWorkId, 'Audiobooks', 'standalone', 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @firstWorkId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@assetId, @editionId, @hash, 'C:/library/combined.m4b', CURRENT_TIMESTAMP);
                INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@assetId, @secondWorkId, 2, 'manual');
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@firstWorkId, 'title', 'Dune', CURRENT_TIMESTAMP),
                       (@secondWorkId, 'title', 'Dune Messiah', CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@assetId, 'author', 0, 'Frank Herbert');
                """,
                new { firstWorkId, secondWorkId, editionId, assetId, hash = Guid.NewGuid().ToString("N") });
        }

        var rows = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None);

        Assert.Equal("1965", Assert.Single(rows, row => row.WorkId == firstWorkId).Year);
        Assert.Equal("1969", Assert.Single(rows, row => row.WorkId == secondWorkId).Year);
    }

    [Fact]
    public async Task LoadAsync_PersonDetailReturnsExactlyTheItemsThatCreditThemByNameOrByQid()
    {
        var personId = Guid.NewGuid();
        var byNameWork = Guid.NewGuid();
        var byQidWork = Guid.NewGuid();
        var similarNameWork = Guid.NewGuid();
        var otherPersonWork = Guid.NewGuid();
        var uncreditedWork = Guid.NewGuid();
        var byQidAssetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, wikidata_qid, is_pseudonym, created_at) VALUES (@personId, 'Ada Lovelace', 'Q7259', 0, CURRENT_TIMESTAMP);
                INSERT INTO persons (id, name, wikidata_qid, is_pseudonym, created_at) VALUES (@similarId, 'Ada Lovelaces', NULL, 0, CURRENT_TIMESTAMP);
                INSERT INTO persons (id, name, wikidata_qid, is_pseudonym, created_at) VALUES (@otherId, 'Someone Else', 'Q1', 0, CURRENT_TIMESTAMP);
                """,
                new { personId, similarId = Guid.NewGuid(), otherId = Guid.NewGuid() });
            await InsertBookAsync(conn, byNameWork, "Notes", ["ada LOVELACE"]);
            await InsertBookAsync(conn, byQidWork, "Sketch", ["A. A. Lovelace"], byQidAssetId);
            await conn.ExecuteAsync(
                "UPDATE canonical_value_arrays SET value_qid = 'q7259' WHERE entity_id = @byQidAssetId;",
                new { byQidAssetId });
            await InsertBookAsync(conn, similarNameWork, "Lookalike", ["Ada Lovelaces"]);
            await InsertBookAsync(conn, otherPersonWork, "Elsewhere", ["Someone Else"]);
            await InsertBookAsync(conn, uncreditedWork, "Nobody", ["Nobody Known"]);
        }

        var rows = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None, detailId: personId);

        Assert.Equal(
            new[] { byNameWork, byQidWork }.OrderBy(id => id).ToArray(),
            rows.Select(row => row.WorkId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_PersonDetailForAWorkOrUnknownIdFindsNoCreditedItems()
    {
        var workId = Guid.NewGuid();
        var otherWorkId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO persons (id, name, is_pseudonym, created_at) VALUES (@id, 'Ada Lovelace', 0, CURRENT_TIMESTAMP);",
                new { id = Guid.NewGuid() });
            await InsertBookAsync(conn, workId, "Notes", ["Ada Lovelace"]);
            await InsertBookAsync(conn, otherWorkId, "Other", ["Ada Lovelace"]);
        }

        var reader = new DisplayWorkProjectionReader(_db);

        Assert.Equal(workId, Assert.Single(await reader.LoadAsync(CancellationToken.None, detailId: workId)).WorkId);
        Assert.Empty(await reader.LoadAsync(CancellationToken.None, detailId: Guid.NewGuid()));
    }

    [Fact]
    public async Task LoadAsync_PersonCreditedOnlyOnAShowReturnsItsEpisodes()
    {
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeOneId = Guid.NewGuid();
        var episodeTwoId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO persons (id, name, is_pseudonym, created_at) VALUES (@personId, 'Show Star', 0, CURRENT_TIMESTAMP);
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@showId, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@seasonId, 'TV', 'parent', @showId, 1, 'accepted');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@showId, 'cast_member', 0, 'show star');
                """,
                new { personId, showId, seasonId });
            foreach (var (episodeId, number) in new[] { (episodeOneId, 1), (episodeTwoId, 2) })
            {
                var editionId = Guid.NewGuid();
                var assetId = Guid.NewGuid();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@episodeId, 'TV', 'child', @seasonId, @number, 'accepted');
                    INSERT INTO editions (id, work_id) VALUES (@editionId, @episodeId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                    VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
                    """,
                    new { episodeId, seasonId, number, editionId, assetId, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/show/e{number}.mkv" });
            }

            // An unrelated book is not returned.
            await InsertBookAsync(conn, Guid.NewGuid(), "Unrelated", ["Unrelated Author"]);
        }

        var rows = await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None, detailId: personId);

        Assert.Equal(
            new[] { episodeOneId, episodeTwoId }.OrderBy(id => id).ToArray(),
            rows.Select(row => row.WorkId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task LoadAsync_ArtistIdentityPrefersAHeadshotOverABiographyWhenTwoPersonsShareTheName()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var withBiographyId = Guid.NewGuid();
        var withHeadshotId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO works (id, media_type, curator_state) VALUES (@workId, 'Music', 'accepted');
                INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Audio');
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@assetId, @editionId, @hash, 'C:/library/music/song.flac', CURRENT_TIMESTAMP);
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@workId, 'artist', 0, 'Same Name');
                INSERT INTO persons (id, name, biography, created_at) VALUES (@withBiographyId, 'Same Name', 'Has a biography', '2026-01-01T00:00:00Z');
                INSERT INTO persons (id, name, headshot_url, created_at) VALUES (@withHeadshotId, 'Same Name', 'https://example.invalid/face.jpg', '2026-02-01T00:00:00Z');
                """,
                new { workId, editionId, assetId, hash = Guid.NewGuid().ToString("N"), withBiographyId, withHeadshotId });
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal(withHeadshotId, row.ArtistPersonId);
    }

    [Fact]
    public async Task LoadAsync_ItemsAddedAtTheSameMomentComeBackInWorkIdOrderAndTheLimitCutIsStable()
    {
        var workIds = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        using (var conn = _db.CreateConnection())
        {
            foreach (var workId in workIds)
            {
                var assetId = Guid.NewGuid();
                await InsertBookAsync(conn, workId, $"Same moment {workId:N}", ["Some Author"], assetId);
                await conn.ExecuteAsync(
                    """
                    INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                    VALUES (@id, @assetId, @providerId, 'title', 'x', 0.9, '2026-03-01T12:00:00.000Z');
                    """,
                    new { id = Guid.NewGuid(), assetId, providerId = WellKnownProviders.LocalProcessor });
            }
        }

        var expected = workIds.OrderBy(id => Convert.ToHexString(GuidSql.ToBlob(id)), StringComparer.Ordinal).ToList();
        var reader = new DisplayWorkProjectionReader(_db);

        var all = await reader.LoadAsync(CancellationToken.None);
        var firstCut = await reader.LoadAsync(CancellationToken.None, limit: 3);
        var secondCut = await reader.LoadAsync(CancellationToken.None, limit: 3);

        Assert.Equal(expected, all.Select(row => row.WorkId).ToList());
        Assert.Equal(expected.Take(3).ToList(), firstCut.Select(row => row.WorkId).ToList());
        Assert.Equal(firstCut.Select(row => row.WorkId), secondCut.Select(row => row.WorkId));
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

    [Fact]
    public async Task LoadAsync_TvEpisodeTakesItsCoverFromTheShowWhenItsFileAndWorkHaveNone()
    {
        Guid showId;
        Guid episodeWorkId;
        using (var conn = _db.CreateConnection())
        {
            showId = await InsertWorkAsync(conn, "TV", "parent");
            (episodeWorkId, _, _) = await InsertFileAsync(conn, "TV", "child", showId);
            await SetValueAsync(conn, showId, "poster_url", "/art/show-poster.jpg");
            await SetValueAsync(conn, showId, "cover_state", "present");
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal(episodeWorkId, row.WorkId);
        Assert.Equal(showId, row.RootWorkId);
        Assert.Equal("/art/show-poster.jpg", row.CoverUrl);
        Assert.Equal("/art/show-poster.jpg", row.RootCoverUrl);
    }

    [Fact]
    public async Task LoadAsync_AFieldWithSeveralStoredNamesReturnsTheAlphabeticallyFirstNamePresent()
    {
        Guid assetId;
        using (var conn = _db.CreateConnection())
        {
            (_, _, assetId) = await InsertFileAsync(conn, "TV", "standalone");
            // Written in this order, but the data store has always answered with the alphabetically first name.
            await SetValueAsync(conn, assetId, "poster_url_s", "/small/poster.jpg");
            await SetValueAsync(conn, assetId, "episode_still_url_s", "/small/still.jpg");
            await SetValueAsync(conn, assetId, "still_url_s", "/small/other-still.jpg");
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal("/small/still.jpg", row.CoverSmallUrl);
        Assert.Equal("/small/still.jpg", row.BannerSmallUrl);
    }

    [Fact]
    public async Task LoadAsync_AnEmptyValueOnTheFirstStoredNameStillCountsAsTheValue()
    {
        Guid workId;
        using (var conn = _db.CreateConnection())
        {
            (workId, _, var assetId) = await InsertFileAsync(conn, "TV", "standalone");
            // The file's first name is empty, so the file answers with the empty text and the work's cover is not looked at.
            await SetValueAsync(conn, assetId, "cover_url_s", "");
            await SetValueAsync(conn, assetId, "poster_url_s", "/small/poster.jpg");
            await SetValueAsync(conn, workId, "cover_url_s", "/small/work.jpg");
            // A description is plain text: empty on its first name means nothing, even though another name has text.
            await SetValueAsync(conn, workId, "episode_description", "");
            await SetValueAsync(conn, workId, "short_description", "Hidden behind the empty first name");
            await SetValueAsync(conn, assetId, "short_description", "From the file");
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal("", row.CoverSmallUrl);
        Assert.Equal("From the file", row.Description);
    }

    [Fact]
    public async Task LoadAsync_JoinsAuthorsAndGenresInPositionOrderWithTheirSeparators()
    {
        using (var conn = _db.CreateConnection())
        {
            var (workId, _, _) = await InsertFileAsync(conn, "Books", "standalone");
            // Several stored names for one field are interleaved by position; names that tie keep alphabetical order.
            await SetArrayAsync(conn, workId, "writer", 0, "Writer Zero");
            await SetArrayAsync(conn, workId, "author", 1, "Author One");
            await SetArrayAsync(conn, workId, "author", 0, "Author Zero");
            await SetArrayAsync(conn, workId, "creator", 1, "Creator One");
            await SetArrayAsync(conn, workId, "genre", 1, "Drama");
            await SetArrayAsync(conn, workId, "genre", 0, "Comedy");
            await SetArrayAsync(conn, workId, "genre", 2, "Western");
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal("Author Zero; Writer Zero; Author One; Creator One", row.Author);
        Assert.Equal("Comedy;Drama;Western", row.Genre);
    }

    [Fact]
    public async Task LoadAsync_LeanReadReturnsNullForSearchOnlyColumnsAndFullReadFillsThem()
    {
        using (var conn = _db.CreateConnection())
        {
            var (workId, editionId, assetId) = await InsertFileAsync(conn, "TV", "standalone");
            await SetArrayAsync(conn, editionId, "publisher", 0, "Edition House");
            await SetArrayAsync(conn, workId, "network", 0, "Northstar TV");
            await SetArrayAsync(conn, workId, "country_of_origin", 0, "Japan");
            await SetArrayAsync(conn, workId, "franchise", 0, "Skyward Saga");
            await SetValueAsync(conn, assetId, "title", "Still shown when lean");
        }

        var reader = new DisplayWorkProjectionReader(_db);
        var full = Assert.Single(await reader.LoadAsync(CancellationToken.None));
        var lean = Assert.Single(await reader.LoadAsync(CancellationToken.None, lean: true));

        Assert.Equal("Edition House", full.SearchPublisher);
        Assert.Equal("Northstar TV", full.SearchNetwork);
        Assert.Equal("Japan", full.CountryOfOrigin);
        Assert.Equal("Skyward Saga", full.Franchise);
        Assert.Null(lean.SearchPublisher);
        Assert.Null(lean.SearchNetwork);
        Assert.Null(lean.CountryOfOrigin);
        Assert.Null(lean.Franchise);
        Assert.Equal(full.Title, lean.Title);
    }

    [Fact]
    public async Task LoadAsync_MovieWithOnlyANonNumericYearOnTheWorkFallsBackToTheFilesYear()
    {
        using (var conn = _db.CreateConnection())
        {
            var (workId, _, assetId) = await InsertFileAsync(conn, "Movies", "standalone");
            await SetValueAsync(conn, workId, "year", "circa 1999");
            await SetValueAsync(conn, workId, "release_date", "unknown");
            await SetValueAsync(conn, assetId, "release_date", "+2011-04-02");
        }

        var row = Assert.Single(await new DisplayWorkProjectionReader(_db).LoadAsync(CancellationToken.None));

        Assert.Equal("2011", row.Year);
    }

    private static async Task<Guid> InsertWorkAsync(System.Data.IDbConnection conn, string mediaType, string workKind, Guid? parentId = null)
    {
        var workId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO works (id, media_type, work_kind, parent_work_id, curator_state) VALUES (@workId, @mediaType, @workKind, @parentId, 'accepted');",
            new { workId, mediaType, workKind, parentId });
        return workId;
    }

    private static async Task<(Guid WorkId, Guid EditionId, Guid AssetId)> InsertFileAsync(
        System.Data.IDbConnection conn, string mediaType, string workKind, Guid? parentId = null)
    {
        var workId = await InsertWorkAsync(conn, mediaType, workKind, parentId);
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, 'test-library');
            """,
            new { workId, editionId, assetId, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{workId:N}.mkv" });
        return (workId, editionId, assetId);
    }

    private static Task SetValueAsync(System.Data.IDbConnection conn, Guid entityId, string key, string value) =>
        conn.ExecuteAsync(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, CURRENT_TIMESTAMP);",
            new { entityId, key, value });

    private static Task SetArrayAsync(System.Data.IDbConnection conn, Guid entityId, string key, int ordinal, string value) =>
        conn.ExecuteAsync(
            "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@entityId, @key, @ordinal, @value);",
            new { entityId, key, ordinal, value });

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

    private static async Task InsertAudiobookAsync(System.Data.IDbConnection conn, Guid workId, string title, string author)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state)
            VALUES (@workId, 'Audiobooks', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@assetId, 'author', 0, @author);
            """,
            new { workId, editionId, assetId, hash = Guid.NewGuid().ToString("N"), path = $"C:/library/{workId:N}.m4b", title, author });
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
