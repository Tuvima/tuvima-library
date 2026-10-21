using System.Diagnostics;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Benchmarks.Seeding;

/// <summary>
/// Builds the synthetic library. The file is created through the production path (<c>InitializeSchema</c> then
/// <c>RunStartupChecks</c>) and filled with set-based <c>INSERT ... SELECT</c> statements from recursive generators,
/// one transaction per table group, the same technique as <c>tests/MediaEngine.Performance.Tests/LargeLibraryFixture.cs</c>.
/// </summary>
internal static class DataSeeder
{
    public const int FullWorks = 100_000;
    public const int SmallWorks = 2_000;

    /// <summary>
    /// Size used for the two projection reads (Q4, Q5) and the concurrency run. Their cost grows faster than the library
    /// (README, "Sizing"), so at <see cref="FullWorks"/> one call takes minutes to hours.
    /// </summary>
    public const int ProjectionWorks = 4_000;

    private const int ClaimsPerWork = 5;

    /// <param name="worksOverride">
    /// Optional exact leaf-work count for size-estimation runs (labelled scale "custom"). The full and small scales
    /// are the supported ones; every other count keeps the same ratios.
    /// </param>
    public static DatasetInfo Seed(string databasePath, string scale, int? worksOverride = null)
    {
        var works = scale switch
        {
            "full" => FullWorks,
            "small" => SmallWorks,
            "projection" => ProjectionWorks,
            _ => throw new ArgumentException($"Unknown scale '{scale}'. Use 'full', 'projection' or 'small'."),
        };
        if (worksOverride is { } custom)
        {
            if (custom < 200)
            {
                throw new ArgumentException("--works must be at least 200 so every media type and parent group exists.");
            }

            works = custom;
            scale = "custom";
        }

        databasePath = Path.GetFullPath(databasePath);
        if (File.Exists(databasePath))
        {
            throw new InvalidOperationException($"'{databasePath}' already exists. Run 'clean --db' first so the dataset is built from scratch.");
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var persons = works / 5;
        var collections = Math.Max(10, works / 200);
        var artwork = works / 2;
        var timer = Stopwatch.StartNew();

        using var database = new DatabaseConnection(databasePath);
        database.InitializeSchema();
        database.RunStartupChecks();
        Console.WriteLine($"[{timer.Elapsed:mm\\:ss}] schema created through the production path");

        using (var connection = database.CreateConnection())
        {
            // Seeder-only speed-up: a larger page cache while bulk loading. Benchmark connections do not use it.
            connection.Execute("PRAGMA cache_size = -262144;");

            RunStep(connection, timer, "providers, collections, persons", transaction =>
            {
                InsertProviders(connection, transaction);
                connection.Execute(CollectionsSql, new { collections }, transaction, commandTimeout: 0);
                connection.Execute(PersonsSql, new { persons }, transaction, commandTimeout: 0);
            });

            RunStep(connection, timer, "works, editions, media assets", transaction =>
            {
                connection.Execute(LeafTableSql, new { n = works }, transaction, commandTimeout: 0);
                connection.Execute(WorksSql, new { collections }, transaction, commandTimeout: 0);
                connection.Execute(EditionsAndAssetsSql, null, transaction, commandTimeout: 0);
            });

            RunStep(connection, timer, "canonical values and arrays", transaction =>
            {
                connection.Execute(CanonicalValuesSql, new { localProcessor = WellKnownProviders.LocalProcessor }, transaction, commandTimeout: 0);
                connection.Execute(CanonicalArraysSql, new { persons }, transaction, commandTimeout: 0);
            });

            RunStep(connection, timer, "metadata claims", transaction =>
            {
                connection.Execute(
                    ClaimsSql,
                    new
                    {
                        persons,
                        localProcessor = WellKnownProviders.LocalProcessor,
                        scanner = WellKnownProviders.LibraryScanner,
                        wikidata = WellKnownProviders.Wikidata,
                        apple = WellKnownProviders.AppleApi,
                        tmdb = WellKnownProviders.Tmdb,
                        musicBrainz = WellKnownProviders.MusicBrainz,
                        comicVine = WellKnownProviders.ComicVine,
                    },
                    transaction,
                    commandTimeout: 0);
            });

            RunStep(connection, timer, "play history, person group", transaction =>
            {
                // Added 2026-10-20. Both read existing rows only (seed_leaf, seed_audio_match, persons) and add rows to tables no
                // earlier snapshot section reads, so the earlier sections keep their values.
                connection.Execute(PlayHistorySql, null, transaction, commandTimeout: 0);
                connection.Execute(PersonGroupSql, new { persons }, transaction, commandTimeout: 0);
            });

            RunStep(connection, timer, "artwork, review queue, identity jobs", transaction =>
            {
                connection.Execute(ArtworkSql, new { artwork }, transaction, commandTimeout: 0);
                connection.Execute(ReviewAndJobsSql, null, transaction, commandTimeout: 0);
                connection.Execute("DROP TABLE seed_audio_match; DROP TABLE seed_owner; DROP TABLE seed_leaf;", null, transaction);
            });

            RunStep(connection, timer, "mark every work Ready", transaction =>
            {
                // Home, detail and the library list show only Ready works. A fully processed real library has every work
                // Ready, so the seed matches it; without this Home and detail return no rows and look fast.
                connection.Execute(
                    "UPDATE works SET ready_at = '2026-01-01T00:00:00Z' WHERE ready_at IS NULL;",
                    null,
                    transaction,
                    commandTimeout: 0);
            });
        }

        using (var connection = database.CreateConnection())
        {
            // The product's startup runs PRAGMA optimize, which analyses tables that have none; ANALYZE gives the
            // planner the same statistics here so both stacks plan against a realistic file.
            connection.Execute("ANALYZE;", commandTimeout: 0);
            connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);", commandTimeout: 0);
        }

        Console.WriteLine($"[{timer.Elapsed:mm\\:ss}] ANALYZE and WAL checkpoint done");

        var counts = CountTables(database);
        var info = new DatasetInfo(
            scale,
            works,
            checked((int)(counts["works"] - works)),
            persons,
            collections,
            checked((int)counts["metadata_claims"]),
            artwork,
            counts);
        info.Save(databasePath);

        SqliteConnection.ClearAllPools();
        PrintSummary(databasePath, info, timer.Elapsed);
        return info;
    }

