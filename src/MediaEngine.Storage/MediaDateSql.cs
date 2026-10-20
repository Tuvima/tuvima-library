using System.Text.RegularExpressions;
using MediaEngine.Domain.Services;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite projections for <see cref="MediaDateSemantics"/>. Display surfaces
/// use this instead of relying on row order among year/date canonical keys.
/// </summary>
public static partial class MediaDateSql
{
    public static string DisplayOriginalYear(
        string workIdExpression,
        string rootWorkIdExpression,
        string assetIdExpression,
        string mediaTypeExpression,
        string contributorRelation = "primary_person_media_credits",
        string? relatedBookYearExpression = null)
    {
        var workYear = OriginalYearValue(workIdExpression, mediaTypeExpression);
        var rootYear = OriginalYearValue(rootWorkIdExpression, mediaTypeExpression);
        var assetYear = OriginalYearValue(assetIdExpression, mediaTypeExpression);
        var explicitWorkYear = ExplicitOriginalYearValue(workIdExpression, mediaTypeExpression);
        var explicitRootYear = ExplicitOriginalYearValue(rootWorkIdExpression, mediaTypeExpression);
        var explicitAssetYear = ExplicitOriginalYearValue(assetIdExpression, mediaTypeExpression);
        // A ready expression (RelatedBookYearLookup over RelatedBookOriginalYearCte, or NULL for a read that never
        // returns audiobooks) replaces the per-row look-up, whose cost grows with the square of the library.
        var relatedBookYear = relatedBookYearExpression
            ?? RelatedBookOriginalYear(workIdExpression, assetIdExpression, contributorRelation);

        return $"""
            CASE
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%audio%'
                THEN COALESCE(
                    {explicitWorkYear},
                    {explicitAssetYear},
                    {relatedBookYear},
                    {workYear},
                    {assetYear},
                    {rootYear})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%music%'
                THEN COALESCE(
                    {explicitRootYear},
                    {explicitWorkYear},
                    {workYear},
                    {assetYear},
                    {rootYear})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%tv%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%television%'
                THEN COALESCE({rootYear}, {workYear}, {assetYear})
                ELSE COALESCE({workYear}, {assetYear}, {rootYear})
            END
            """;
    }

    public static string OriginalYearValue(string entityIdExpression, string mediaTypeExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityIdExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaTypeExpression);

