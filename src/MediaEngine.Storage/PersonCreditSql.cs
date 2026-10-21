using System.Text.RegularExpressions;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite building block that works out person credits for a given set of files only.
/// It produces exactly the rows of the <c>primary_person_media_credits</c> view
/// (<c>schema.snapshot.sql</c>), so a read that returns a handful of items does not
/// pay for credits across the whole library. Any rule change must be made in the view
/// and here together; <c>PersonCreditSqlTests</c> fails if the two ever differ.
/// </summary>
public static partial class PersonCreditSql
{
    /// <summary>The credit keys, as a comma-separated list of SQL string literals.</summary>
    public const string CreditKeys =
        "'author', 'narrator', 'director', 'artist', 'performer', " +
        "'composer', 'illustrator', 'screenwriter', 'cast_member'";

    /// <summary>
    /// Returns a chain of CTE definitions (without the leading <c>WITH</c>) whose last CTE is named
    /// <paramref name="resultName"/> and has the view's columns:
    /// <c>media_asset_id, person_id, person_name, person_qid, credit_key, billing_order, role</c>.
    /// </summary>
    /// <param name="resultName">Name of the final CTE the caller selects from.</param>
    /// <param name="assetIdsSelect">
    /// A <c>SELECT</c> returning exactly one column holding the media asset ids to work out credits for
    /// (for example <c>SELECT AssetId FROM chosen</c> or <c>SELECT value FROM json_each(@assetIds)</c>).
    /// It may repeat ids. It is placed inside <c>ma.id IN (...)</c>, so it carries no trailing semicolon.
    /// </param>
    /// <param name="prefix">
    /// Prepended to every internal CTE name, so the block can appear more than once in one statement
    /// (use a different prefix each time). Letters, digits and underscores only.
    /// </param>
    /// <remarks>
    /// The files are narrowed first, then every look-up into <c>canonical_value_arrays</c>,
    /// <c>metadata_claims</c> and <c>canonical_values</c> is by <c>entity_id</c> (the root work, the work and
    /// the file), so SQLite searches those tables by index instead of scanning them. The caller joins the
    /// block into a statement that already has its own <c>WITH</c>, separating the definitions with a comma.
    /// <para>
    /// SQLite copies a CTE's definition at every place the CTE is named, so the number of references inside this
    /// block decides how often <paramref name="assetIdsSelect"/> (often a large query) is duplicated and how long the
    /// statement takes to prepare. Each internal CTE is therefore named as few times as possible: the input once (by
    /// the file scope), the file scope once, the stored credits three times (arrays, claims, scalars), and every later
    /// step once. Precedence between the three sources and between the show/album, work and file is decided with a
    /// window over (file, credit key) rather than by joining a CTE back to itself.
    /// </para>
    /// </remarks>
    public static string ScopedCreditsCte(string resultName, string assetIdsSelect, string prefix)
    {
        ValidateIdentifier(resultName, nameof(resultName));
        ValidateIdentifier(prefix, nameof(prefix));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetIdsSelect);

        var scope = prefix + "asset_scope";
        var entities = prefix + "scope_entities";
        var candidates = prefix + "candidates";
        var selectedCredits = prefix + "selected_credits";
        var creditPersons = prefix + "credit_persons";