    private static void RunStep(SqliteConnection connection, Stopwatch timer, string name, Action<SqliteTransaction> body)
    {
        using var transaction = connection.BeginTransaction();
        body(transaction);
        transaction.Commit();
        Console.WriteLine($"[{timer.Elapsed:mm\\:ss}] {name}");
    }

    private static void InsertProviders(SqliteConnection connection, SqliteTransaction transaction)
    {
        var providers = new (Guid Id, string Name)[]
        {
            (WellKnownProviders.LocalProcessor, "local_processor"),
            (WellKnownProviders.LibraryScanner, "library_scanner"),
            (WellKnownProviders.Wikidata, "wikidata"),
            (WellKnownProviders.AppleApi, "apple_api"),
            (WellKnownProviders.Tmdb, "tmdb"),
            (WellKnownProviders.MusicBrainz, "musicbrainz"),
            (WellKnownProviders.ComicVine, "comicvine"),
        };

        foreach (var (id, name) in providers)
        {
            connection.Execute(
                "INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled) VALUES (@id, @name, '1', 1);",
                new { id, name },
                transaction);
        }
    }

    private static Dictionary<string, long> CountTables(DatabaseConnection database)
    {
        var tables = new[]
        {
            "works", "editions", "media_assets", "canonical_values", "canonical_value_arrays", "metadata_claims",
            "persons", "collections", "artwork_assets", "entity_artwork_links", "review_queue", "identity_jobs",
        };

        using var connection = database.CreateConnection();
        return tables.ToDictionary(t => t, t => connection.ExecuteScalar<long>($"SELECT COUNT(*) FROM {t};"));
    }

    private static void PrintSummary(string databasePath, DatasetInfo info, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine($"Seeded '{databasePath}' (scale: {info.Scale}) in {elapsed:mm\\:ss}.");
        foreach (var (table, count) in info.TableCounts)
        {
            Console.WriteLine($"  {table,-24} {count,12:N0}");
        }

        Console.WriteLine($"  (works = {info.Works:N0} leaf works with files + {info.ParentWorks:N0} parent works for TV shows and music albums)");
        var size = new FileInfo(databasePath).Length;
        Console.WriteLine($"  database file size       {size / 1024.0 / 1024.0,12:N1} MiB");
    }

