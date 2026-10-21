using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Benchmarks.Seeding;

namespace MediaEngine.Storage.Benchmarks.Snapshot;

/// <summary>
/// The fixed ids the snapshot asks about, chosen by SQL over the seeded file: ordered by id, then picked by a
/// deterministic stride. The same seeded file always gives the same ids.
/// </summary>
internal sealed class SnapshotSamples
{
    private SnapshotSamples()
    {
    }

    public IReadOnlyList<Guid> TvShowRoots { get; private set; } = [];

    public IReadOnlyList<Guid> AlbumRoots { get; private set; } = [];

    public IReadOnlyList<Guid> StandaloneWorks { get; private set; } = [];

    /// <summary>Label and work id for the single-item reads, in the order they are written. Id is null when the file has no such item.</summary>
    public IReadOnlyList<(string Label, Guid? Id)> Works { get; private set; } = [];

    /// <summary>Person id for the person detail read, or null when no person has at least three credited items.</summary>
    public Guid? Person { get; private set; }

    /// <summary>The series pages (<c>series-page:&lt;label&gt;</c>) the snapshot opens. Added 2026-10-20 (spec "Audiobook year on the remaining screens").</summary>
    public IReadOnlyList<SeriesPageSample> SeriesPages { get; private set; } = [];

    /// <summary>The seeded play-history profile, or null when the file has no play history (seeded before 2026-10-20).</summary>
    public Guid? PlayProfile { get; private set; }

    /// <summary>40 works for <c>collection-resolve</c>: 20 audiobooks without a year of their own, then 5 books, 5 movies, 5 TV episodes and 5 tracks.</summary>
    public IReadOnlyList<Guid> ResolveWorkIds { get; private set; } = [];

    /// <summary>Every work id, handed to the collection editor's "add media" read as the works the person may see.</summary>
    public IReadOnlyList<Guid> AllWorkIds { get; private set; } = [];

    /// <summary>Label and query text for <c>series-search</c>; the query is null when no sample was found.</summary>
    public IReadOnlyList<(string Label, string? Query)> SearchQueries { get; private set; } = [];

    /// <summary>Label and person id for <c>person-credits</c>. Id is null when the file has no such person.</summary>
    public IReadOnlyList<(string Label, Guid? Id)> CreditPersons { get; private set; } = [];

    /// <summary>The labels (from <see cref="Works"/>) the library item detail read is asked about.</summary>
    public static readonly string[] LibraryDetailLabels =
        ["book", "audiobook-own-year", "audiobook-borrowed-year", "tv-episode", "music-track", "movie"];

