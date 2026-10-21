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

    // The items the Home and detail guardrails open. Fixed ids, so the same item exists at both sizes.

    /// <summary>An audiobook (the 18th work) without a year of its own that borrows the year of the book with its title and author.</summary>
    public static readonly Guid AudiobookWorkId = new("a0d10b00-0000-4000-8000-000000000018");

    /// <summary>A TV episode (the 25th work) of the first show.</summary>
    public static readonly Guid TvEpisodeWorkId = new("a0d10b00-0000-4000-8000-000000000025");

    /// <summary>
    /// A person credited as an author on 24 books (half of those credits carry the Wikidata id) and as the artist of the
    /// first album's 10 tracks, who shares a name with a second person that has a headshot.
    /// </summary>
    public static readonly Guid FeaturedPersonId = new("a0d10b00-0000-4000-8000-000000000042");

    // Play history and narrators for the guardrails of spec "Audiobook year on the remaining screens" (D7). Each read is measured
    // twice, once with audiobooks that need a borrowed year and once with the same number of audiobooks that have their own,
    // so the difference is the year's own cost. One audiobook per 100 leaf works per profile or narrator (40 at 4,000 works),
    // and, for the listeners, one book per 200 leaf works (20 at 4,000), so a listener has 60 plays at 4,000 works.

    /// <summary>Plays audiobooks without a year of their own (<c>seq % 20 = 18</c>) and books, all at 40%.</summary>
    public static readonly Guid YearBorrowingListenerId = new("a0d10b00-0000-4000-8000-0000000000a1");

    /// <summary>Plays the same number of audiobooks that have their own year (<c>seq % 20 = 8</c>) and the same books, all at 40%.</summary>
    public static readonly Guid OwnYearListenerId = new("a0d10b00-0000-4000-8000-0000000000a2");

    /// <summary>Credited as the second narrator of audiobooks without a year of their own, one per 100 leaf works.</summary>
    public static readonly Guid YearBorrowingNarratorId = new("a0d10b00-0000-4000-8000-0000000000b1");

    /// <summary>Credited as the second narrator of the same number of audiobooks that have their own year.</summary>
    public static readonly Guid OwnYearNarratorId = new("a0d10b00-0000-4000-8000-0000000000b2");

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
    // needs; one in ten of those persons has a Wikidata id, which their credits then carry, and one in seven a headshot. Half the audiobooks carry their own original year, as an identified audiobook normally does. Of the other half, a
    // third borrow the year of a book with the same title and the same author, a third have a book's title but another author
    // (so they borrow nothing), and a third match no book; some borrowers spell the title in capitals with spaces around it.
    // So the list, Home and detail reads exercise the audiobook year look-up, which must grow in proportion to the library.
    // Two listeners and two narrators (constants above) let a guardrail separate the borrowed year's own cost from the rest of a read.
    // Odd tens of books and audiobooks (10 to 19, 30 to 39, ...) belong to one series each, so a book's detail returns its
    // series mates. Two persons are called "Featured Author" (one with a headshot, one with a Wikidata id), which is credited
    // on 24 books and on the first album, so the person page, the credit look-ups and the album artist all meet a shared name.
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
                twin_same_author INTEGER NOT NULL,
                collection_no INTEGER);

            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM sequence WHERE value < @leafWorks)
            INSERT INTO seed_leaf
            SELECT value,
                   CASE value WHEN 18 THEN @audiobookId WHEN 25 THEN @episodeId ELSE randomblob(16) END,
                   randomblob(16), randomblob(16),
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
                   CASE WHEN value % 20 = 18 AND (value / 20) % 3 = 0 THEN 1 ELSE 0 END,
                   CASE WHEN value % 10 IN (0, 1, 2, 8) AND (value / 10) % 2 = 1 THEN value / 10 END
            FROM sequence;

            INSERT INTO persons (id, name, wikidata_qid, headshot_url, is_pseudonym, created_at)
            SELECT randomblob(16), 'Person ' || value,
                   CASE WHEN value % 10 = 0 THEN 'Q' || (100000 + value) END,
                   CASE WHEN value % 7 = 0 THEN 'https://headshots.invalid/' || value || '.jpg' END,
                   0, @readyAt
            FROM (
                WITH RECURSIVE names(value) AS (
                    SELECT 0
                    UNION ALL
                    SELECT value + 1 FROM names WHERE value < 799)
                SELECT value FROM names);

            -- Two persons share one name: the first has a Wikidata id, the second a headshot.
            INSERT INTO persons (id, name, wikidata_qid, headshot_url, is_pseudonym, created_at)
            VALUES (@featuredPersonId, 'Featured Author', 'Q424242', NULL, 0, @readyAt),
                   (randomblob(16), 'Featured Author', NULL, 'https://headshots.invalid/featured.jpg', 0, @readyAt);

            CREATE TEMP TABLE seed_series (
                collection_no INTEGER PRIMARY KEY,
                collection_id BLOB NOT NULL);

            INSERT INTO seed_series
            SELECT collection_no, randomblob(16) FROM seed_leaf WHERE collection_no IS NOT NULL GROUP BY collection_no;

            INSERT INTO collections (id, display_name, collection_type)
            SELECT collection_id, 'Series ' || collection_no, 'ContentGroup' FROM seed_series;

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

            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, collection_id, curator_state, wikidata_status, ready_at)
            SELECT l.work_id,
                   l.media_type,
                   CASE WHEN l.media_type IN ('TV', 'Music') THEN 'child' ELSE 'standalone' END,
                   CASE l.media_type WHEN 'TV' THEN g.season_id WHEN 'Music' THEN g.album_id END,
                   CASE WHEN l.media_type IN ('TV', 'Music') THEN l.seq % 100 END,
                   s.collection_id,
                   'accepted',
                   'confirmed',
                   @readyAt
            FROM seed_leaf l
            INNER JOIN seed_group g ON g.group_no = l.group_no
            LEFT JOIN seed_series s ON s.collection_no = l.collection_no;

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

            -- The shared name is credited on 24 books, half of them naming the person with the Wikidata id.
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
            SELECT work_id, 'author', 2, 'Featured Author', CASE WHEN seq % 20 = 1 THEN 'Q424242' END
            FROM seed_leaf WHERE media_type = 'Books' AND seq % 10 = 1 AND seq <= 231;

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
            SELECT album_id, 'artist', 0, CASE WHEN group_no = 0 THEN 'Featured Author' ELSE 'Person ' || (group_no * 41 % 800) END FROM seed_group
            UNION ALL
            SELECT album_id, 'genre', 0, 'Genre ' || ((group_no + 3) % 12) FROM seed_group;

            -- Credits for the persons that have a Wikidata id carry it, as an enriched credit does.
            UPDATE canonical_value_arrays
            SET value_qid = (SELECT person.wikidata_qid FROM persons person WHERE person.name = canonical_value_arrays.value AND person.wikidata_qid IS NOT NULL)
            WHERE key IN ('author', 'narrator', 'director', 'cast_member') AND value_qid IS NULL AND value LIKE 'Person %';

            -- One claim per file gives the list its first-claimed date, which it sorts by.
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            SELECT randomblob(16), asset_id, @localProcessor, 'title', media_type || ' item ' || seq, 0.9,
                   strftime('%Y-%m-%dT%H:%M:%fZ', '2026-01-01', '+' || seq || ' minutes')
            FROM seed_leaf;

            -- Two profiles' play history: one play per 100 leaf works on audiobooks (every fifth audiobook of its kind, skipping the first, which is the Home and detail guardrails' audiobook) and one
            -- per 200 on books, at 40%, with a distinct last_accessed. The two profiles play the same books.
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed)
            SELECT @yearBorrowingListenerId,
                   asset_id, 40.0, strftime('%Y-%m-%dT%H:%M:%fZ', '2026-06-01', '+' || seq || ' minutes')
            FROM seed_leaf
            WHERE (media_type = 'Audiobooks' AND seq % 20 = 18 AND (seq / 20) % 5 = 1) OR (media_type = 'Books' AND seq % 200 = 1)
            UNION ALL
            SELECT @ownYearListenerId,
                   asset_id, 40.0, strftime('%Y-%m-%dT%H:%M:%fZ', '2026-06-01', '+' || seq || ' minutes')
            FROM seed_leaf
            WHERE (media_type = 'Audiobooks' AND seq % 20 = 8 AND (seq / 20) % 5 = 1) OR (media_type = 'Books' AND seq % 200 = 1);

            -- Two narrators, each credited (second billing, so the author matching the borrowed year uses is untouched) on one
            -- audiobook per 100 leaf works: the first on audiobooks without a year of their own, the second on ones with a year.
            INSERT INTO persons (id, name, is_pseudonym, created_at)
            VALUES (@yearBorrowingNarratorId, 'Year Borrowing Narrator', 0, @readyAt),
                   (@ownYearNarratorId, 'Own Year Narrator', 0, @readyAt);

            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            SELECT work_id, 'narrator', 1, 'Year Borrowing Narrator'
            FROM seed_leaf WHERE media_type = 'Audiobooks' AND seq % 20 = 18 AND (seq / 20) % 5 = 1
            UNION ALL
            SELECT work_id, 'narrator', 1, 'Own Year Narrator'
            FROM seed_leaf WHERE media_type = 'Audiobooks' AND seq % 20 = 8 AND (seq / 20) % 5 = 1;

            DROP TABLE seed_leaf;
            DROP TABLE seed_group;
            DROP TABLE seed_series;
            """,
            new
            {
                leafWorks,
                readyAt = "2026-01-01T00:00:00Z",
                localProcessor = WellKnownProviders.LocalProcessor,
                audiobookId = GuidSql.ToBlob(AudiobookWorkId),
                episodeId = GuidSql.ToBlob(TvEpisodeWorkId),
                featuredPersonId = GuidSql.ToBlob(FeaturedPersonId),
                yearBorrowingListenerId = GuidSql.ToBlob(YearBorrowingListenerId),
                ownYearListenerId = GuidSql.ToBlob(OwnYearListenerId),
                yearBorrowingNarratorId = GuidSql.ToBlob(YearBorrowingNarratorId),
                ownYearNarratorId = GuidSql.ToBlob(OwnYearNarratorId),
            },
            transaction,
            commandTimeout: 120);
        transaction.Commit();
        connection.Execute("ANALYZE;", commandTimeout: 120);
    }
}