    // ---------------------------------------------------------------------------------------------------------
    // SQL. Expressions: ID(kind, v) builds the deterministic 16-byte id (see SeedIds); TS(days, seconds) builds an
    // ISO-8601 timestamp 'yyyy-MM-ddTHH:mm:ss.fffZ' inside the 2024-10-17 .. 2026-10-17 window.
    // ---------------------------------------------------------------------------------------------------------

    private static string Id(int kind, string value) => SeedIds.Sql(kind, value);

    private static string Ts(string days, string seconds) =>
        $"strftime('%Y-%m-%dT%H:%M:%fZ', '2024-10-17 00:00:00', '+' || ({days}) || ' days', '+' || ({seconds}) || ' seconds')";

    private static readonly string CollectionsSql = $"""
        WITH RECURSIVE s(k) AS (SELECT 0 UNION ALL SELECT k + 1 FROM s WHERE k < @collections - 1)
        INSERT INTO collections (id, display_name, collection_type, description, created_at)
        SELECT {Id(SeedIds.Collection, "k")}, 'Series ' || k, 'Series', 'Collection ' || k, {Ts("k % 700", "k * 17 % 86400")}
        FROM s;
        """;

    private static readonly string PersonsSql = $"""
        WITH RECURSIVE s(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM s WHERE v < @persons)
        INSERT INTO persons (id, name, wikidata_qid, biography, created_at, enriched_at, is_pseudonym)
        SELECT {Id(SeedIds.Person, "v")},
               'Person ' || v,
               CASE WHEN v % 2 = 0 THEN 'Q' || (900000 + v) END,
               CASE WHEN v % 3 = 0 THEN 'Biography of person ' || v END,
               {Ts("v % 700", "v * 29 % 86400")},
               CASE WHEN v % 2 = 0 THEN {Ts("v % 700 + 1", "v * 29 % 86400")} END,
               0
        FROM s;
        """;