    public static SnapshotSamples Choose(BenchContext context)
    {
        using var connection = context.Database.CreateConnection();

        List<Guid> Ids(string sql) => [.. connection.Query<byte[]>(sql).Select(GuidSql.FromDb)];

        static IReadOnlyList<Guid> Spread(List<Guid> ids, int count)
        {
            if (ids.Count <= count)
            {
                return ids;
            }

            var picked = new List<Guid>(count);
            for (var i = 0; i < count; i++)
            {
                picked.Add(ids[(int)((long)i * ids.Count / count + ids.Count / (2 * count))]);
            }

            return picked;
        }

        static Guid? Middle(List<Guid> ids) => ids.Count == 0 ? null : ids[ids.Count / 2];

        var samples = new SnapshotSamples
        {
            TvShowRoots = Spread(Ids("SELECT id FROM works WHERE media_type = 'TV' AND work_kind = 'parent' ORDER BY id;"), 5),
            AlbumRoots = Spread(Ids("SELECT id FROM works WHERE media_type = 'Music' AND work_kind = 'parent' ORDER BY id;"), 5),
            StandaloneWorks = Spread(Ids("SELECT id FROM works WHERE work_kind = 'standalone' ORDER BY id;"), 10),
        };

        const string AudiobookWithoutOwnYear = """
            SELECT w.id
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.media_type = 'Audiobooks'
              AND NOT EXISTS (
                  SELECT 1 FROM canonical_values cv
                  WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'original_publication_year')
            """;

        var works = new List<(string Label, Guid? Id)>
        {
            ("book", Middle(Ids("SELECT id FROM works WHERE media_type = 'Books' AND work_kind = 'standalone' ORDER BY id;"))),
            ("audiobook-own-year", Middle(Ids("""
                SELECT w.id
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE w.media_type = 'Audiobooks'
                  AND EXISTS (
                      SELECT 1 FROM canonical_values cv
                      WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'original_publication_year')
                ORDER BY w.id;
                """))),
            // No year of its own, but a book with the same title (ignoring case and surrounding spaces) and the same author.
            ("audiobook-borrowed-year", Middle(Ids(AudiobookWithoutOwnYear + """

                  AND EXISTS (
                      SELECT 1
                      FROM canonical_values audio_title
                      INNER JOIN canonical_values book_title
                          ON book_title.key = 'title'
                         AND LOWER(TRIM(book_title.value)) = LOWER(TRIM(audio_title.value))
                      INNER JOIN media_assets book_asset ON book_asset.id = book_title.entity_id
                      INNER JOIN editions book_edition ON book_edition.id = book_asset.edition_id
                      INNER JOIN works book_work ON book_work.id = book_edition.work_id AND book_work.media_type = 'Books'
                      INNER JOIN canonical_value_arrays book_author ON book_author.entity_id = book_work.id AND book_author.key = 'author'
                      INNER JOIN canonical_value_arrays audio_author
                          ON audio_author.entity_id = w.id AND audio_author.key = 'author' AND audio_author.value = book_author.value
                      WHERE audio_title.entity_id = ma.id AND audio_title.key = 'title')
                ORDER BY w.id;
                """))),
            ("tv-episode", Middle(Ids("SELECT id FROM works WHERE media_type = 'TV' AND work_kind = 'child' ORDER BY id;"))),
            ("music-track", Middle(Ids("SELECT id FROM works WHERE media_type = 'Music' AND work_kind = 'child' ORDER BY id;"))),
            ("movie", Middle(Ids("SELECT id FROM works WHERE media_type = 'Movies' ORDER BY id;"))),
            ("series-mates", Middle(Ids("""
                SELECT id FROM works
                WHERE collection_id IN (SELECT collection_id FROM works WHERE collection_id IS NOT NULL GROUP BY collection_id HAVING COUNT(*) >= 2)
                ORDER BY id;
                """))),
        };

        samples.Works = works;

        // First person by name (then id) with at least three distinct credited items.
        var person = Ids("""
            SELECT p.id
            FROM persons p
            WHERE (SELECT COUNT(DISTINCT a.entity_id) FROM canonical_value_arrays a WHERE a.value = p.name) >= 3
            ORDER BY p.name, p.id
            LIMIT 1;
            """);
        samples.Person = person.Count == 0 ? null : person[0];

        ChooseAudiobookYearSamples(connection, samples, Ids, Spread, Middle);
        return samples;
    }

    /// <summary>The samples for the reads of spec "Audiobook year on the remaining screens" (sections 3 and 6, WP1).</summary>
    private static void ChooseAudiobookYearSamples(
        System.Data.IDbConnection connection,
        SnapshotSamples samples,
        Func<string, List<Guid>> ids,
        Func<List<Guid>, int, IReadOnlyList<Guid>> spread,
        Func<List<Guid>, Guid?> middle)
    {
        var withoutYear = ids("SELECT w.id " + AudiobookWithoutOwnYearFrom + " ORDER BY w.id;");
        var withYear = ids("SELECT w.id " + AudiobookWithOwnYearFrom + " ORDER BY w.id;");
        var pages = new List<SeriesPageSample>();

        // audiobook-series: a collection with at least three audiobooks without a year. The seeder puts no audiobook in a
        // collection, so normally the audiobooks are handed over as resolved work ids of a collection id nothing owns.
        var audiobookCollection = middle(ids(
            "SELECT w.collection_id " + AudiobookWithoutOwnYearFrom
            + " AND w.collection_id IS NOT NULL GROUP BY w.collection_id HAVING COUNT(*) >= 3 ORDER BY w.collection_id;"));
        if (audiobookCollection is { } existingCollection)
        {
            pages.Add(new SeriesPageSample("audiobook-series", existingCollection, null, null, "a collection with at least three audiobooks without a year"));
        }
        else if (withoutYear.Count >= 3)
        {
            var resolved = new List<Guid>(spread(withoutYear, 30));
            resolved.AddRange(spread(withYear, 10));
            pages.Add(new SeriesPageSample(
                "audiobook-series",
                SeedIds.Id(SeedIds.Collection, 1_000_000),
                null,
                resolved,
                $"no seeded audiobook series, so {resolved.Count} audiobooks ({Math.Min(30, withoutYear.Count)} without a year of their own) are passed as resolvedWorkIds of a fresh collection id"));
        }

        // book-series: a collection with at least three books.
        var bookCollection = middle(ids(
            "SELECT w.collection_id FROM works w WHERE w.media_type = 'Books' AND w.collection_id IS NOT NULL GROUP BY w.collection_id HAVING COUNT(*) >= 3 ORDER BY w.collection_id;"));
        if (bookCollection is { } booksId)
        {
            pages.Add(new SeriesPageSample("book-series", booksId, null, null, "a collection with at least three books"));
        }

        // tv-season: a TV show root as rootWorkId. The seeded episodes sit directly under the show (no season level) and their
        // episode numbers are on the file, so the root alone matches nothing; the show's episodes are passed as resolved ids too.
        var showRoot = middle(ids("SELECT id FROM works WHERE media_type = 'TV' AND work_kind = 'parent' ORDER BY id;"));
        if (showRoot is { } rootId)
        {
            var episodes = connection.Query<byte[]>(
                    "SELECT id FROM works WHERE parent_work_id = @root ORDER BY ordinal_sort, id;",
                    new { root = GuidSql.ToBlob(rootId) })
                .Select(GuidSql.FromDb)
                .ToList();
            pages.Add(new SeriesPageSample(
                "tv-season",
                SeedIds.Id(SeedIds.Collection, 1_000_001),
                rootId,
                episodes,
                $"the show root as rootWorkId and its {episodes.Count} episodes as resolvedWorkIds (the seeded file has no season level)"));
        }

        samples.SeriesPages = pages;

        var hasPlayHistory = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM user_states WHERE user_id = @profile;",
            new { profile = GuidSql.ToBlob(SeedIds.PlayHistoryProfile) }) > 0;
        samples.PlayProfile = hasPlayHistory ? SeedIds.PlayHistoryProfile : null;