        return $"""
            {scope} AS MATERIALIZED (
                SELECT ma.id AS media_asset_id,
                       w.id AS work_id,
                       COALESCE(gp.id, p.id, w.id) AS root_work_id
                FROM media_assets ma
                INNER JOIN editions e ON e.id = ma.edition_id
                INNER JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE ma.id IN ({assetIdsSelect})
            ),
            {entities} AS MATERIALIZED (
                -- 0 = show or album, 1 = work (when different), 2 = file (when different from both): the first match wins.
                SELECT scope.media_asset_id,
                       CASE level.value
                           WHEN 0 THEN scope.root_work_id
                           WHEN 1 THEN scope.work_id
                           ELSE scope.media_asset_id
                       END AS entity_id,
                       level.value AS scope_priority
                FROM {scope} scope
                CROSS JOIN (SELECT 0 AS value UNION ALL SELECT 1 UNION ALL SELECT 2) level
                WHERE level.value = 0
                   OR (level.value = 1 AND scope.work_id != scope.root_work_id)
                   OR (level.value = 2
                       AND scope.media_asset_id != scope.root_work_id
                       AND scope.media_asset_id != scope.work_id)
            ),
            {candidates} AS MATERIALIZED (
                -- source_rank: stored credit arrays beat provider claims, which beat single stored values.
                SELECT scope.media_asset_id,
                       cva.key AS credit_key,
                       cva.ordinal AS billing_order,
                       TRIM(cva.value) AS person_name,
                       NULLIF(TRIM(cva.value_qid), '') AS person_qid,
                       0 AS source_rank,
                       scope.scope_priority
                FROM {entities} scope
                CROSS JOIN canonical_value_arrays cva
                    ON cva.entity_id = scope.entity_id
                WHERE cva.key IN ({CreditKeys})
                  AND NULLIF(TRIM(cva.value), '') IS NOT NULL
                UNION ALL
                SELECT scope.media_asset_id,
                       mc.claim_key,
                       mc.rowid,
                       TRIM(mc.claim_value),
                       NULL,
                       1,
                       scope.scope_priority
                FROM {entities} scope
                CROSS JOIN metadata_claims mc
                    ON mc.entity_id = scope.entity_id
                WHERE mc.claim_key IN ({CreditKeys})
                  AND NULLIF(TRIM(mc.claim_value), '') IS NOT NULL
                UNION ALL
                SELECT scope.media_asset_id,
                       cv.key,
                       0,
                       TRIM(cv.value),
                       NULL,
                       2,
                       scope.scope_priority
                FROM {entities} scope
                CROSS JOIN canonical_values cv
                    ON cv.entity_id = scope.entity_id
                WHERE cv.key IN ({CreditKeys})
                  AND NULLIF(TRIM(cv.value), '') IS NOT NULL
            ),
            {selectedCredits} AS MATERIALIZED (
                -- Per file and credit key only the best (source, level) survives; a lower source beats any level.
                SELECT ranked.media_asset_id,
                       ranked.credit_key,
                       ranked.billing_order,
                       ranked.person_name,
                       ranked.person_qid
                FROM (
                    SELECT candidate.*,
                           candidate.source_rank * 10 + candidate.scope_priority AS rank_value,
                           MIN(candidate.source_rank * 10 + candidate.scope_priority) OVER (
                               PARTITION BY candidate.media_asset_id, candidate.credit_key) AS best_rank_value
                    FROM {candidates} candidate
                ) ranked
                WHERE ranked.rank_value = ranked.best_rank_value
            ),
            {creditPersons} AS MATERIALIZED (
                -- A credit with a QID finds persons by QID; one without finds them by name ignoring case.
                -- pseudonym_min and pseudonym_max are the lowest and highest id of the pseudonym persons credited
                -- for the same file and key; they stand in for "another person credited here is a pseudonym".
                SELECT credit.media_asset_id,
                       credit.credit_key,
                       credit.billing_order,
                       COALESCE(person_by_qid.id, person_by_name.id) AS person_id,
                       COALESCE(person_by_qid.name, person_by_name.name) AS person_name,
                       COALESCE(person_by_qid.wikidata_qid, person_by_name.wikidata_qid) AS person_qid,
                       MIN(CASE WHEN COALESCE(person_by_qid.is_pseudonym, person_by_name.is_pseudonym) = 1
                                THEN COALESCE(person_by_qid.id, person_by_name.id) END) OVER credit_group AS pseudonym_min,
                       MAX(CASE WHEN COALESCE(person_by_qid.is_pseudonym, person_by_name.is_pseudonym) = 1
                                THEN COALESCE(person_by_qid.id, person_by_name.id) END) OVER credit_group AS pseudonym_max
                FROM {selectedCredits} credit
                LEFT JOIN persons person_by_qid
                    ON credit.person_qid IS NOT NULL
                   AND person_by_qid.wikidata_qid = credit.person_qid COLLATE NOCASE
                LEFT JOIN persons person_by_name
                    ON credit.person_qid IS NULL
                   AND person_by_name.name = credit.person_name COLLATE NOCASE
                WHERE person_by_qid.id IS NOT NULL OR person_by_name.id IS NOT NULL
                WINDOW credit_group AS (PARTITION BY credit.media_asset_id, credit.credit_key)
            ),
            {resultName} AS (
                SELECT DISTINCT credit.media_asset_id,
                       credit.person_id,
                       credit.person_name,
                       credit.person_qid,
                       credit.credit_key,
                       credit.billing_order,
                       CASE credit.credit_key
                           WHEN 'author' THEN 'Author'
                           WHEN 'narrator' THEN 'Narrator'
                           WHEN 'director' THEN 'Director'
                           WHEN 'artist' THEN 'Artist'
                           WHEN 'performer' THEN 'Performer'
                           WHEN 'composer' THEN 'Composer'
                           WHEN 'illustrator' THEN 'Illustrator'
                           WHEN 'screenwriter' THEN 'Screenwriter'
                           WHEN 'cast_member' THEN 'Actor'
                       END AS role
                FROM {creditPersons} credit
                -- An author row is dropped when some other person credited for the same file and key is a pseudonym.
                WHERE credit.credit_key != 'author'
                   OR credit.pseudonym_min IS NULL
                   OR (credit.pseudonym_min = credit.person_id AND credit.pseudonym_max = credit.person_id)
            )
            """;
    }

    /// <summary>
    /// Returns a chain of CTE definitions (without the leading <c>WITH</c>) whose last CTE is named
    /// <paramref name="resultName"/> and has one column, <c>asset_id</c>: the files that may credit one of the persons
    /// returned by <paramref name="personsSelect"/>. It is a superset of the files the shared credit list credits to them
    /// (never fewer): every file whose show or album, work or own id carries a stored credit, provider claim or single
    /// stored value that names one of the persons (same name ignoring case and surrounding spaces, or the same Wikidata
    /// id), together with that work's other files and its children's and grandchildren's files. Credits are then worked
    /// out exactly for those files by <see cref="ScopedCreditsCte"/>, so a read that keeps a handful of rows never
    /// builds the whole library's credit list. A rule change for how a credit names a person is made here and in
    /// <see cref="ScopedCreditsCte"/> together.
    /// </summary>
    /// <param name="resultName">Name of the final CTE the caller selects from.</param>
    /// <param name="personsSelect">
    /// A <c>SELECT</c> returning the columns <c>id, name, wikidata_qid</c> for the persons to look for. It is saved once
    /// inside the block, so it may be a query; it carries no trailing semicolon.
    /// </param>
    /// <param name="prefix">Prepended to every internal CTE name (letters, digits and underscores only).</param>
    /// <remarks>
    /// Every step is <c>MATERIALIZED</c> because SQLite copies a CTE's definition at every place it is named.
    /// The stored credit rows are read once each, by the persons' names and ids.
    /// </remarks>
    public static string CandidateAssetsCte(string resultName, string personsSelect, string prefix)
    {
        ValidateIdentifier(resultName, nameof(resultName));
        ValidateIdentifier(prefix, nameof(prefix));
        ArgumentException.ThrowIfNullOrWhiteSpace(personsSelect);

        var persons = prefix + "match_persons";
        var entities = prefix + "credit_entities";

        return $"""
            {persons} AS MATERIALIZED (
                {personsSelect}
            ),
            -- Every person a credit could resolve to that the persons asked for match (same name, or same Wikidata id).
            {prefix}match_resolved AS MATERIALIZED (
                SELECT other.id, other.name, other.wikidata_qid
                FROM {persons} person
                INNER JOIN persons other ON other.name = person.name COLLATE NOCASE
                UNION
                SELECT other.id, other.name, other.wikidata_qid
                FROM {persons} person
                INNER JOIN persons other ON other.wikidata_qid = person.wikidata_qid
                WHERE NULLIF(person.wikidata_qid, '') IS NOT NULL
            ),
            {entities} AS MATERIALIZED (
                SELECT cva.entity_id
                FROM {prefix}match_resolved match_person
                CROSS JOIN canonical_value_arrays cva
                WHERE cva.key IN ({CreditKeys})
                  AND (TRIM(cva.value) = match_person.name COLLATE NOCASE
                       OR NULLIF(TRIM(cva.value_qid), '') = match_person.wikidata_qid COLLATE NOCASE)
                UNION
                SELECT mc.entity_id
                FROM {prefix}match_resolved match_person
                CROSS JOIN metadata_claims mc
                WHERE mc.claim_key IN ({CreditKeys})
                  AND TRIM(mc.claim_value) = match_person.name COLLATE NOCASE
                UNION
                SELECT cv.entity_id
                FROM {prefix}match_resolved match_person
                CROSS JOIN canonical_values cv
                WHERE cv.key IN ({CreditKeys})
                  AND TRIM(cv.value) = match_person.name COLLATE NOCASE
            ),
            {resultName} AS MATERIALIZED (
                -- readiness: ids of files only; the caller's read applies the Ready rule to every row it returns
                SELECT ma.id AS asset_id
                FROM {entities} entity
                INNER JOIN media_assets ma ON ma.id = entity.entity_id
                UNION
                SELECT ma.id
                FROM {entities} entity
                INNER JOIN editions e ON e.work_id = entity.entity_id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                UNION
                SELECT ma.id
                FROM {entities} entity
                INNER JOIN works w ON w.parent_work_id = entity.entity_id
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                UNION
                SELECT ma.id
                FROM {entities} entity
                INNER JOIN works p ON p.parent_work_id = entity.entity_id
                INNER JOIN works w ON w.parent_work_id = p.id
                INNER JOIN editions e ON e.work_id = w.id
                INNER JOIN media_assets ma ON ma.edition_id = e.id
            )
            """;
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || !IdentifierPattern().IsMatch(value))
        {
            throw new ArgumentException(
                "Must contain only letters, digits and underscores, and not start with a digit.",
                parameterName);
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