    // One row per file-bearing work. t = seq % 100 picks the media type (30% Books, 20% Movies, 25% TV episodes,
    // 15% Music tracks, 6% Audiobooks, 4% Comics). TV episodes group 20 to a show and tracks 12 to an album; the
    // show/album is a 'parent' work that holds the shared metadata and has no file of its own.
    private static readonly string LeafTableSql = $"""
        CREATE TEMP TABLE seed_leaf (
            seq INTEGER PRIMARY KEY,
            t INTEGER NOT NULL,
            media_type TEXT NOT NULL,
            ext TEXT NOT NULL,
            rank_in_type INTEGER NOT NULL,
            parent_key INTEGER,
            work_id BLOB NOT NULL,
            edition_id BLOB NOT NULL,
            asset_id BLOB NOT NULL,
            parent_id BLOB,
            owner_id BLOB NOT NULL);

        WITH RECURSIVE s(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM s WHERE v < @n),
        typed AS (
            SELECT v, v % 100 AS t, v / 100 AS b,
                   CASE WHEN v % 100 < 30 THEN 'Books'
                        WHEN v % 100 < 50 THEN 'Movies'
                        WHEN v % 100 < 75 THEN 'TV'
                        WHEN v % 100 < 90 THEN 'Music'
                        WHEN v % 100 < 96 THEN 'Audiobooks'
                        ELSE 'Comics' END AS media_type
            FROM s),
        ranked AS (
            SELECT v, t, media_type,
                   CASE WHEN media_type = 'TV' THEN b * 25 + (t - 50)
                        WHEN media_type = 'Music' THEN b * 15 + (t - 75)
                        ELSE 0 END AS rk
            FROM typed),
        keyed AS (
            SELECT v, t, media_type, rk,
                   CASE WHEN media_type = 'TV' THEN rk / 20
                        WHEN media_type = 'Music' THEN {SeedIds.MusicParentBase} + rk / 12 END AS pk
            FROM ranked)
        INSERT INTO seed_leaf (seq, t, media_type, ext, rank_in_type, parent_key, work_id, edition_id, asset_id, parent_id, owner_id)
        SELECT v, t, media_type,
               CASE media_type
                   WHEN 'Books' THEN CASE WHEN v % 6 = 0 THEN '.pdf' ELSE '.epub' END
                   WHEN 'Movies' THEN '.mkv'
                   WHEN 'TV' THEN '.mkv'
                   WHEN 'Music' THEN CASE WHEN v % 2 = 0 THEN '.flac' ELSE '.mp3' END
                   WHEN 'Audiobooks' THEN '.m4b'
                   ELSE '.cbz' END,
               rk, pk,
               {Id(SeedIds.Work, "v")},
               {Id(SeedIds.Edition, "v")},
               {Id(SeedIds.Asset, "v")},
               CASE WHEN pk IS NULL THEN NULL ELSE {Id(SeedIds.Parent, "pk")} END,
               COALESCE(CASE WHEN pk IS NULL THEN NULL ELSE {Id(SeedIds.Parent, "pk")} END, {Id(SeedIds.Work, "v")})
        FROM keyed;

        -- One row per owner of work-level metadata: each standalone work, and each TV show / music album.
        CREATE TEMP TABLE seed_owner (
            owner_id BLOB PRIMARY KEY,
            seq INTEGER NOT NULL,
            media_type TEXT NOT NULL,
            is_parent INTEGER NOT NULL);
        INSERT INTO seed_owner SELECT work_id, seq, media_type, 0 FROM seed_leaf WHERE parent_id IS NULL;
        INSERT INTO seed_owner SELECT parent_id, MIN(seq), media_type, 1 FROM seed_leaf WHERE parent_id IS NOT NULL GROUP BY parent_id;

        -- Audiobook classes (cls = dense audiobook index % 8). 0-3 (half) keep their own original year. 4-5 (a quarter of all)
        -- have no year, a book's title (case and spacing differ) and that book's author. 6 (an eighth) has no year and a book's
        -- title but a different author. 7 (an eighth) has no year and matches nothing. book_seq is the book a class 4-6
        -- audiobook is paired with, spread over all books with a fixed stride. See the comment on CanonicalValuesSql.
        CREATE TEMP TABLE seed_audio_match (
            audio_seq INTEGER PRIMARY KEY,
            cls INTEGER NOT NULL,
            book_seq INTEGER NOT NULL);
        WITH a AS (SELECT seq, ROW_NUMBER() OVER (ORDER BY seq) - 1 AS ai FROM seed_leaf WHERE media_type = 'Audiobooks'),
        bk AS (SELECT seq, ROW_NUMBER() OVER (ORDER BY seq) - 1 AS bi FROM seed_leaf WHERE media_type = 'Books'),
        nb AS (SELECT COUNT(*) AS n FROM bk)
        INSERT INTO seed_audio_match (audio_seq, cls, book_seq)
        SELECT a.seq, a.ai % 8, bk.seq
        FROM a CROSS JOIN nb INNER JOIN bk ON bk.bi = (a.ai * 7) % nb.n;
        """;

    private static readonly string WorksSql = $"""
        INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id, ordinal, curator_state, wikidata_status, wikidata_qid)
        SELECT owner_id, NULL, media_type, 'parent', NULL, NULL, 'accepted', 'linked', 'Q' || (500000 + seq)
        FROM seed_owner WHERE is_parent = 1;

        INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, curator_state, wikidata_status, wikidata_qid)
        SELECT work_id,
               CASE WHEN (t < 30 OR t >= 96) AND seq % 7 = 0 THEN {Id(SeedIds.Collection, "seq % @collections")} END,
               media_type,
               CASE WHEN parent_id IS NULL THEN 'standalone' ELSE 'child' END,
               parent_id,
               CASE WHEN parent_id IS NULL THEN NULL WHEN media_type = 'TV' THEN rank_in_type % 20 + 1 ELSE rank_in_type % 12 + 1 END,
               CASE WHEN parent_id IS NULL THEN NULL WHEN media_type = 'TV' THEN rank_in_type % 20 + 1.0 ELSE rank_in_type % 12 + 1.0 END,
               CASE WHEN seq % 97 = 0 THEN 'provisional' ELSE 'accepted' END,
               CASE WHEN seq % 10 < 7 THEN 'linked' ELSE 'pending' END,
               CASE WHEN seq % 10 < 7 THEN 'Q' || (1000 + seq * 3) END
        FROM seed_leaf;
        """;