        // collection-resolve: 20 audiobooks without a year (spread over all four classes), then a mix of other types.
        var resolve = new List<Guid>(spread(withoutYear, 20));
        resolve.AddRange(spread(ids("SELECT id FROM works WHERE media_type = 'Books' AND work_kind = 'standalone' ORDER BY id;"), 5));
        resolve.AddRange(spread(ids("SELECT id FROM works WHERE media_type = 'Movies' ORDER BY id;"), 5));
        resolve.AddRange(spread(ids("SELECT id FROM works WHERE media_type = 'TV' AND work_kind = 'child' ORDER BY id;"), 5));
        resolve.AddRange(spread(ids("SELECT id FROM works WHERE media_type = 'Music' AND work_kind = 'child' ORDER BY id;"), 5));
        samples.ResolveWorkIds = resolve;
        samples.AllWorkIds = ids("SELECT id FROM works ORDER BY id;");

        // person-credits: an author of at least two audiobooks without a year (preferably one that borrows a book's year too),
        // one credited on an audiobook that borrows a book's year, and the member of the seeded group.
        List<Guid> AuthorsOfAudiobooksWithoutYear(string having) => ids(
            $"""
            SELECT p.id
            FROM persons p
            INNER JOIN (
                SELECT author.value AS author_name,
                       COUNT(DISTINCT w.id) AS audiobooks,
                       COUNT(DISTINCT CASE WHEN {BorrowsABooksYear} THEN w.id END) AS borrowers
                FROM works w
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                INNER JOIN canonical_value_arrays author ON author.entity_id = w.id AND author.key = 'author'
                WHERE w.media_type = 'Audiobooks'
                  AND NOT EXISTS (
                      SELECT 1 FROM canonical_values cv
                      WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'original_publication_year')
                GROUP BY author.value
            ) credited ON credited.author_name = p.name
            WHERE {having}
            ORDER BY p.name, p.id;
            """);
        var authorsOfSeveral = AuthorsOfAudiobooksWithoutYear("credited.audiobooks >= 2 AND credited.borrowers >= 1");
        if (authorsOfSeveral.Count == 0)
        {
            authorsOfSeveral = AuthorsOfAudiobooksWithoutYear("credited.audiobooks >= 2");
        }

