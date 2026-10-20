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
    private const string CreditKeys =
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
    /// </remarks>
    public static string ScopedCreditsCte(string resultName, string assetIdsSelect, string prefix)
    {
        ValidateIdentifier(resultName, nameof(resultName));
        ValidateIdentifier(prefix, nameof(prefix));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetIdsSelect);

        var scope = prefix + "asset_scope";
        var entities = prefix + "scope_entities";
        var arrayCandidates = prefix + "array_candidates";
        var selectedArrays = prefix + "selected_arrays";
        var claimCandidates = prefix + "claim_candidates";
        var selectedClaims = prefix + "selected_claims";
        var scalarCandidates = prefix + "scalar_candidates";
        var selectedScalars = prefix + "selected_scalars";
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
                SELECT scope.media_asset_id, scope.root_work_id AS entity_id, 0 AS scope_priority
                FROM {scope} scope
                UNION ALL
                SELECT scope.media_asset_id, scope.work_id, 1
                FROM {scope} scope
                WHERE scope.work_id != scope.root_work_id
                UNION ALL
                SELECT scope.media_asset_id, scope.media_asset_id, 2
                FROM {scope} scope
                WHERE scope.media_asset_id != scope.root_work_id
                  AND scope.media_asset_id != scope.work_id
            ),
            {arrayCandidates} AS MATERIALIZED (
                SELECT scope.media_asset_id,
                       cva.key AS credit_key,
                       cva.ordinal AS billing_order,
                       TRIM(cva.value) AS person_name,
                       NULLIF(TRIM(cva.value_qid), '') AS person_qid,
                       scope.scope_priority
                FROM {entities} scope
                CROSS JOIN canonical_value_arrays cva
                    ON cva.entity_id = scope.entity_id
                WHERE cva.key IN ({CreditKeys})
                  AND NULLIF(TRIM(cva.value), '') IS NOT NULL
            ),
            {selectedArrays} AS MATERIALIZED (
                SELECT candidate.media_asset_id,
                       candidate.credit_key,
                       candidate.billing_order,
                       candidate.person_name,
                       candidate.person_qid
                FROM {arrayCandidates} candidate
                WHERE candidate.scope_priority = (
                    SELECT MIN(peer.scope_priority)
                    FROM {arrayCandidates} peer
                    WHERE peer.media_asset_id = candidate.media_asset_id
                      AND peer.credit_key = candidate.credit_key
                )
            ),
            {claimCandidates} AS MATERIALIZED (
                SELECT scope.media_asset_id,
                       mc.claim_key AS credit_key,
                       mc.rowid AS billing_order,
                       TRIM(mc.claim_value) AS person_name,
                       NULL AS person_qid,
                       scope.scope_priority
                FROM {entities} scope
                CROSS JOIN metadata_claims mc
                    ON mc.entity_id = scope.entity_id
                WHERE mc.claim_key IN ({CreditKeys})
                  AND NULLIF(TRIM(mc.claim_value), '') IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM {selectedArrays} array_credit
                      WHERE array_credit.media_asset_id = scope.media_asset_id
                        AND array_credit.credit_key = mc.claim_key
                  )
            ),
            {selectedClaims} AS MATERIALIZED (
                SELECT candidate.media_asset_id,
                       candidate.credit_key,
                       candidate.billing_order,
                       candidate.person_name,
                       candidate.person_qid
                FROM {claimCandidates} candidate
                WHERE candidate.scope_priority = (
                    SELECT MIN(peer.scope_priority)
                    FROM {claimCandidates} peer
                    WHERE peer.media_asset_id = candidate.media_asset_id
                      AND peer.credit_key = candidate.credit_key
                )
            ),
            {scalarCandidates} AS MATERIALIZED (
                SELECT scope.media_asset_id,
                       cv.key AS credit_key,
                       0 AS billing_order,
                       TRIM(cv.value) AS person_name,
                       NULL AS person_qid,
                       scope.scope_priority
                FROM {entities} scope
                CROSS JOIN canonical_values cv
                    ON cv.entity_id = scope.entity_id
                WHERE cv.key IN ({CreditKeys})
                  AND NULLIF(TRIM(cv.value), '') IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM {selectedArrays} array_credit
                      WHERE array_credit.media_asset_id = scope.media_asset_id
                        AND array_credit.credit_key = cv.key
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM {selectedClaims} claim_credit
                      WHERE claim_credit.media_asset_id = scope.media_asset_id
                        AND claim_credit.credit_key = cv.key
                  )
            ),
            {selectedScalars} AS MATERIALIZED (
                SELECT candidate.media_asset_id,
                       candidate.credit_key,
                       candidate.billing_order,
                       candidate.person_name,
                       candidate.person_qid
                FROM {scalarCandidates} candidate
                WHERE candidate.scope_priority = (
                    SELECT MIN(peer.scope_priority)
                    FROM {scalarCandidates} peer
                    WHERE peer.media_asset_id = candidate.media_asset_id
                      AND peer.credit_key = candidate.credit_key
                )
            ),
            {selectedCredits} AS MATERIALIZED (
                SELECT * FROM {selectedArrays}
                UNION ALL
                SELECT * FROM {selectedClaims}
                UNION ALL
                SELECT * FROM {selectedScalars}
            ),
            {creditPersons} AS MATERIALIZED (
                SELECT credit.media_asset_id,
                       credit.credit_key,
                       credit.billing_order,
                       person.id AS person_id,
                       person.name AS person_name,
                       person.wikidata_qid AS person_qid,
                       person.is_pseudonym
                FROM {selectedCredits} credit
                INNER JOIN persons person
                    ON person.wikidata_qid = credit.person_qid COLLATE NOCASE
                WHERE credit.person_qid IS NOT NULL
                UNION ALL
                SELECT credit.media_asset_id,
                       credit.credit_key,
                       credit.billing_order,
                       person.id,
                       person.name,
                       person.wikidata_qid,
                       person.is_pseudonym
                FROM {selectedCredits} credit
                INNER JOIN persons person
                    ON person.name = credit.person_name COLLATE NOCASE
                WHERE credit.person_qid IS NULL
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
                WHERE credit.credit_key != 'author'
                   OR NOT EXISTS (
                    SELECT 1
                    FROM {creditPersons} collective_credit
                    WHERE collective_credit.media_asset_id = credit.media_asset_id
                      AND collective_credit.credit_key = credit.credit_key
                      AND collective_credit.is_pseudonym = 1
                      AND collective_credit.person_id != credit.person_id
                )
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