    private static readonly string EditionsAndAssetsSql = $"""
        INSERT INTO editions (id, work_id, format_label)
        SELECT edition_id, work_id, CASE WHEN seq % 5 = 0 THEN 'Special Edition' ELSE 'Standard' END
        FROM seed_leaf;

        INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, status, library_id, is_orphaned)
        SELECT asset_id,
               edition_id,
               lower(hex(asset_id)),
               'D:/Media/' || media_type || '/' || printf('%04d', seq / 100) || '/Item ' || seq || ext,
               {Ts("seq * 7 % 730", "seq * 131 % 86400 + 3600")},
               'Normal',
               'library-main',
               0
        FROM seed_leaf;
        """;

    // Audiobooks: half carry their own original publication year (an identified audiobook normally does, from a retail or
    // Wikidata match). The other half have none (since 2026-10-20; before that every audiobook had a year, so numbers measured
    // earlier never exercised the look-up below). Without a year the display and library list reads borrow the year of the
    // book with the same title and the same author (MediaDateSql.RelatedBookOriginalYearCte): of the audiobooks without a year,
    // half match a book by title (case and surrounding spaces differ) and by author, a quarter match a book's title but have a
    // different author, and a quarter match nothing. Classes are in seed_audio_match.
    private static readonly string CanonicalValuesSql = $"""
        INSERT INTO canonical_values (entity_id, key, value, last_scored_at, winning_provider_id)
        SELECT asset_id, 'title',
               CASE WHEN cls BETWEEN 4 AND 6 THEN '  ' || UPPER('Books Title ' || book_seq) || ' ' ELSE media_type || ' Title ' || seq END,
               {Ts("seq * 7 % 730", "seq * 131 % 86400")}, @localProcessor
        FROM seed_leaf LEFT JOIN seed_audio_match ON audio_seq = seq
        UNION ALL SELECT asset_id, 'file_name', 'Item ' || seq || ext, {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf
        UNION ALL SELECT asset_id, 'cover_state', 'present', {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf WHERE seq % 12 != 5
        UNION ALL SELECT asset_id, 'release_year', CAST(1950 + (seq * 31 % 75) AS TEXT), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf
        UNION ALL SELECT asset_id, 'language', CASE seq % 3 WHEN 0 THEN 'en' WHEN 1 THEN 'fr' ELSE 'de' END, {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf
        UNION ALL SELECT asset_id, 'runtime', CAST(20 + seq % 160 AS TEXT), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf
        UNION ALL SELECT asset_id, 'duration', CAST(600 + (seq * 37) % 36000 AS TEXT), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL
                  FROM seed_leaf WHERE media_type IN ('Audiobooks', 'Music', 'Movies', 'TV')
        UNION ALL SELECT asset_id, 'wikidata_qid', 'Q' || (1000 + seq * 3), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf WHERE seq % 10 < 7
        UNION ALL SELECT asset_id, 'original_publication_year', CAST(1950 + (seq * 31 % 75) AS TEXT), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL
                  FROM seed_leaf WHERE media_type = 'Audiobooks' AND seq IN (SELECT audio_seq FROM seed_audio_match WHERE cls < 4)
        UNION ALL SELECT asset_id, 'episode_title', 'Episode ' || (rank_in_type % 20 + 1), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf WHERE media_type = 'TV'
        UNION ALL SELECT asset_id, 'season_number', CAST(rank_in_type % 20 / 10 + 1 AS TEXT), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf WHERE media_type = 'TV'
        UNION ALL SELECT asset_id, 'episode_number', CAST(rank_in_type % 10 + 1 AS TEXT), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf WHERE media_type = 'TV'
        UNION ALL SELECT asset_id, 'track_number', CAST(rank_in_type % 12 + 1 AS TEXT), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_leaf WHERE media_type = 'Music';

        INSERT INTO canonical_values (entity_id, key, value, last_scored_at, winning_provider_id)
        SELECT owner_id, 'title',
               CASE WHEN is_parent = 1 THEN media_type || ' Show or Album ' || seq
                    WHEN cls BETWEEN 4 AND 6 THEN '  ' || UPPER('Books Title ' || book_seq) || ' '
                    ELSE media_type || ' Work ' || seq END,
               {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL
        FROM seed_owner LEFT JOIN seed_audio_match ON audio_seq = seq AND is_parent = 0
        UNION ALL SELECT owner_id, 'description', 'A long-form description of item ' || seq || ' used to give the text columns realistic width.', {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_owner
        UNION ALL SELECT owner_id, 'short_description', 'Short description ' || seq, {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_owner
        UNION ALL SELECT owner_id, 'series', 'Series ' || (seq % 900), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_owner WHERE media_type = 'Books' AND seq % 4 = 0
        UNION ALL SELECT owner_id, 'cover_state', 'present', {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_owner WHERE is_parent = 1
        UNION ALL SELECT owner_id, 'show_name', 'Show ' || seq, {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_owner WHERE is_parent = 1 AND media_type = 'TV'
        UNION ALL SELECT owner_id, 'network', 'Network ' || (seq % 12), {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_owner WHERE is_parent = 1 AND media_type = 'TV'
        UNION ALL SELECT owner_id, 'album', 'Album ' || seq, {Ts("seq * 7 % 730", "seq * 131 % 86400")}, NULL FROM seed_owner WHERE is_parent = 1 AND media_type = 'Music';
        """;

