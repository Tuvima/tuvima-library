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
        string relatedBookYearExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relatedBookYearExpression);

        var workYear = OriginalYearValue(workIdExpression, mediaTypeExpression);
        var rootYear = OriginalYearValue(rootWorkIdExpression, mediaTypeExpression);
        var assetYear = OriginalYearValue(assetIdExpression, mediaTypeExpression);
        var explicitWorkYear = ExplicitOriginalYearValue(workIdExpression, mediaTypeExpression);
        var explicitRootYear = ExplicitOriginalYearValue(rootWorkIdExpression, mediaTypeExpression);
        var explicitAssetYear = ExplicitOriginalYearValue(assetIdExpression, mediaTypeExpression);
        // The borrowed audiobook year: RelatedBookYearLookup over a RelatedBookOriginalYearCte built for the read's own
        // audiobooks, or NULL for a read that never returns audiobooks.
        var relatedBookYear = relatedBookYearExpression;

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

    /// <summary>The stored entities a display year can come from (see <see cref="DisplayOriginalYearFromPivots"/>).</summary>
    public enum YearSlot
    {
        Work,
        Root,
        Asset,
    }

    private static readonly (string[] Likes, string? MediaType)[] YearMediaTypeBranches =
    [
        (["%book%", "%audio%"], "Books"),
        (["%movie%", "%film%"], "Movies"),
        (["%tv%", "%television%"], "TV"),
        (["%comic%", "%manga%"], "Comics"),
        (["%music%", "%album%", "%song%"], "Music"),
    ];

    /// <summary>
    /// Every stored name any media type reads a year from, original or explicit. A read that pivots values (one
    /// column per name, see <see cref="YearPivotAggregate"/>) needs a year column for each of them.
    /// </summary>
    public static IReadOnlyList<string> AllOriginalYearKeys() =>
        new[] { "Books", "Movies", "TV", "Comics", "Music", null }
            .SelectMany(mediaType => MediaDateSemantics.OriginalYearKeys(mediaType)
                .Concat(MediaDateSemantics.ExplicitOriginalYearKeys(mediaType)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// One column of a value pivot (a <c>GROUP BY entity</c> over <c>canonical_values</c>) holding the four-digit year
    /// of <paramref name="key"/>, or NULL when that value does not start with four digits after trimming spaces and '+'.
    /// The column is what <see cref="DisplayOriginalYearFromPivots"/> reads for the key.
    /// </summary>
    public static string YearPivotAggregate(string keyExpression, string valueExpression, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return $"MAX(CASE WHEN {keyExpression} = {SqlLiteral(key)} AND {YearPrefix(valueExpression)} GLOB '[0-9][0-9][0-9][0-9]' THEN {YearPrefix(valueExpression)} END)";
    }

    /// <summary>
    /// Same choice of year as <see cref="DisplayOriginalYear"/> (same media-type rules, same order of keys and entities),
    /// reading the per-entity year columns of a value pivot instead of issuing a look-up for each key.
    /// </summary>
    /// <param name="yearColumn">
    /// The SQL expression holding the year of a stored name for one entity, built with <see cref="YearPivotAggregate"/>
    /// (already four digits, or NULL).
    /// </param>
    /// <param name="relatedBookYearExpression">The borrowed audiobook year, as for <see cref="DisplayOriginalYear"/>.</param>
    public static string DisplayOriginalYearFromPivots(
        string mediaTypeExpression,
        Func<YearSlot, string, string> yearColumn,
        string relatedBookYearExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaTypeExpression);
        ArgumentNullException.ThrowIfNull(yearColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(relatedBookYearExpression);

        string Year(YearSlot slot, bool explicitOnly) => YearByMediaType(
            mediaTypeExpression,
            mediaType => "COALESCE(" + string.Join(
                ", ",
                (explicitOnly
                    ? MediaDateSemantics.ExplicitOriginalYearKeys(mediaType)
                    : MediaDateSemantics.OriginalYearKeys(mediaType))
                .Select(key => yearColumn(slot, key))) + ")");

        var workYear = Year(YearSlot.Work, explicitOnly: false);
        var rootYear = Year(YearSlot.Root, explicitOnly: false);
        var assetYear = Year(YearSlot.Asset, explicitOnly: false);
        var explicitWorkYear = Year(YearSlot.Work, explicitOnly: true);
        var explicitRootYear = Year(YearSlot.Root, explicitOnly: true);
        var explicitAssetYear = Year(YearSlot.Asset, explicitOnly: true);

        return $"""
            CASE
                WHEN LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '%audio%'
                THEN COALESCE(
                    {explicitWorkYear},
                    {explicitAssetYear},
                    {relatedBookYearExpression},
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

    private static string YearByMediaType(string mediaTypeExpression, Func<string?, string> forMediaType)
    {
        var branches = YearMediaTypeBranches.Select(branch =>
            $"WHEN {string.Join(" OR ", branch.Likes.Select(like => $"LOWER(COALESCE({mediaTypeExpression}, '')) LIKE '{like}'"))} THEN {forMediaType(branch.MediaType)}");
        return $"CASE {string.Join(" ", branches)} ELSE {forMediaType(null)} END";
    }

    private static string YearPrefix(string valueExpression) =>
        $"SUBSTR(LTRIM(TRIM({valueExpression}), '+'), 1, 4)";

    /// <summary>
    /// The borrowed audiobook year: an audiobook without an original year of its own takes the year of the matching book,
    /// worked out for every candidate audiobook in one pass. Returns a chain of CTE definitions (without the leading
    /// <c>WITH</c>) whose last CTE is named <paramref name="resultName"/> and has the columns
    /// <c>audiobook_work_id, audiobook_asset_id, year</c>: one row per candidate (work, file) pair that has a matching book.
    /// The pair is the key because one file can cover several works, and each work's own title picks its own book.
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
    /// The rule: a book (not an audiobook) whose file or work title equals the audiobook's
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
                SELECT candidate.work_id AS audiobook_work_id,
                       candidate.asset_id AS audiobook_asset_id,
                       book_match.book_work_id,
                       book_match.book_asset_id,
                       book_match.media_type,
                       ROW_NUMBER() OVER (
                           PARTITION BY candidate.work_id, candidate.asset_id
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
                SELECT book_match.audiobook_work_id,
                       book_match.audiobook_asset_id,
                       COALESCE({bookWorkYear}, {bookAssetYear}) AS year
                FROM {ranked} book_match
                WHERE book_match.match_rank = 1
            )
            """;
    }

    /// <summary>
    /// The look-up of one audiobook (work and file) borrowed year in a CTE built by <see cref="RelatedBookOriginalYearCte"/>.
    /// Pass the result as the <c>relatedBookYearExpression</c> of <see cref="DisplayOriginalYear"/>.
    /// </summary>
    public static string RelatedBookYearLookup(string resultName, string audiobookWorkIdExpression, string audiobookAssetIdExpression)
    {
        ValidateIdentifier(resultName, nameof(resultName));
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookWorkIdExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookAssetIdExpression);

        return $"(SELECT related.year FROM {resultName} related WHERE related.audiobook_work_id = {audiobookWorkIdExpression} AND related.audiobook_asset_id = {audiobookAssetIdExpression})";
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
                YearPivotAggregate(keyExpression, valueExpression, key)));

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
