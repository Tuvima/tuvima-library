using System.Diagnostics;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

internal sealed class RecentCandidate
{
    public Guid WorkId { get; init; }
    public Guid RootWorkId { get; init; }
    public Guid? CollectionId { get; init; }
    public string? LibraryId { get; init; }
    public string MediaType { get; init; } = string.Empty;
    public DateTimeOffset? AddedAt { get; init; }
}

/// <summary>The roots (shows, albums) and individual works whose rows a recent page needs.</summary>
public sealed record DisplayRecentGroupFilter(IReadOnlyCollection<Guid> RootIds, IReadOnlyCollection<Guid> WorkIds);

public sealed class DisplayWorkProjectionReader
{
    private readonly IDatabaseConnection _db;
    private readonly ILogger<DisplayWorkProjectionReader>? _logger;

    public DisplayWorkProjectionReader(
        IDatabaseConnection db,
        ILogger<DisplayWorkProjectionReader>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    internal static string BuildSql(bool hasDetail, bool lean = false, bool hasGroupFilter = false)
    {
        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var detailPredicate = hasDetail ? """
            -- readiness: fragment of the projection query, which filters on the Ready rule
            AND (w.id = @detailId OR p.id = @detailId OR gp.id = @detailId
                OR w.collection_id = (SELECT collection_id FROM works WHERE id = @detailId)
                -- the files that credit the person @detailId, found by LoadPersonAssetsAsync (a JSON array of hex ids)
                OR ma.id IN (SELECT unhex(value) FROM json_each(@personAssetIds)))
            """ : "";
        // Recent pages only load the shows, albums and works that can appear on the page.
        var groupPredicate = hasGroupFilter ? """
            AND ((CASE WHEN w.media_type = 'Music' THEN COALESCE(p.id, w.id) ELSE COALESCE(gp.id, p.id, w.id) END) IN @rootIds
                 OR w.id IN @workIds)
            """ : "";
        // The borrowed audiobook year is worked out once for the audiobook rows that were chosen, not once per row.
        var relatedBookYearCte = MediaDateSql.RelatedBookOriginalYearCte(
            "related_book_year",
            """
            SELECT chosen_audiobook.WorkId AS work_id, chosen_audiobook.AssetId AS asset_id
            FROM chosen chosen_audiobook
            WHERE LOWER(COALESCE(chosen_audiobook.MediaType, '')) LIKE '%audio%'
            """,
            "rby_");
        // Credits are worked out for the chosen rows' files only (the shared whole-library credit list stays for other screens).
        var projectionCreditsCte = PersonCreditSql.ScopedCreditsCte(
            "projection_credits",
            "SELECT AssetId FROM chosen",
            "pc_");

        // Every display field is chosen from the stored values of the chosen rows' entities, which are read once (below).
        var reads = new PivotReads();
        var selectList = new List<string>();
        foreach (var column in ProjectionColumns())
        {
            // Search-only columns are never shown on a card, so list pages skip them (and the values only they read).
            var columnReads = new PivotReads();
            var expression = column.Build(columnReads);
            if (!lean || !column.SearchOnly)
            {
                reads.Add(columnReads);
            }

            selectList.Add(column.SearchOnly
                ? $"/*search-only:begin*/{expression}/*search-only:end*/ AS {column.Name}"
                : column.Name == expression ? column.Name : $"{expression} AS {column.Name}");
        }

        var sql = $"""
            WITH asset_dates AS MATERIALIZED (
                SELECT entity_id, MIN(claimed_at) AS claimed_at
                FROM metadata_claims GROUP BY entity_id
            ),
            ranked_assets AS (
                SELECT
                    w.id AS WorkId,
                    ma.library_id AS LibraryId,
                    w.collection_id AS CollectionId,
                    w.media_type AS MediaType,
                    w.work_kind AS WorkKind,
                    e.id AS EditionId,
                    CASE
                        WHEN w.media_type = 'Music' THEN COALESCE(p.id, w.id)
                        ELSE COALESCE(gp.id, p.id, w.id)
                    END AS RootWorkId,
                    ma.id AS AssetId,
                    MIN(mc.claimed_at) OVER (PARTITION BY w.id, ma.library_id) AS CreatedAt,
                    ROW_NUMBER() OVER (
                        PARTITION BY w.id, ma.library_id
                        ORDER BY CASE WHEN w.media_type = 'Books' AND LOWER(ma.file_path_root) LIKE '%.epub' THEN 0 ELSE 1 END,
                                 CASE WHEN mc.claimed_at IS NULL THEN 1 ELSE 0 END, mc.claimed_at ASC, ma.id
                    ) AS AssetRank
                FROM works w
                INNER JOIN work_owned_assets woa ON woa.work_id = w.id
                INNER JOIN editions e ON e.id = woa.edition_id
                INNER JOIN media_assets ma ON ma.id = woa.asset_id
                LEFT JOIN asset_dates mc ON mc.entity_id = ma.id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.work_kind != 'parent'
                  AND ma.status = 'Normal' AND ma.is_orphaned = 0
                  AND {visibleWorkPredicate}
                  AND {visibleAssetPredicate}
                  {detailPredicate}
                  {groupPredicate}
            ),
            -- The rows are chosen before any credit is worked out; ties on the added time keep a fixed order.
            chosen AS MATERIALIZED (
                SELECT * FROM ranked_assets
                WHERE AssetRank = 1
                ORDER BY COALESCE(CreatedAt, CURRENT_TIMESTAMP) DESC, WorkId, LibraryId
                LIMIT @limit
            ),
            {relatedBookYearCte},
            {projectionCreditsCte},
            canonical_artist_credits AS (
                SELECT
                    credit.media_asset_id,
                    person.id AS person_id,
                    person.name AS person_name,
                    ROW_NUMBER() OVER (
                        PARTITION BY credit.media_asset_id
                        ORDER BY
                            credit.billing_order,
                            CASE WHEN NULLIF(TRIM(person.local_headshot_path), '') IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN NULLIF(TRIM(person.headshot_url), '') IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN NULLIF(TRIM(person.biography), '') IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN person.enriched_at IS NOT NULL THEN 0 ELSE 1 END,
                            CASE WHEN NULLIF(TRIM(person.wikidata_qid), '') IS NOT NULL THEN 0 ELSE 1 END,
                            person.created_at,
                            person.id
                    ) AS identity_rank
                FROM projection_credits credit
                INNER JOIN persons person
                    ON person.name = credit.person_name COLLATE NOCASE
                    OR (
                        NULLIF(TRIM(credit.person_qid), '') IS NOT NULL
                        AND person.wikidata_qid = credit.person_qid COLLATE NOCASE
                    )
                WHERE credit.credit_key = 'artist'
            ),
            {reads.PivotCtes()}
            SELECT
                {string.Join("," + Environment.NewLine + "    ", selectList)}
            FROM chosen
            LEFT JOIN works own_work ON own_work.id = chosen.WorkId
            LEFT JOIN collections own_collection ON own_collection.id = chosen.CollectionId
            {reads.PivotJoins()}
            ORDER BY CreatedAt DESC, WorkId, LibraryId;
            """;
        // Search-only columns are never shown on a card, so list pages skip their per-row lookups.
        return lean
            ? System.Text.RegularExpressions.Regex.Replace(sql, @"/\*search-only:begin\*/.*?/\*search-only:end\*/", "NULL", System.Text.RegularExpressions.RegexOptions.Singleline)
            : sql;
    }

    // ---- The display fields, chosen from one pass over the stored values (home-and-detail-speed D2) ----
    //
    // The old read looked each field up separately for every row (about 280 look-ups a row). Now the stored values of
    // every entity the chosen rows touch (file, work, show or album, edition, series) are read once into two pivots,
    // `value_pivot` (canonical_values) and `array_pivot` (canonical_value_arrays), one column per stored name, and each
    // field below is chosen from those columns in the order it always was.
    //
    // Two quiet rules of the old read are kept on purpose:
    //  * Where a field accepts several stored names, the data store gave the alphabetically first name that exists for
    //    the entity (the key order of the primary index), not the first one listed. `Value` does the same. The covers
    //    are the exception: they take the Engine's own copy (cover_url) before the provider's link (cover), in the
    //    order written (engineering/reports/stored-name-order-2026-10.md). Every other field with several names
    //    shows the same either way, because the names that coexist hold the same value.
    //  * An empty value is a value: it ends a COALESCE. Only a missing name is NULL.

    private enum Slot { Asset, Work, Root, Edition, Collection }

    /// <summary>An array field: the stored names it joins, in order of position (ties broken by name), with its separator.</summary>
    private sealed record ArrayGroup(string Name, string[] Keys, string? Separator);

    // A static field can only use fields declared above it, so the shared list of network names comes first.
    private static readonly string[] NetworkKeys = ["network", "studio", "broadcaster", "streaming_service", "platform"];

    private static readonly ArrayGroup AuthorArray = new("author", ["author", "creator", "writer"], "; ");
    private static readonly ArrayGroup ArtistArray = new("artist", ["artist", "album_artist", "performer"], "; ");
    private static readonly ArrayGroup GenreArray = new("genre", ["genre"], ";");
    private static readonly ArrayGroup DirectorArray = new("director", ["director"], "; ");
    private static readonly ArrayGroup NetworkArray = new("network", NetworkKeys, "; ");
    private static readonly ArrayGroup CountryArray = new("country_of_origin", ["country_of_origin"], "; ");
    private static readonly ArrayGroup FranchiseArray = new("franchise", ["franchise"], "; ");

    /// <summary>Only the first narrator (the lowest position), no joined list.</summary>
    private static readonly ArrayGroup NarratorFirst = new("narrator", ["narrator"], null);

    private sealed record ProjectionColumn(string Name, bool SearchOnly, Func<PivotReads, string> Build);

    /// <summary>Which stored names, groups and entities a set of columns reads, and the SQL that pivots exactly those.</summary>
    private sealed class PivotReads
    {
        private readonly SortedSet<string> _valueKeys = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _yearKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ArrayGroup> _arrayGroups = new(StringComparer.Ordinal);
        private readonly HashSet<Slot> _valueSlots = [];
        private readonly HashSet<Slot> _arraySlots = [];

        private static string ValueAlias(Slot slot) => "v" + SlotLetter(slot);

        private static string ArrayAlias(Slot slot) => "a" + SlotLetter(slot);

        private static char SlotLetter(Slot slot) => slot switch
        {
            Slot.Asset => 'a',
            Slot.Work => 'w',
            Slot.Root => 'r',
            Slot.Edition => 'e',
            _ => 'c',
        };

        private static string SlotId(Slot slot) => slot switch
        {
            Slot.Asset => "AssetId",
            Slot.Work => "WorkId",
            Slot.Root => "RootWorkId",
            Slot.Edition => "EditionId",
            _ => "CollectionId",
        };

        /// <summary>The stored value of the first of <paramref name="keys"/> that exists for the entity, in alphabetical (index) order.</summary>
        public string Value(Slot slot, params string[] keys)
        {
            var ordered = keys.ToArray();
            Array.Sort(ordered, StringComparer.Ordinal);
            return ValueInOrder(slot, ordered);
        }

        /// <summary>As <see cref="Value"/>, but the data store was told to prefer <paramref name="keys"/> in the order given.</summary>
        public string ValueInOrder(Slot slot, params string[] keys)
        {
            _valueSlots.Add(slot);
            var columns = keys.Select(key =>
            {
                _valueKeys.Add(key);
                return $"{ValueAlias(slot)}.v_{key}";
            }).ToList();
            return columns.Count == 1 ? columns[0] : $"COALESCE({string.Join(", ", columns)})";
        }

        /// <summary>The value as non-empty text (NULL when empty), taken from the first existing name in alphabetical order.</summary>
        public string Text(Slot slot, params string[] keys) => $"NULLIF(CAST({Value(slot, keys)} AS TEXT), '')";

        public string TextInOrder(Slot slot, params string[] keys) => $"NULLIF(CAST({ValueInOrder(slot, keys)} AS TEXT), '')";

        public string ArrayValue(Slot slot, ArrayGroup group)
        {
            _arraySlots.Add(slot);
            _arrayGroups[group.Name] = group;
            return $"{ArrayAlias(slot)}.a_{group.Name}";
        }

        /// <summary>The four-digit year of a stored date or year name for the entity (NULL when it does not start with four digits).</summary>
        public string Year(MediaDateSql.YearSlot slot, string key)
        {
            var asSlot = slot switch
            {
                MediaDateSql.YearSlot.Work => Slot.Work,
                MediaDateSql.YearSlot.Root => Slot.Root,
                _ => Slot.Asset,
            };
            _valueSlots.Add(asSlot);
            _yearKeys.Add(key);
            return $"{ValueAlias(asSlot)}.y_{key}";
        }

        public void Add(PivotReads other)
        {
            _valueKeys.UnionWith(other._valueKeys);
            _yearKeys.UnionWith(other._yearKeys);
            foreach (var group in other._arrayGroups)
            {
                _arrayGroups[group.Key] = group.Value;
            }

            _valueSlots.UnionWith(other._valueSlots);
            _arraySlots.UnionWith(other._arraySlots);
        }

        /// <summary>The CTEs that read the values: the entities involved, then one pivot per table. Ends with a comma.</summary>
        public string PivotCtes()
        {
            // Both pivots list their columns after the entity id, so each needs at least one (Author and Title always are).
            if (_valueKeys.Count == 0 || _arrayGroups.Count == 0)
            {
                throw new InvalidOperationException("The display projection must read at least one stored value and one stored list.");
            }

            var entities = _valueSlots.Union(_arraySlots)
                .Select(slot => $"SELECT {SlotId(slot)} AS id FROM chosen WHERE {SlotId(slot)} IS NOT NULL");
            var valueColumns = _valueKeys
                // (entity_id, key) is the primary key, so at most one row matches and MAX returns it as it is.
                .Select(key => $"MAX(cv.value) FILTER (WHERE cv.key = '{key}') AS v_{key}")
                .Concat(_yearKeys.Select(key => MediaDateSql.YearPivotAggregate("cv.key", "cv.value", key) + $" AS y_{key}"));
            var valueNames = string.Join(", ", _valueKeys.Union(_yearKeys, StringComparer.Ordinal).Select(key => $"'{key}'"));
            var arrayColumns = _arrayGroups.Values.OrderBy(group => group.Name, StringComparer.Ordinal).Select(group =>
            {
                var filter = group.Keys.Length == 1
                    ? $"cva.key = '{group.Keys[0]}'"
                    : $"cva.key IN ({string.Join(", ", group.Keys.Select(key => $"'{key}'"))})";
                var aggregate = group.Separator is null
                    // The first value by position: the old read took ORDER BY ordinal LIMIT 1.
                    ? "json_group_array(cva.value ORDER BY cva.ordinal)"
                    : $"group_concat(cva.value, '{group.Separator}' ORDER BY cva.ordinal, cva.key)";
                return group.Separator is null
                    ? $"({aggregate} FILTER (WHERE {filter})) ->> 0 AS a_{group.Name}"
                    : $"{aggregate} FILTER (WHERE {filter}) AS a_{group.Name}";
            });
            var arrayNames = string.Join(", ", _arrayGroups.Values.SelectMany(group => group.Keys).Distinct(StringComparer.Ordinal).Select(key => $"'{key}'"));

            // CROSS JOIN keeps the order written: the chosen entities first, then each one's own rows by the primary key
            // (left to itself the data store may scan the whole table instead, which grows with the library). Unary plus
            // on the key stops it probing the index once per name per entity: it reads the entity's short run of rows
            // and tests each name against the list.
            return $"""
                chosen_entities AS MATERIALIZED (
                    {string.Join(Environment.NewLine + "        UNION" + Environment.NewLine + "        ", entities)}
                ),
                value_pivot AS MATERIALIZED (
                    SELECT cv.entity_id,
                           {string.Join("," + Environment.NewLine + "           ", valueColumns)}
                    FROM chosen_entities ce
                    CROSS JOIN canonical_values cv ON cv.entity_id = ce.id
                    WHERE +cv.key IN ({valueNames})
                    GROUP BY cv.entity_id
                ),
                array_pivot AS MATERIALIZED (
                    SELECT cva.entity_id,
                           {string.Join("," + Environment.NewLine + "           ", arrayColumns)}
                    FROM chosen_entities ce
                    CROSS JOIN canonical_value_arrays cva ON cva.entity_id = ce.id
                    WHERE +cva.key IN ({arrayNames})
                    GROUP BY cva.entity_id
                )
                """;
        }

        /// <summary>The joins of <c>chosen</c> to the pivots, one per entity kind the columns read.</summary>
        public string PivotJoins()
        {
            var values = _valueSlots.OrderBy(slot => slot)
                .Select(slot => $"LEFT JOIN value_pivot {ValueAlias(slot)} ON {ValueAlias(slot)}.entity_id = chosen.{SlotId(slot)}");
            var arrays = _arraySlots.OrderBy(slot => slot)
                .Select(slot => $"LEFT JOIN array_pivot {ArrayAlias(slot)} ON {ArrayAlias(slot)}.entity_id = chosen.{SlotId(slot)}");
            return string.Join(Environment.NewLine, values.Concat(arrays));
        }
    }

    /// <summary>The columns of <see cref="DisplayWorkRow"/> in the order the read returns them, each with how it is chosen.</summary>
    private static IReadOnlyList<ProjectionColumn> ProjectionColumns()
    {
        var visibleEditionAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("editionAsset.file_path_root");
        var updatingPredicate = IngestionAvailability.UpdatingWorkPredicate("WorkId");

        // The entities a field is read from, in the order they are tried (the first that has a value wins).
        Slot[] asset = [Slot.Asset];
        Slot[] root = [Slot.Root];
        Slot[] collection = [Slot.Collection];
        Slot[] assetWork = [Slot.Asset, Slot.Work];
        Slot[] assetWorkRoot = [Slot.Asset, Slot.Work, Slot.Root];
        Slot[] workRootAsset = [Slot.Work, Slot.Root, Slot.Asset];
        Slot[] rootWorkAsset = [Slot.Root, Slot.Work, Slot.Asset];

        static string FirstOf(IEnumerable<string> parts)
        {
            var list = parts.ToList();
            return list.Count == 1 ? list[0] : $"COALESCE({string.Join(", ", list)})";
        }

        // The user's own title, tagline and so on, edited on the detail page (the work is joined once per row).
        static string Override(string field) => $"NULLIF(TRIM(json_extract(own_work.display_overrides_json, '$.{field}')), '')";

        static ProjectionColumn Plain(string name) => new(name, false, _ => name);
        static ProjectionColumn Column(string name, Func<PivotReads, string> build) => new(name, false, build);
        static ProjectionColumn SearchOnly(string name, Func<PivotReads, string> build) => new(name, true, build);
        static ProjectionColumn From(string name, Slot[] slots, params string[] keys) =>
            Column(name, r => FirstOf(slots.Select(slot => r.Value(slot, keys))));
        static ProjectionColumn FromInOrder(string name, Slot[] slots, params string[] keys) =>
            Column(name, r => FirstOf(slots.Select(slot => r.ValueInOrder(slot, keys))));
        static ProjectionColumn ArrayFrom(string name, Slot[] slots, ArrayGroup group) =>
            Column(name, r => FirstOf(slots.Select(slot => r.ArrayValue(slot, group))));
        // Artwork comes in three more sizes, each with its own stored names.
        static IEnumerable<ProjectionColumn> Sizes(string prefix, Slot[] slots, Func<string, string[]> keysFor) =>
            new[] { ("Small", "_s"), ("Medium", "_m"), ("Large", "_l") }
                .Select(size => From($"{prefix}{size.Item1}Url", slots, keysFor(size.Item2)));
        // Searching also looks at the edition and at every stored spelling of the field, arrays first.
        static ProjectionColumn SearchAll(string name, ArrayGroup group, params string[] keys) =>
            SearchOnly(name, r => FirstOf(
                new[] { Slot.Work, Slot.Edition, Slot.Asset, Slot.Root }
                    .SelectMany(slot => new[] { r.ArrayValue(slot, group), r.Value(slot, keys) })));

        var columns = new List<ProjectionColumn>
        {
            Plain("LibraryId"),
            Plain("WorkId"),
            Plain("CollectionId"),
            Plain("MediaType"),
            Plain("WorkKind"),
            Plain("RootWorkId"),
            From("EpisodeStillUrl", assetWork, "episode_still_url", "still_url"),
        };
        columns.AddRange(Sizes("EpisodeStill", assetWork, suffix => [$"episode_still_url{suffix}", $"still_url{suffix}"]));
        columns.AddRange(
        [
            Plain("EditionId"),
            Plain("AssetId"),
            Column("IsUpdatingDetails", _ => $"CASE WHEN {updatingPredicate} THEN 1 ELSE 0 END"),
            Column("IdentityQid", r => FirstOf(
            [
                "NULLIF(TRIM(own_work.wikidata_qid), '')",
                $"NULLIF(TRIM({r.Value(Slot.Work, "wikidata_qid")}), '')",
                $"NULLIF(TRIM({r.Value(Slot.Root, "wikidata_qid")}), '')",
                $"NULLIF(TRIM({r.Value(Slot.Asset, "wikidata_qid")}), '')",
            ])),
            Column("CreatedAt", _ => "COALESCE(CreatedAt, CURRENT_TIMESTAMP)"),
            Column("Title", r => FirstOf(
            [
                Override("title"),
                r.Value(Slot.Asset, "issue_title"),
                r.Value(Slot.Work, "issue_title"),
                r.Value(Slot.Asset, "episode_title"),
                r.Value(Slot.Work, "episode_title"),
                $"CASE WHEN MediaType = 'Books' THEN {r.Value(Slot.Work, "title")} END",
                r.Value(Slot.Asset, "title"),
                r.Value(Slot.Root, "title"),
                "'Untitled'",
            ])),
            Column("SortTitle", _ => Override("sort_title")),
            Column("Tagline", r => FirstOf(
            [
                Override("tagline"),
                r.Text(Slot.Work, "tagline"),
                r.Text(Slot.Root, "tagline"),
                r.Text(Slot.Asset, "tagline"),
            ])),
            Column("Description", r => FirstOf(
            [
                Override("description"),
                r.TextInOrder(Slot.Work, "episode_description", "short_description"),
                $"CASE WHEN MediaType <> 'TV' THEN {r.Text(Slot.Root, "short_description")} END",
                r.TextInOrder(Slot.Asset, "episode_description", "short_description"),
            ])),
            Column("RootDescription", r => r.Text(Slot.Root, "short_description")),
            ArrayFrom("Author", workRootAsset, AuthorArray),
            ArrayFrom("Artist", workRootAsset, ArtistArray),
            Column("ArtistPersonId", _ => """
                (SELECT primary_credit.person_id
                 FROM canonical_artist_credits primary_credit
                 WHERE primary_credit.media_asset_id = AssetId
                   AND primary_credit.identity_rank = 1
                 LIMIT 1)
                """),
            Column("ArtistPersonName", _ => """
                (SELECT primary_credit.person_name
                 FROM canonical_artist_credits primary_credit
                 WHERE primary_credit.media_asset_id = AssetId
                   AND primary_credit.identity_rank = 1
                 LIMIT 1)
                """),
            From("Album", workRootAsset, "album"),
            Column("Year", r => MediaDateSql.DisplayOriginalYearFromPivots(
                "MediaType",
                r.Year,
                MediaDateSql.RelatedBookYearLookup("related_book_year", "WorkId", "AssetId"))),
            From("SeriesEndYear", rootWorkAsset, "series_end_year"),
            From("ContentRating", workRootAsset, "content_rating", "certification"),
            From("Runtime", workRootAsset, "runtime"),
            From("Duration", workRootAsset, "duration", "duration_sec", "duration_seconds"),
            From("PageCount", workRootAsset, "page_count"),
            From("Rating", rootWorkAsset, "rating"),
            Column("Genre", r => FirstOf(
            [
                Override("genre"),
                r.ArrayValue(Slot.Work, GenreArray),
                r.ArrayValue(Slot.Root, GenreArray),
                r.ArrayValue(Slot.Asset, GenreArray),
            ])),
            From("Series", workRootAsset, "series"),
            Column("SeriesPosition", r => FirstOf(
            [
                r.Value(Slot.Asset, "issue_number"),
                r.Value(Slot.Work, "issue_number"),
                r.Value(Slot.Asset, "series_position"),
            ])),
            Column("CollectionTitle", _ => "own_collection.display_name"),
            Column("CollectionDescription", _ => "own_collection.description"),
            Column("CollectionType", _ => "own_collection.collection_type"),
            Column("CollectionGroupByField", _ => "own_collection.group_by_field"),
            Column("CollectionManifestTotalCount", r => $"""
                COALESCE(
                    CASE WHEN CAST({r.Value(Slot.Collection, "sequence_total")} AS INTEGER) > 0
                          AND {r.Value(Slot.Collection, "sequence_total_scope")} = 'MainSequence'
                         THEN CAST({r.Value(Slot.Collection, "sequence_total")} AS INTEGER) END,
                    (
                        SELECT MAX(COALESCE(
                            CAST(json_extract(api_metadata_json, '$.expectedTotal') AS INTEGER),
                            CAST(json_extract(api_metadata_json, '$.expected_total') AS INTEGER)))
                        FROM series_manifest_hydrations
                        WHERE collection_id = CollectionId
                          AND json_extract(api_metadata_json, '$.completeness') = 'Complete'
                    )
                )
                """),
            ArrayFrom("Narrator", workRootAsset, NarratorFirst),
            From("Publisher", workRootAsset, "publisher", "imprint"),
            SearchOnly("SearchPublisher", _ => $"""
                (SELECT group_concat(value, '; ')
                 FROM (
                    SELECT DISTINCT value
                    FROM (
                        SELECT value FROM canonical_value_arrays
                         WHERE entity_id IN (WorkId, RootWorkId, AssetId) AND key IN ('publisher', 'imprint')
                        UNION ALL
                        SELECT value FROM canonical_values
                         WHERE entity_id IN (WorkId, RootWorkId, AssetId) AND key IN ('publisher', 'imprint')
                        UNION ALL
                        SELECT editionPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_value_arrays editionPublisher
                                  ON editionPublisher.entity_id=edition.id
                                 AND editionPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                        UNION ALL
                        SELECT editionPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_values editionPublisher
                                  ON editionPublisher.entity_id=edition.id
                                 AND editionPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                        UNION ALL
                        SELECT assetPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_value_arrays assetPublisher
                                  ON assetPublisher.entity_id=editionAsset.id
                                 AND assetPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                        UNION ALL
                        SELECT assetPublisher.value
                          FROM editions edition
                          INNER JOIN media_assets editionAsset ON editionAsset.edition_id=edition.id
                          INNER JOIN canonical_values assetPublisher
                                  ON assetPublisher.entity_id=editionAsset.id
                                 AND assetPublisher.key IN ('publisher', 'imprint')
                         WHERE edition.work_id=WorkId
                           AND editionAsset.library_id=LibraryId
                           AND editionAsset.status='Normal' AND editionAsset.is_orphaned=0
                           AND {visibleEditionAssetPredicate}
                    ) publisherValues
                    WHERE NULLIF(TRIM(value), '') IS NOT NULL
                    ORDER BY value COLLATE NOCASE
                 ))
                """),
            ArrayFrom("Director", workRootAsset, DirectorArray),
            From("Network", rootWorkAsset, NetworkKeys),
            SearchAll("SearchNetwork", NetworkArray, NetworkKeys),
            SearchAll("CountryOfOrigin", CountryArray, "country_of_origin"),
            SearchAll("Franchise", FranchiseArray, "franchise"),
            From("Source", rootWorkAsset, "source_service", "source_platform"),
            From("Quality", assetWorkRoot, "quality", "video_quality", "resolution", "video_resolution", "video_resolution_label"),
            Column("ShowName", r => FirstOf([r.Value(Slot.Root, "show_name"), r.Value(Slot.Root, "title")])),
            From("SeasonNumber", asset, "season_number"),
            From("EpisodeNumber", asset, "episode_number"),
            From("TrackNumber", asset, "track_number"),
            FromInOrder("CoverUrl", assetWorkRoot, "cover_url", "cover", "poster_url", "poster", "episode_still_url", "episode_still", "still_url", "still"),
        ]);
        columns.AddRange(Sizes("Cover", assetWorkRoot, suffix => [$"cover_url{suffix}", $"poster_url{suffix}", $"episode_still_url{suffix}", $"still_url{suffix}"]));
        columns.Add(From("SquareUrl", assetWorkRoot, "square_url", "square"));
        columns.AddRange(Sizes("Square", assetWorkRoot, suffix => [$"square_url{suffix}"]));
        columns.Add(From("BannerUrl", assetWorkRoot, "banner_url", "banner", "episode_still_url", "episode_still", "still_url", "still"));
        columns.AddRange(Sizes("Banner", assetWorkRoot, suffix => [$"banner_url{suffix}", $"episode_still_url{suffix}", $"still_url{suffix}"]));
        columns.Add(From("BackgroundUrl", assetWorkRoot, "background_url", "background", "episode_still_url", "episode_still", "still_url", "still"));
        columns.AddRange(Sizes("Background", assetWorkRoot, suffix => [$"background_url{suffix}", $"episode_still_url{suffix}", $"still_url{suffix}"]));
        columns.Add(From("LogoUrl", assetWorkRoot, "logo_url", "logo"));
        foreach (var kind in new[] { "cover", "square", "banner", "background", "logo" })
        {
            columns.Add(From(char.ToUpperInvariant(kind[0]) + kind[1..] + "State", assetWorkRoot, kind + "_state"));
        }

        columns.AddRange(
        [
            FromInOrder("CollectionCoverUrl", collection, "cover_url", "cover", "poster_url", "poster"),
            From("CollectionSquareUrl", collection, "square_url", "square"),
            From("CollectionBannerUrl", collection, "banner_url", "banner"),
            From("CollectionBackgroundUrl", collection, "background_url", "background", "hero_url", "hero"),
            From("CollectionLogoUrl", collection, "logo_url", "logo"),
            From("CollectionAccentColor", collection, "artwork_accent_hex"),
            FromInOrder("RootCoverUrl", root, "cover_url", "cover", "poster_url", "poster"),
            From("RootCoverSmallUrl", root, "cover_url_s", "poster_url_s"),
            From("RootCoverMediumUrl", root, "cover_url_m", "poster_url_m"),
            From("RootCoverLargeUrl", root, "cover_url_l", "poster_url_l"),
            From("RootSquareUrl", root, "square_url", "square"),
            From("RootSquareSmallUrl", root, "square_url_s"),
            From("RootSquareMediumUrl", root, "square_url_m"),
            From("RootSquareLargeUrl", root, "square_url_l"),
            From("RootBannerUrl", root, "banner_url", "banner"),
            From("RootBannerSmallUrl", root, "banner_url_s"),
            From("RootBannerMediumUrl", root, "banner_url_m"),
            From("RootBannerLargeUrl", root, "banner_url_l"),
            From("RootBackgroundUrl", root, "background_url", "background", "hero_url", "hero"),
            From("RootBackgroundSmallUrl", root, "background_url_s", "hero_url_s"),
            From("RootBackgroundMediumUrl", root, "background_url_m", "hero_url_m"),
            From("RootBackgroundLargeUrl", root, "background_url_l", "hero_url_l"),
            From("RootLogoUrl", root, "logo_url", "logo"),
            From("RootCoverState", root, "cover_state"),
            From("RootSquareState", root, "square_state"),
            From("RootBannerState", root, "banner_state"),
            From("RootBackgroundState", root, "background_state"),
            From("RootLogoState", root, "logo_state"),
            From("RootCoverWidthPx", root, "cover_width_px"),
            From("RootCoverHeightPx", root, "cover_height_px"),
            From("RootSquareWidthPx", root, "square_width_px"),
            From("RootSquareHeightPx", root, "square_height_px"),
            From("RootBannerWidthPx", root, "banner_width_px"),
            From("RootBannerHeightPx", root, "banner_height_px"),
            From("RootBackgroundWidthPx", root, "background_width_px"),
            From("RootBackgroundHeightPx", root, "background_height_px"),
            From("RootAccentColor", root, "artwork_accent_hex"),
            From("CoverWidthPx", assetWorkRoot, "cover_width_px", "episode_still_width_px"),
            From("CoverHeightPx", assetWorkRoot, "cover_height_px", "episode_still_height_px"),
            From("SquareWidthPx", assetWorkRoot, "square_width_px"),
            From("SquareHeightPx", assetWorkRoot, "square_height_px"),
            From("BannerWidthPx", assetWorkRoot, "banner_width_px", "episode_still_width_px"),
            From("BannerHeightPx", assetWorkRoot, "banner_height_px", "episode_still_height_px"),
            From("BackgroundWidthPx", assetWorkRoot, "background_width_px", "episode_still_width_px"),
            From("BackgroundHeightPx", assetWorkRoot, "background_height_px", "episode_still_height_px"),
            From("AccentColor", root, "artwork_accent_hex"),
        ]);
        return columns;
    }

    /// <summary>
    /// The files that credit the person <paramref name="personId"/>, by name (ignoring case) or by Wikidata id, with credits
    /// worked out the way the shared credit list does, as a JSON array of hex ids for the main read's
    /// <c>@personAssetIds</c>. Nothing is read beyond one look-up of the person when <paramref name="personId"/> is not a
    /// person (a work, show or series page): the array is empty.
    /// It stays proportional to the library in two steps. First the candidate files: every file whose show or album,
    /// work or own id carries a stored credit for a person who matches (a superset, found by scanning the credit rows
    /// once). Then credits are built for just those files and the match is checked on them. This is a separate statement
    /// because SQLite copies a CTE at every place it is named, so this chain inside the main read would be copied
    /// once per use of the chosen rows. The two statements are not one snapshot: a credit added between them shows on
    /// the next read.
    /// </summary>
    internal static async Task<string> LoadPersonAssetsAsync(System.Data.IDbConnection conn, Guid personId, CancellationToken ct)
    {
        var isPerson = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM persons WHERE id = @personId;", new { personId }, cancellationToken: ct));
        if (isPerson is null)
        {
            return "[]";
        }

        var ids = await conn.QueryAsync<string>(new CommandDefinition(
            $"WITH {PersonAssetsCtes()} SELECT hex(asset_id) FROM detail_person_assets;",
            new { detailId = personId },
            cancellationToken: ct));
        return System.Text.Json.JsonSerializer.Serialize(ids);
    }

    private static string PersonAssetsCtes()
    {
        var keys = PersonCreditSql.CreditKeys;
        var scopedCredits = PersonCreditSql.ScopedCreditsCte(
            "detail_person_credits",
            "SELECT asset_id FROM detail_candidate_assets",
            "dp_");
        return $"""
            detail_person AS MATERIALIZED (
                SELECT person.id, person.name, person.wikidata_qid FROM persons person WHERE person.id = @detailId
            ),
            -- Every person a credit could resolve to that the page's person matches (same name, or same Wikidata id).
            detail_match_persons AS MATERIALIZED (
                SELECT other.id, other.name, other.wikidata_qid
                FROM detail_person person
                INNER JOIN persons other ON other.name = person.name COLLATE NOCASE
                UNION
                SELECT other.id, other.name, other.wikidata_qid
                FROM detail_person person
                INNER JOIN persons other ON other.wikidata_qid = person.wikidata_qid
                WHERE NULLIF(person.wikidata_qid, '') IS NOT NULL
            ),
            detail_credit_entities AS MATERIALIZED (
                SELECT cva.entity_id
                FROM detail_match_persons match_person
                CROSS JOIN canonical_value_arrays cva
                WHERE cva.key IN ({keys})
                  AND (TRIM(cva.value) = match_person.name COLLATE NOCASE
                       OR NULLIF(TRIM(cva.value_qid), '') = match_person.wikidata_qid COLLATE NOCASE)
                UNION
                SELECT mc.entity_id
                FROM detail_match_persons match_person
                CROSS JOIN metadata_claims mc
                WHERE mc.claim_key IN ({keys})
                  AND TRIM(mc.claim_value) = match_person.name COLLATE NOCASE
                UNION
                SELECT cv.entity_id
                FROM detail_match_persons match_person
                CROSS JOIN canonical_values cv
                WHERE cv.key IN ({keys})
                  AND TRIM(cv.value) = match_person.name COLLATE NOCASE
            ),
            detail_candidate_assets AS MATERIALIZED (
                -- readiness: ids of files only; the main read applies the Ready rule to every row it returns
                SELECT ma.id AS asset_id
                FROM detail_credit_entities entity
                INNER JOIN media_assets ma ON ma.id = entity.entity_id
                UNION
                SELECT ma.id
                FROM detail_credit_entities entity
                INNER JOIN editions e ON e.work_id = entity.entity_id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                UNION
                SELECT ma.id
                FROM detail_credit_entities entity
                INNER JOIN works w ON w.parent_work_id = entity.entity_id
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                UNION
                SELECT ma.id
                FROM detail_credit_entities entity
                INNER JOIN works p ON p.parent_work_id = entity.entity_id
                INNER JOIN works w ON w.parent_work_id = p.id
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
            ),
            {scopedCredits},
            detail_person_assets AS MATERIALIZED (
                SELECT DISTINCT credit.media_asset_id AS asset_id
                FROM detail_person_credits credit
                INNER JOIN detail_person person
                    ON credit.person_name = person.name COLLATE NOCASE
                    OR (NULLIF(credit.person_qid, '') IS NOT NULL AND credit.person_qid = person.wikidata_qid)
            )
            """;
    }

    /// <summary>
    /// Light listing of every visible (work, library) pair with the timestamp the recent shelf sorts it by.
    /// A null <see cref="RecentCandidate.AddedAt"/> means the full projection derives the time from metadata claims.
    /// </summary>
    internal async Task<IReadOnlyList<RecentCandidate>> LoadRecentCandidatesAsync(CancellationToken ct)
    {
        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        using var conn = _db.CreateConnection();
        var sql = $"""
            SELECT
                w.id AS WorkId,
                ma.library_id AS LibraryId,
                w.collection_id AS CollectionId,
                w.media_type AS MediaType,
                CASE
                    WHEN w.media_type = 'Music' THEN COALESCE(p.id, w.id)
                    ELSE COALESCE(gp.id, p.id, w.id)
                END AS RootWorkId,
                MAX(CASE WHEN own.work_id = w.id THEN ma.presented_at END) AS AddedAt
            FROM works w
            INNER JOIN work_owned_assets woa ON woa.work_id = w.id
            INNER JOIN media_assets ma ON ma.id = woa.asset_id
            INNER JOIN editions own ON own.id = ma.edition_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            LEFT JOIN works gp ON gp.id = p.parent_work_id
            WHERE w.work_kind != 'parent'
              AND ma.status = 'Normal' AND ma.is_orphaned = 0
              AND {visibleWorkPredicate}
              AND {visibleAssetPredicate}
            GROUP BY w.id, ma.library_id;
            """;
        return (await conn.QueryAsync<RecentCandidate>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Light rows (no artwork or text lookups) for every episode of the given shows: just what a show card reports
    /// about the whole show, plus the rating the profile limit is checked against. One row per episode file.
    /// </summary>
    internal async Task<IReadOnlyList<DisplayWorkRow>> LoadTvEpisodeSkeletonAsync(IReadOnlyCollection<Guid> rootIds, CancellationToken ct)
    {
        if (rootIds.Count == 0)
        {
            return [];
        }

        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        // This listing only reads TV rows, which never borrow a book's year, so the audiobook look-up is left out.
        var displayYearSql = MediaDateSql.DisplayOriginalYear("WorkId", "RootWorkId", "AssetId", "MediaType", relatedBookYearExpression: "NULL");
        var sql = $"""
            WITH show_assets AS (
                SELECT
                    w.id AS WorkId,
                    ma.library_id AS LibraryId,
                    w.collection_id AS CollectionId,
                    w.media_type AS MediaType,
                    COALESCE(gp.id, p.id, w.id) AS RootWorkId,
                    ma.id AS AssetId
                FROM works w
                INNER JOIN work_owned_assets woa ON woa.work_id = w.id
                INNER JOIN editions e ON e.id = woa.edition_id
                INNER JOIN media_assets ma ON ma.id = woa.asset_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.work_kind != 'parent'
                  AND ma.status = 'Normal' AND ma.is_orphaned = 0
                  AND {visibleWorkPredicate}
                  AND {visibleAssetPredicate}
                  AND COALESCE(gp.id, p.id, w.id) IN @rootIds
            ),
            asset_dates AS (
                SELECT entity_id, MIN(claimed_at) AS claimed_at
                FROM metadata_claims WHERE entity_id IN (SELECT AssetId FROM show_assets) GROUP BY entity_id
            ),
            episode_assets AS (
                -- Same choice of file and same added time as the full projection (ranked_assets).
                SELECT
                    sa.*,
                    MIN(mc.claimed_at) OVER (PARTITION BY sa.WorkId, sa.LibraryId) AS CreatedAt,
                    ROW_NUMBER() OVER (
                        PARTITION BY sa.WorkId, sa.LibraryId
                        ORDER BY CASE WHEN mc.claimed_at IS NULL THEN 1 ELSE 0 END, mc.claimed_at ASC, sa.AssetId
                    ) AS AssetRank
                FROM show_assets sa
                LEFT JOIN asset_dates mc ON mc.entity_id = sa.AssetId
            )
            SELECT
                LibraryId,
                WorkId,
                CollectionId,
                MediaType,
                RootWorkId,
                AssetId,
                COALESCE(CreatedAt, CURRENT_TIMESTAMP) AS CreatedAt,
                CASE WHEN {IngestionAvailability.UpdatingWorkPredicate("WorkId")} THEN 1 ELSE 0 END AS IsUpdatingDetails,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key IN ('content_rating', 'certification') LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key IN ('content_rating', 'certification') LIMIT 1)
                ) AS ContentRating,
                {displayYearSql} AS Year,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = RootWorkId AND key = 'series_end_year' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = WorkId AND key = 'series_end_year' LIMIT 1),
                    (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'series_end_year' LIMIT 1)
                ) AS SeriesEndYear,
                (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'season_number' LIMIT 1) AS SeasonNumber,
                (SELECT value FROM canonical_values WHERE entity_id = AssetId AND key = 'episode_number' LIMIT 1) AS EpisodeNumber
            FROM episode_assets
            WHERE AssetRank = 1
            ORDER BY WorkId, LibraryId, AssetId;
            """;
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<DisplayWorkRow>(new CommandDefinition(sql, new { rootIds = rootIds.Select(GuidSql.ToBlob).ToArray() }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadAsync(CancellationToken ct, int limit = int.MaxValue, Guid? detailId = null, DisplayRecentGroupFilter? groups = null, bool lean = false)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var conn = _db.CreateConnection();
        var sql = BuildSql(detailId.HasValue, lean, groups is not null);
        var personAssetIds = detailId.HasValue ? await LoadPersonAssetsAsync(conn, detailId.Value, ct) : "[]";

        var rows = (await conn.QueryAsync<DisplayWorkRow>(new CommandDefinition(sql, new { limit, detailId, personAssetIds, rootIds = (groups?.RootIds ?? []).Select(GuidSql.ToBlob).ToArray(), workIds = (groups?.WorkIds ?? []).Select(GuidSql.ToBlob).ToArray() }, cancellationToken: ct))).ToList();
        var pseudonymNames = conn.Query<string>(new CommandDefinition(
                "SELECT name FROM persons WHERE is_pseudonym = 1 AND NULLIF(TRIM(name), '') IS NOT NULL;",
                cancellationToken: ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            row.Author = NormalizeContributorList(row.Author, pseudonymNames, preferCollectivePseudonym: true);
            row.Artist = NormalizeContributorList(row.Artist, pseudonymNames, preferCollectivePseudonym: false);
            row.Director = NormalizeContributorList(row.Director, pseudonymNames, preferCollectivePseudonym: false);
            row.CoverUrl = DisplayArtworkUrlResolver.Resolve(row.CoverUrl, row.AssetId, "cover", row.CoverState);
            row.SquareUrl = DisplayArtworkUrlResolver.Resolve(row.SquareUrl, row.AssetId, "square", row.SquareState);
            row.BannerUrl = DisplayArtworkUrlResolver.Resolve(row.BannerUrl, row.AssetId, "banner", row.BannerState);
            row.BackgroundUrl = DisplayArtworkUrlResolver.Resolve(row.BackgroundUrl, row.AssetId, "background", row.BackgroundState);
            row.LogoUrl = DisplayArtworkUrlResolver.Resolve(row.LogoUrl, row.AssetId, "logo", row.LogoState);
            row.RootCoverUrl = DisplayArtworkUrlResolver.Resolve(row.RootCoverUrl, row.AssetId, "cover", row.RootCoverState);
            row.RootSquareUrl = DisplayArtworkUrlResolver.Resolve(row.RootSquareUrl, row.AssetId, "square", row.RootSquareState);
            row.RootBannerUrl = DisplayArtworkUrlResolver.Resolve(row.RootBannerUrl, row.AssetId, "banner", row.RootBannerState);
            row.RootBackgroundUrl = DisplayArtworkUrlResolver.Resolve(row.RootBackgroundUrl, row.AssetId, "background", row.RootBackgroundState);
            row.RootLogoUrl = DisplayArtworkUrlResolver.Resolve(row.RootLogoUrl, row.AssetId, "logo", row.RootLogoState);
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        PerformanceMetrics.DatabaseReadDurationMs.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", "display.works"));
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            _logger?.LogWarning(
                "Database read {Operation} took {ElapsedMs} ms with limit {Limit}, returned {ItemCount}",
                "display.works",
                elapsed.TotalMilliseconds,
                limit,
                rows.Count);
        }
        else
        {
            _logger?.LogDebug(
                "Database read {Operation} took {ElapsedMs} ms with limit {Limit}, returned {ItemCount}",
                "display.works",
                elapsed.TotalMilliseconds,
                limit,
                rows.Count);
        }

        return rows;
    }

    private static string? NormalizeContributorList(
        string? value,
        IReadOnlySet<string> pseudonymNames,
        bool preferCollectivePseudonym)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var names = value
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (preferCollectivePseudonym)
        {
            var pseudonyms = names.Where(pseudonymNames.Contains).ToList();
            if (pseudonyms.Count > 0)
            {
                names = pseudonyms;
            }
        }

        return string.Join("; ", names);
    }
}