    private static readonly string CanonicalArraysSql = $"""
        INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
        SELECT owner_id, 'genre', 0, 'Genre ' || (seq % 40) FROM seed_owner
        UNION ALL SELECT owner_id, 'author', 0, 'Person ' || (1 + (COALESCE(CASE WHEN cls IN (4, 5) THEN book_seq END, seq) * 13) % @persons)
                  FROM seed_owner LEFT JOIN seed_audio_match ON audio_seq = seq AND is_parent = 0
                  WHERE media_type IN ('Books', 'Comics', 'Audiobooks')
        UNION ALL SELECT owner_id, 'author', 1, 'Person ' || (1 + (seq * 17) % @persons) FROM seed_owner WHERE media_type = 'Comics' AND seq % 3 = 0
        UNION ALL SELECT owner_id, 'narrator', 0, 'Person ' || (1 + (seq * 19) % @persons) FROM seed_owner WHERE media_type = 'Audiobooks'
        UNION ALL SELECT owner_id, 'director', 0, 'Person ' || (1 + (seq * 13) % @persons) FROM seed_owner WHERE media_type = 'Movies'
        UNION ALL SELECT owner_id, 'artist', 0, 'Person ' || (1 + (seq * 13) % @persons) FROM seed_owner WHERE media_type = 'Music'
        UNION ALL SELECT owner_id, 'cast_member', 0, 'Person ' || (1 + (seq * 23) % @persons) FROM seed_owner WHERE media_type IN ('Movies', 'TV')
        UNION ALL SELECT owner_id, 'cast_member', 1, 'Person ' || (1 + (seq * 29) % @persons) FROM seed_owner WHERE media_type IN ('Movies', 'TV')
        UNION ALL SELECT owner_id, 'cast_member', 2, 'Person ' || (1 + (seq * 31) % @persons) FROM seed_owner WHERE media_type IN ('Movies', 'TV');
        """;