        return $"""
            CASE
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%book%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%audio%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.OriginalYearKeys("Books"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%movie%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%film%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.OriginalYearKeys("Movies"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%tv%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%television%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.OriginalYearKeys("TV"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%comic%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%manga%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.OriginalYearKeys("Comics"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%music%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%album%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%song%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.OriginalYearKeys("Music"))})
                ELSE COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.OriginalYearKeys(null))})
            END
            """;
    }

    public static string ExplicitOriginalYearValue(string entityIdExpression, string mediaTypeExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityIdExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaTypeExpression);

        return $"""
            CASE
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%book%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%audio%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Books"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%movie%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%film%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Movies"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%tv%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%television%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.ExplicitOriginalYearKeys("TV"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%comic%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%manga%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Comics"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%music%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%album%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%song%'
                THEN COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Music"))})
                ELSE COALESCE(
                    {CanonicalYearForKeys(entityIdExpression, MediaDateSemantics.ExplicitOriginalYearKeys(null))})
            END
            """;
    }

    public static string RelatedBookOriginalYear(
        string audiobookWorkIdExpression,
        string audiobookAssetIdExpression,
        string contributorRelation = "primary_person_media_credits")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookWorkIdExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookAssetIdExpression);

        var bookWorkYear = OriginalYearValue("book_work.id", "book_work.media_type");
        var bookAssetYear = OriginalYearValue("book_asset.id", "book_work.media_type");

        return $"""
            (SELECT COALESCE({bookWorkYear}, {bookAssetYear})
             FROM {contributorRelation} audiobook_author
             INNER JOIN {contributorRelation} book_author
                 ON book_author.person_id = audiobook_author.person_id
                AND book_author.credit_key = 'author'
             INNER JOIN media_assets book_asset
                 ON book_asset.id = book_author.media_asset_id
             INNER JOIN editions book_edition
                 ON book_edition.id = book_asset.edition_id
             INNER JOIN works book_work
                 ON book_work.id = book_edition.work_id
             WHERE audiobook_author.media_asset_id = {audiobookAssetIdExpression}
               AND audiobook_author.credit_key = 'author'
               AND LOWER(book_work.media_type) LIKE '%book%'
               AND LOWER(book_work.media_type) NOT LIKE '%audio%'
               AND LOWER(TRIM(COALESCE(
                   (SELECT value FROM canonical_values
                    WHERE entity_id = book_asset.id AND key = 'title'
                    ORDER BY last_scored_at DESC LIMIT 1),
                   (SELECT value FROM canonical_values
                    WHERE entity_id = book_work.id AND key = 'title'
                    ORDER BY last_scored_at DESC LIMIT 1)))) =
                   LOWER(TRIM(COALESCE(
                       (SELECT value FROM canonical_values
                        WHERE entity_id = {audiobookAssetIdExpression} AND key = 'title'
                        ORDER BY last_scored_at DESC LIMIT 1),
                       (SELECT value FROM canonical_values
                        WHERE entity_id = {audiobookWorkIdExpression} AND key = 'title'
                        ORDER BY last_scored_at DESC LIMIT 1))))
             ORDER BY book_work.id, book_asset.id
             LIMIT 1)
            """;
    }

    /// <summary>
    /// Set-based form of <see cref="RelatedBookOriginalYear"/>: works out the borrowed year for every candidate
    /// audiobook in one pass instead of one look-up per row. Returns a chain of CTE definitions (without the leading
    /// <c>WITH</c>) whose last CTE is named <paramref name="resultName"/> and has the columns
    /// <c>audiobook_asset_id, year</c>: one row per candidate file that has a matching book.
    /// </summary>
    /// <param name="resultName">Name of the final CTE the caller selects from.</param>
    /// <param name="candidatesSelect">
    /// A <c>SELECT</c> returning the candidate audiobooks as two columns, <c>work_id</c> and <c>asset_id</c>
    /// (the work and the file the year is wanted for). It carries no trailing semicolon.
    /// </param>
    /// <param name="prefix">
    /// Prepended to every internal CTE name so the block can appear more than once in one statement.
    /// Letters, digits and underscores only.
    /// </param>
    /// <remarks>
    /// Same rule as the per-row form: a book (not an audiobook) whose file or work title equals the audiobook's
    /// (ignoring case and surrounding spaces) and that shares an author person; the lowest work id and then file id
    /// wins, and when that first match has no year the result has no borrowed year. Author credits come from
    /// <see cref="PersonCreditSql.ScopedCreditsCte"/> for just the candidates and the matching books.
    /// </remarks>
    public static string RelatedBookOriginalYearCte(string resultName, string candidatesSelect, string prefix)
    {
        ValidateIdentifier(resultName, nameof(resultName));
        ValidateIdentifier(prefix, nameof(prefix));
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatesSelect);

        var candidates = prefix + "candidates";
        var candidateTitles = prefix + "candidate_titles";
        var bookTitles = prefix + "book_titles";
        var bookMatches = prefix + "book_matches";
        var credits = prefix + "credits";
        var authors = prefix + "authors";
        var ranked = prefix + "ranked";

        var bookWorkYear = OriginalYearValue("book_match.book_work_id", "book_match.media_type");
        var bookAssetYear = OriginalYearValue("book_match.book_asset_id", "book_match.media_type");

        var creditsCte = PersonCreditSql.ScopedCreditsCte(
            credits,
            $"SELECT asset_id FROM {candidates} UNION SELECT book_asset_id FROM {bookMatches}",
            prefix + "cr_");

        return $"""
            {candidates} AS MATERIALIZED (
                SELECT DISTINCT candidate.work_id, candidate.asset_id
                FROM ({candidatesSelect}) candidate
                WHERE candidate.asset_id IS NOT NULL
            ),
            {candidateTitles} AS MATERIALIZED (
                SELECT candidate.work_id,
                       candidate.asset_id,
                       LOWER(TRIM(COALESCE(
                           (SELECT value FROM canonical_values
                            WHERE entity_id = candidate.asset_id AND key = 'title'
                            ORDER BY last_scored_at DESC LIMIT 1),
                           (SELECT value FROM canonical_values
                            WHERE entity_id = candidate.work_id AND key = 'title'
                            ORDER BY last_scored_at DESC LIMIT 1)))) AS title_key
                FROM {candidates} candidate
            ),
            {bookTitles} AS MATERIALIZED (
                SELECT book_work.id AS book_work_id,
                       book_asset.id AS book_asset_id,
                       book_work.media_type AS media_type,
                       LOWER(TRIM(COALESCE(
                           (SELECT value FROM canonical_values
                            WHERE entity_id = book_asset.id AND key = 'title'
                            ORDER BY last_scored_at DESC LIMIT 1),
                           (SELECT value FROM canonical_values
                            WHERE entity_id = book_work.id AND key = 'title'
                            ORDER BY last_scored_at DESC LIMIT 1)))) AS title_key
                FROM works book_work
                INNER JOIN editions book_edition ON book_edition.work_id = book_work.id
                INNER JOIN media_assets book_asset ON book_asset.edition_id = book_edition.id
                WHERE LOWER(book_work.media_type) LIKE '%book%'
                  AND LOWER(book_work.media_type) NOT LIKE '%audio%'
            ),
            {bookMatches} AS MATERIALIZED (
                SELECT book.book_work_id, book.book_asset_id, book.media_type, book.title_key
                FROM {bookTitles} book
                WHERE book.title_key IN (
                    SELECT title_key FROM {candidateTitles} WHERE title_key IS NOT NULL)
            ),
            {creditsCte},
            {authors} AS MATERIALIZED (
                SELECT DISTINCT credit.media_asset_id, credit.person_id
                FROM {credits} credit
                WHERE credit.credit_key = 'author'
            ),
            {ranked} AS (
                SELECT candidate.asset_id AS audiobook_asset_id,
                       book_match.book_work_id,
                       book_match.book_asset_id,
                       book_match.media_type,
                       ROW_NUMBER() OVER (
                           PARTITION BY candidate.asset_id
                           ORDER BY book_match.book_work_id, book_match.book_asset_id) AS match_rank
                FROM {candidateTitles} candidate
                INNER JOIN {authors} audiobook_author
                    ON audiobook_author.media_asset_id = candidate.asset_id
                INNER JOIN {authors} book_author
                    ON book_author.person_id = audiobook_author.person_id
                INNER JOIN {bookMatches} book_match
                    ON book_match.book_asset_id = book_author.media_asset_id
                   AND book_match.title_key = candidate.title_key
            ),
            {resultName} AS MATERIALIZED (
                SELECT book_match.audiobook_asset_id,
                       COALESCE({bookWorkYear}, {bookAssetYear}) AS year
                FROM {ranked} book_match
                WHERE book_match.match_rank = 1
            )
            """;
    }

    /// <summary>
    /// The look-up of one audiobook file's borrowed year in a CTE built by <see cref="RelatedBookOriginalYearCte"/>.
    /// Pass the result as the <c>relatedBookYearExpression</c> of <see cref="DisplayOriginalYear"/>.
    /// </summary>
    public static string RelatedBookYearLookup(string resultName, string audiobookAssetIdExpression)
    {
        ValidateIdentifier(resultName, nameof(resultName));
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookAssetIdExpression);

        return $"(SELECT related.year FROM {resultName} related WHERE related.audiobook_asset_id = {audiobookAssetIdExpression})";
    }

    public static string OriginalYearFromGroupedRows(
        string mediaTypeExpression,
        string keyExpression,
        string valueExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaTypeExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueExpression);

        return $"""
            CASE
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%book%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%audio%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.OriginalYearKeys("Books"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%movie%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%film%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.OriginalYearKeys("Movies"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%tv%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%television%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.OriginalYearKeys("TV"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%comic%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%manga%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.OriginalYearKeys("Comics"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%music%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%album%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%song%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.OriginalYearKeys("Music"))})
                ELSE COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.OriginalYearKeys(null))})
            END
            """;
    }

    public static string ExplicitOriginalYearFromGroupedRows(
        string mediaTypeExpression,
        string keyExpression,
        string valueExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaTypeExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueExpression);

        return $"""
            CASE
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%book%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%audio%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Books"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%movie%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%film%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Movies"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%tv%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%television%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.ExplicitOriginalYearKeys("TV"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%comic%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%manga%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Comics"))})
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%music%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%album%'
                  OR LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%song%'
                THEN COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.ExplicitOriginalYearKeys("Music"))})
                ELSE COALESCE(
                    {AggregateKeys(keyExpression, valueExpression, MediaDateSemantics.ExplicitOriginalYearKeys(null))})
            END
            """;
    }

    private static string CanonicalYearForKeys(
        string entityIdExpression,
        IReadOnlyList<string> keys)
        => string.Join(
            "," + Environment.NewLine,
            keys.Select(key => $"""
                (SELECT SUBSTR(LTRIM(TRIM(date_value.value), '+'), 1, 4)
                 FROM canonical_values date_value
                 WHERE date_value.entity_id = {entityIdExpression}
                   AND date_value.key = {SqlLiteral(key)}
                   AND SUBSTR(LTRIM(TRIM(date_value.value), '+'), 1, 4)
                       GLOB '[0-9][0-9][0-9][0-9]'
                 ORDER BY date_value.last_scored_at DESC
                 LIMIT 1)
                """));

    private static string AggregateKeys(
        string keyExpression,
        string valueExpression,
        IReadOnlyList<string> keys)
        => string.Join(
            "," + Environment.NewLine,
            keys.Select(key =>
                $"MAX(CASE WHEN {keyExpression} = {SqlLiteral(key)} AND SUBSTR(LTRIM(TRIM({valueExpression}), '+'), 1, 4) GLOB '[0-9][0-9][0-9][0-9]' THEN SUBSTR(LTRIM(TRIM({valueExpression}), '+'), 1, 4) END)"));

    private static string SqlLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

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
