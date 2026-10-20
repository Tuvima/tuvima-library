using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// Two libraries shaped like a real one, at 4,000 and 8,000 leaf works, for the library list guardrails. Each size has its
/// own data store so the two measurements never share rows. Every work is Ready, as in a fully processed library.
/// </summary>
public sealed class LibraryListFixture : IDisposable
{
    public const int SmallLeafWorks = 4_000;
    public const int LargeLeafWorks = 8_000;

    private readonly List<string> _paths = [];

    public LibraryListFixture()
    {
        Small = Create(SmallLeafWorks);
        Large = Create(LargeLeafWorks);
    }

    public DatabaseConnection Small { get; }

    public DatabaseConnection Large { get; }

    public void Dispose()
    {
        Small.Dispose();
        Large.Dispose();
        foreach (var path in _paths)
        {
            TestTemp.ReleaseSqlitePool(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private DatabaseConnection Create(int leafWorks)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_library_list_{leafWorks}_{Guid.NewGuid():N}.db");
        _paths.Add(path);
        var database = new DatabaseConnection(path);
        database.InitializeSchema();
        database.RunStartupChecks();
        Seed(database, leafWorks);
        return database;
    }

    // Per 100 leaf works: 30 books, 20 movies, 20 TV episodes of one show (show -> season -> episode), 10 tracks of one
    // album, 10 audiobooks and 10 comics. Credits sit where the product puts them: on the book, movie, show or album, with
    // half the movies' directors only on the file so the list's "director from the file" fallback is exercised.
    // Every book has an original year and every credited name (Person 0 to Person 799) has a persons row, as the credits view
    // needs. Half the audiobooks carry their own original year, as an identified audiobook normally does. Of the other half, a
    // third borrow the year of a book with the same title and the same author, a third have a book's title but another author
    // (so they borrow nothing), and a third match no book; some borrowers spell the title in capitals with spaces around it.
    // So the list, Home and detail reads exercise the audiobook year look-up, which must grow in proportion to the library.
    private static void Seed(DatabaseConnection database, int leafWorks)
    {
        using var connection = database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            """
            CREATE TEMP TABLE seed_leaf (
                seq INTEGER PRIMARY KEY,
                work_id BLOB NOT NULL,
                edition_id BLOB NOT NULL,
                asset_id BLOB NOT NULL,
                media_type TEXT NOT NULL,
                group_no INTEGER NOT NULL,
                twin_seq INTEGER,
                twin_same_author INTEGER NOT NULL);

            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < @leafWorks)
            INSERT INTO seed_leaf
            SELECT value, randomblob(16), randomblob(16), randomblob(16),
                   CASE
                       WHEN value % 10 < 3 THEN 'Books'
                       WHEN value % 10 < 5 THEN 'Movies'
                       WHEN value % 10 < 7 THEN 'TV'
                       WHEN value % 10 = 7 THEN 'Music'
                       WHEN value % 10 = 8 THEN 'Audiobooks'
                       ELSE 'Comics'
                   END,
                   value / 100,
                   -- Audiobook 20k+18 without a year of its own takes the title of book 20k+1.
                   CASE WHEN value % 20 = 18 AND (value / 20) % 3 < 2 THEN value - 17 END,
                   CASE WHEN value % 20 = 18 AND (value / 20) % 3 = 0 THEN 1 ELSE 0 END
            FROM sequence;

            INSERT INTO persons (id, name, is_pseudonym, created_at)
            SELECT randomblob(16), 'Person ' || value, 0, @readyAt
            FROM (
                WITH RECURSIVE names(value) AS (
                    SELECT 0
                    UNION ALL
                    SELECT value + 1 FROM names WHERE value < 799)
                SELECT value FROM names);

            CREATE TEMP TABLE seed_group (
                group_no INTEGER PRIMARY KEY,
                show_id BLOB NOT NULL,
                season_id BLOB NOT NULL,
                album_id BLOB NOT NULL);

            INSERT INTO seed_group
            SELECT group_no, randomblob(16), randomblob(16), randomblob(16)
            FROM (SELECT DISTINCT group_no FROM seed_leaf);

            INSERT INTO works (id, media_type, work_kind, curator_state, wikidata_status, ready_at)
            SELECT show_id, 'TV', 'parent', 'accepted', 'confirmed', @readyAt FROM seed_group;

            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state, wikidata_status, ready_at)
            SELECT season_id, 'TV', 'parent', show_id, 1, 'accepted', 'confirmed', @readyAt FROM seed_group;

            INSERT INTO works (id, media_type, work_kind, curator_state, wikidata_status, ready_at)
            SELECT album_id, 'Music', 'parent', 'accepted', 'confirmed', @readyAt FROM seed_group;

            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state, wikidata_status, ready_at)
            SELECT l.work_id,
                   l.media_type,
                   CASE WHEN l.media_type IN ('TV', 'Music') THEN 'child' ELSE 'standalone' END,
                   CASE l.media_type WHEN 'TV' THEN g.season_id WHEN 'Music' THEN g.album_id END,
                   CASE WHEN l.media_type IN ('TV', 'Music') THEN l.seq % 100 END,
                   'accepted',
                   'confirmed',
                   @readyAt
            FROM seed_leaf l
            INNER JOIN seed_group g ON g.group_no = l.group_no;

            INSERT INTO editions (id, work_id, format_label)
            SELECT edition_id, work_id, 'Performance fixture' FROM seed_leaf;

            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            SELECT asset_id, edition_id, lower(hex(asset_id)), 'C:/library/' || seq || '.media', @readyAt
            FROM seed_leaf;

            -- Display values on the file and on the work the list reads them from.
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            SELECT asset_id, 'title',
                   CASE
                       WHEN twin_seq IS NULL THEN media_type || ' item ' || seq
                       WHEN (seq / 40) % 2 = 0 THEN '  ' || UPPER('Books item ' || twin_seq) || ' '
                       ELSE 'Books item ' || twin_seq
                   END,
                   @readyAt
            FROM seed_leaf
            UNION ALL
            SELECT asset_id, 'file_name', seq || '.media', @readyAt FROM seed_leaf
            UNION ALL
            SELECT work_id, 'title',
                   CASE WHEN twin_seq IS NULL THEN media_type || ' work ' || seq ELSE 'Books work ' || twin_seq END,
                   @readyAt
            FROM seed_leaf
            UNION ALL
            SELECT work_id, 'cover_url', 'https://covers.invalid/' || seq || '.jpg', @readyAt FROM seed_leaf
            UNION ALL
            SELECT asset_id, 'original_publication_year', CAST(1950 + (seq * 31 % 75) AS TEXT), @readyAt
            FROM seed_leaf WHERE media_type = 'Audiobooks' AND seq % 20 = 8
            UNION ALL
            SELECT work_id, 'original_publication_year', CAST(1900 + (seq * 13 % 100) AS TEXT), @readyAt
            FROM seed_leaf WHERE media_type = 'Books'
            UNION ALL
            SELECT show_id, 'show_name', 'Show ' || group_no, @readyAt FROM seed_group
            UNION ALL
            SELECT album_id, 'album', 'Album ' || group_no, @readyAt FROM seed_group;

            -- Credits on the book, audiobook, comic and movie works.
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            SELECT work_id, 'author', 0,
                   'Person ' || (CASE WHEN twin_same_author = 1 THEN twin_seq ELSE seq END * 7 % 800)
            FROM seed_leaf WHERE media_type IN ('Books', 'Audiobooks', 'Comics')
            UNION ALL
            SELECT work_id, 'author', 1, 'Person ' || (seq * 11 % 800) FROM seed_leaf WHERE media_type = 'Books' AND seq % 3 = 0
            UNION ALL
            SELECT work_id, 'narrator', 0, 'Person ' || (seq * 13 % 800) FROM seed_leaf WHERE media_type = 'Audiobooks'
            UNION ALL
            SELECT work_id, 'genre', 0, 'Genre ' || (seq % 12) FROM seed_leaf WHERE media_type IN ('Books', 'Audiobooks', 'Movies')
            UNION ALL
            SELECT work_id, 'genre', 1, 'Genre ' || ((seq + 5) % 12) FROM seed_leaf WHERE media_type IN ('Books', 'Movies')
            UNION ALL
            SELECT work_id, 'director', 0, 'Person ' || (seq * 17 % 800) FROM seed_leaf WHERE media_type = 'Movies' AND seq % 20 < 10
            UNION ALL
            SELECT asset_id, 'director', 0, 'Person ' || (seq * 19 % 800) FROM seed_leaf WHERE media_type = 'Movies' AND seq % 20 >= 10
            UNION ALL
            SELECT l.work_id, 'cast_member', c.ordinal, 'Person ' || ((l.seq * 23 + c.ordinal) % 800)
            FROM seed_leaf l
            CROSS JOIN (SELECT 0 AS ordinal UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3) c
            WHERE l.media_type = 'Movies';

            -- Credits on the shows and albums, which their episodes and tracks show.
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            SELECT g.show_id, 'cast_member', c.ordinal, 'Person ' || ((g.group_no * 29 + c.ordinal) % 800)
            FROM seed_group g
            CROSS JOIN (SELECT 0 AS ordinal UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4) c
            UNION ALL
            SELECT show_id, 'director', 0, 'Person ' || (group_no * 31 % 800) FROM seed_group
            UNION ALL
            SELECT show_id, 'genre', 0, 'Genre ' || (group_no % 12) FROM seed_group
            UNION ALL
            SELECT album_id, 'album_artist', 0, 'Person ' || (group_no * 37 % 800) FROM seed_group
            UNION ALL
            SELECT album_id, 'artist', 0, 'Person ' || (group_no * 41 % 800) FROM seed_group
            UNION ALL
            SELECT album_id, 'genre', 0, 'Genre ' || ((group_no + 3) % 12) FROM seed_group;

            -- One claim per file gives the list its first-claimed date, which it sorts by.
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            SELECT randomblob(16), asset_id, @localProcessor, 'title', media_type || ' item ' || seq, 0.9,
                   strftime('%Y-%m-%dT%H:%M:%fZ', '2026-01-01', '+' || seq || ' minutes')
            FROM seed_leaf;

            DROP TABLE seed_leaf;
            DROP TABLE seed_group;
            """,
            new { leafWorks, readyAt = "2026-01-01T00:00:00Z", localProcessor = WellKnownProviders.LocalProcessor },
            transaction,
            commandTimeout: 120);
        transaction.Commit();
        connection.Execute("ANALYZE;", commandTimeout: 120);
    }
}