    // Five claims per file-bearing work: four on the media asset (two current title sources, a credit, a release year,
    // some superseded) and one description on the work. claimed_at is unique per entity so ordered reads are stable.
    private static readonly string ClaimsSql = $"""
        INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at, is_user_locked, is_current, superseded_at)
        SELECT {Id(SeedIds.Claim, "seq * 10 + 1")}, asset_id, @localProcessor, 'title', media_type || ' Title ' || seq, 0.6,
               {Ts("seq * 7 % 730", "seq * 131 % 86400 + 37")}, CASE WHEN seq % 200 = 0 THEN 1 ELSE 0 END, 1, NULL
        FROM seed_leaf
        UNION ALL
        SELECT {Id(SeedIds.Claim, "seq * 10 + 2")}, asset_id,
               CASE media_type WHEN 'Books' THEN @apple WHEN 'Audiobooks' THEN @apple WHEN 'Movies' THEN @tmdb WHEN 'TV' THEN @tmdb
                               WHEN 'Music' THEN @musicBrainz ELSE @comicVine END,
               'title', media_type || ' Retail Title ' || seq, 0.9,
               {Ts("seq * 7 % 730", "seq * 131 % 86400 + 74")}, 0, CASE WHEN seq % 4 = 0 THEN 0 ELSE 1 END,
               CASE WHEN seq % 4 = 0 THEN {Ts("seq * 7 % 730 + 30", "seq * 131 % 86400")} END
        FROM seed_leaf
        UNION ALL
        SELECT {Id(SeedIds.Claim, "seq * 10 + 3")}, asset_id, @wikidata,
               CASE media_type WHEN 'Movies' THEN 'director' WHEN 'TV' THEN 'cast_member' WHEN 'Music' THEN 'artist'
                               WHEN 'Audiobooks' THEN 'narrator' ELSE 'author' END,
               'Person ' || (1 + (seq * 13) % @persons), 0.95,
               {Ts("seq * 7 % 730", "seq * 131 % 86400 + 111")}, 0, CASE WHEN seq % 5 = 0 THEN 0 ELSE 1 END,
               CASE WHEN seq % 5 = 0 THEN {Ts("seq * 7 % 730 + 60", "seq * 131 % 86400")} END
        FROM seed_leaf
        UNION ALL
        SELECT {Id(SeedIds.Claim, "seq * 10 + 4")}, asset_id, @scanner, 'release_year', CAST(1950 + (seq * 31 % 75) AS TEXT), 0.5,
               {Ts("seq * 7 % 730", "seq * 131 % 86400 + 148")}, 0, CASE WHEN seq % 3 = 0 THEN 0 ELSE 1 END,
               CASE WHEN seq % 3 = 0 THEN {Ts("seq * 7 % 730 + 90", "seq * 131 % 86400")} END
        FROM seed_leaf
        UNION ALL
        SELECT {Id(SeedIds.Claim, "seq * 10 + 5")}, work_id, @wikidata, 'description', 'Claimed description for item ' || seq, 0.9,
               {Ts("seq * 7 % 730", "seq * 131 % 86400 + 185")}, 0, 1, NULL
        FROM seed_leaf;
        """;

    // One profile's play history (SeedIds.PlayHistoryProfile), 60 plays chosen by a fixed stride from each group: 20 audiobooks
    // without their own year (classes 4 to 7 of seed_audio_match: some borrow a book's year, some borrow nothing), 10 audiobooks
    // with their own year, 10 books, 10 TV episodes and 10 tracks. i is a fixed shuffle of the 60, so last_accessed is distinct
    // and the order is fixed. Every sixth play is finished (100%), plays 7 and 13 are hidden from Continue, the rest sit
    // between 5% and 95%; two in three of the visible, unfinished plays also carry a saved position.
    private static readonly string PlayHistorySql = $"""
        WITH cand AS (
            SELECT l.seq, l.asset_id,
                   CASE WHEN l.media_type = 'Audiobooks' AND m.cls >= 4 THEN 1
                        WHEN l.media_type = 'Audiobooks' THEN 2
                        WHEN l.media_type = 'Books' THEN 3
                        WHEN l.media_type = 'TV' THEN 4
                        ELSE 5 END AS cat
            FROM seed_leaf l
            LEFT JOIN seed_audio_match m ON m.audio_seq = l.seq
            WHERE l.media_type IN ('Audiobooks', 'Books', 'TV', 'Music')
        ),
        ranked AS (
            SELECT seq, asset_id, cat,
                   CASE cat WHEN 1 THEN 20 ELSE 10 END AS want,
                   ROW_NUMBER() OVER (PARTITION BY cat ORDER BY seq) AS rn,
                   COUNT(*) OVER (PARTITION BY cat) AS total
            FROM cand
        ),
        chosen AS (
            SELECT asset_id, ROW_NUMBER() OVER (ORDER BY (seq * 7919) % 10007, seq) AS i
            FROM ranked
            WHERE rn * want / total > (rn - 1) * want / total
        )
        INSERT INTO user_states (revision, user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties)
        SELECT 0,
               {Id(SeedIds.Profile, "1")},
               asset_id,
               NULL,
               CASE WHEN i % 6 = 0 THEN 100.0 ELSE 5.0 + (i * 29) % 91 END,
               strftime('%Y-%m-%dT%H:%M:%fZ', '2026-09-01 00:00:00', '+' || (i * 3607) || ' seconds'),
               CASE WHEN i IN (7, 13) THEN json_object('hide_continue', 'true')
                    WHEN i % 3 != 0 THEN json_object('position_seconds', (5 + (i * 29) % 91) * 36, 'duration_seconds', 3600) END
        FROM chosen;
        """;

