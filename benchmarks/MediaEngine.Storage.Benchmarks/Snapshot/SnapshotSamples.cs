using Dapper;
using MediaEngine.Storage;

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
        return samples;
    }
}
