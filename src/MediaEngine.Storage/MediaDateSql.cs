using MediaEngine.Domain.Services;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite projections for <see cref="MediaDateSemantics"/>. Display surfaces
/// use this instead of relying on row order among year/date canonical keys.
/// </summary>
public static class MediaDateSql
{
    public static string DisplayOriginalYear(
        string workIdExpression,
        string rootWorkIdExpression,
        string assetIdExpression,
        string mediaTypeExpression,
        string contributorRelation = "primary_person_media_credits")
    {
        var workYear = OriginalYearValue(workIdExpression, mediaTypeExpression);
        var rootYear = OriginalYearValue(rootWorkIdExpression, mediaTypeExpression);
        var assetYear = OriginalYearValue(assetIdExpression, mediaTypeExpression);
        var explicitWorkYear = ExplicitOriginalYearValue(workIdExpression, mediaTypeExpression);
        var explicitRootYear = ExplicitOriginalYearValue(rootWorkIdExpression, mediaTypeExpression);
        var explicitAssetYear = ExplicitOriginalYearValue(assetIdExpression, mediaTypeExpression);
        var relatedBookYear = RelatedBookOriginalYear(workIdExpression, assetIdExpression, contributorRelation);

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
}