    // One group and one member, both existing persons, so the earlier sections' credits do not change: the group is the person
    // in the middle of the list (credited on several works already), the member the next one. No dates, so the membership
    // overlaps every work and the member's credits through the group are all shown.
    private static readonly string PersonGroupSql = $"""
        INSERT INTO person_group_members (group_id, member_id, start_date, end_date)
        SELECT {Id(SeedIds.Person, "@persons / 2")}, {Id(SeedIds.Person, "@persons / 2 + 1")}, NULL, NULL;

        UPDATE persons SET is_group = 1 WHERE id = {Id(SeedIds.Person, "@persons / 2")};
        """;

    private static readonly string ArtworkSql = $"""
        WITH RECURSIVE s(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM s WHERE v < @artwork)
        INSERT INTO artwork_assets (id, content_hash, original_path, small_path, medium_path, large_path, width_px, height_px,
                                    aspect_class, primary_hex, accent_hex, source_provider, source_url, created_at)
        SELECT {Id(SeedIds.ArtworkAsset, "v")},
               lower(hex({Id(SeedIds.ArtworkAsset, "v")})),
               'D:/Library/artwork/' || v || '.jpg',
               'D:/Library/artwork/' || v || '_s.jpg',
               'D:/Library/artwork/' || v || '_m.jpg',
               'D:/Library/artwork/' || v || '_l.jpg',
               600 + v % 400, 900 + v % 600, 'Portrait', '#336699', '#cc9933', 'tmdb', 'https://image.example/' || v || '.jpg',
               {Ts("v % 730", "v * 13 % 86400")}
        FROM s;

        WITH RECURSIVE s(v) AS (SELECT 1 UNION ALL SELECT v + 1 FROM s WHERE v < @artwork)
        INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, context, source_asset_type, is_preferred, sort_order, created_at)
        SELECT {Id(SeedIds.ArtworkLink, "v")}, l.work_id, 'Work', {Id(SeedIds.ArtworkAsset, "v")}, 'Primary', NULL, 'CoverArt', 1, 0,
               {Ts("v % 730", "v * 13 % 86400")}
        FROM s INNER JOIN seed_leaf l ON l.seq = v * 2;
        """;

    private static readonly string ReviewAndJobsSql = $"""
        INSERT INTO review_queue (id, entity_id, entity_type, trigger, status, confidence_score, candidates_json, created_at)
        SELECT {Id(SeedIds.Review, "seq")},
               CASE WHEN seq % 3 = 0 THEN work_id ELSE asset_id END,
               CASE WHEN seq % 3 = 0 THEN 'Work' ELSE 'MediaAsset' END,
               CASE seq % 4 WHEN 0 THEN 'LowConfidence' WHEN 1 THEN 'MissingQid' WHEN 2 THEN 'MultipleQidMatches' ELSE 'RetailMatchFailed' END,
               'Pending', 0.4 + (seq % 5) / 10.0, '[]',
               {Ts("seq * 7 % 730 + 1", "seq * 131 % 86400")}
        FROM seed_leaf WHERE seq % 50 = 7;

        INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, pass, resolved_qid, created_at, updated_at)
        SELECT {Id(SeedIds.Job, "seq")}, asset_id, 'MediaAsset', media_type,
               CASE seq % 6 WHEN 0 THEN 'Ready' WHEN 1 THEN 'ReadyWithoutUniverse' WHEN 2 THEN 'Hydrating'
                            WHEN 3 THEN 'RetailNoMatch' WHEN 4 THEN 'QidNeedsReview' ELSE 'QidNoMatch' END,
               'Quick',
               CASE WHEN seq % 6 = 0 THEN 'Q' || (1000 + seq * 3) END,
               {Ts("seq * 7 % 730 + 1", "seq * 131 % 86400")},
               {Ts("seq * 7 % 730 + 2", "seq * 131 % 86400")}
        FROM seed_leaf WHERE seq % 50 = 13;
        """;
}