        var audiobookAuthor = middle(authorsOfSeveral);
        var borrowingAuthor = middle(AuthorsOfAudiobooksWithoutYear("credited.borrowers >= 1"));
        var groupMember = ids("SELECT member_id FROM person_group_members ORDER BY group_id, member_id LIMIT 1;");
        // The pen name (a flagged person) and the first-billed author sharing a comic with them, whose credit it hides.
        var penName = ids("SELECT id FROM persons WHERE is_pseudonym = 1 ORDER BY id LIMIT 1;");
        var penNameCoAuthor = ids(
            """
            SELECT p.id
            FROM persons pen
            INNER JOIN canonical_value_arrays second_author
                ON second_author.key = 'author' AND second_author.ordinal = 1 AND second_author.value = pen.name
            INNER JOIN canonical_value_arrays first_author
                ON first_author.entity_id = second_author.entity_id AND first_author.key = 'author' AND first_author.ordinal = 0
            INNER JOIN persons p ON p.name = first_author.value
            WHERE pen.is_pseudonym = 1
            ORDER BY second_author.entity_id, p.id
            LIMIT 1;
            """);
        samples.CreditPersons =
        [
            ("audiobooks", audiobookAuthor),
            ("audiobook-borrowing", borrowingAuthor),
            ("group", groupMember.Count == 0 ? null : groupMember[0]),
            ("pen-name", penName.Count == 0 ? null : penName[0]),
            ("pen-name-coauthor", penNameCoAuthor.Count == 0 ? null : penNameCoAuthor[0]),
        ];

        // series-search: an audiobook title, a credited person's name and a word most titles share.
        var borrowedAudiobook = samples.Works.FirstOrDefault(work => work.Label == "audiobook-borrowed-year").Id;
        var audiobookTitle = borrowedAudiobook is { } audiobookId
            ? connection.ExecuteScalar<string?>(
                """
                SELECT TRIM(cv.value)
                FROM canonical_values cv
                INNER JOIN media_assets ma ON ma.id = cv.entity_id
                INNER JOIN editions e ON e.id = ma.edition_id
                WHERE e.work_id = @work AND cv.key = 'title'
                ORDER BY ma.id
                LIMIT 1;
                """,
                new { work = GuidSql.ToBlob(audiobookId) })
            : null;
        var personName = audiobookAuthor is { } authorId
            ? connection.ExecuteScalar<string?>("SELECT name FROM persons WHERE id = @id;", new { id = GuidSql.ToBlob(authorId) })
            : null;
        samples.SearchQueries =
        [
            ("audiobook-title", audiobookTitle),
            ("person-name", personName),
            ("common-word", "Title"),
        ];
    }

    // The audiobooks without a year of their own: no 'original_publication_year' on the file or on the work.
    private const string AudiobookWithoutOwnYearFrom = """
        FROM works w
        INNER JOIN editions e ON e.work_id = w.id
        INNER JOIN media_assets ma ON ma.edition_id = e.id
        WHERE w.media_type = 'Audiobooks'
          AND NOT EXISTS (
              SELECT 1 FROM canonical_values cv
              WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'original_publication_year')
        """;

    private const string AudiobookWithOwnYearFrom = """
        FROM works w
        INNER JOIN editions e ON e.work_id = w.id
        INNER JOIN media_assets ma ON ma.edition_id = e.id
        WHERE w.media_type = 'Audiobooks'
          AND EXISTS (
              SELECT 1 FROM canonical_values cv
              WHERE cv.entity_id IN (ma.id, w.id) AND cv.key = 'original_publication_year')
        """;

    // A book with the same title (ignoring case and surrounding spaces) and the same author as the audiobook (w, ma).
    private const string BorrowsABooksYear = """
        EXISTS (
            SELECT 1
            FROM canonical_values audio_title
            INNER JOIN canonical_values book_title
                ON book_title.key = 'title'
               AND LOWER(TRIM(book_title.value)) = LOWER(TRIM(audio_title.value))
            INNER JOIN media_assets book_asset ON book_asset.id = book_title.entity_id
            INNER JOIN editions book_edition ON book_edition.id = book_asset.edition_id
            INNER JOIN works book_work ON book_work.id = book_edition.work_id AND book_work.media_type = 'Books'
            INNER JOIN canonical_value_arrays book_author ON book_author.entity_id = book_work.id AND book_author.key = 'author'
            INNER JOIN canonical_value_arrays audio_author
                ON audio_author.entity_id = w.id AND audio_author.key = 'author' AND audio_author.value = book_author.value
            WHERE audio_title.entity_id = ma.id AND audio_title.key = 'title')
        """;
}

/// <summary>One series page the snapshot opens: the collection id, an optional root work id, optional resolved work ids, and a note for the section header.</summary>
internal sealed record SeriesPageSample(string Label, Guid CollectionId, Guid? RootWorkId, IReadOnlyList<Guid>? ResolvedWorkIds, string Note);
