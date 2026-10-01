using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class EditorSuggestionReadService
{
    private readonly IDatabaseConnection _db;

    public EditorSuggestionReadService(IDatabaseConnection db) => _db = db;

    public IReadOnlyList<string> GetValues(string field, Guid? profileId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var take = Math.Clamp(limit, 1, 500);
        using var connection = _db.CreateConnection();

        IEnumerable<string> values = field.Trim().ToLowerInvariant() switch
        {
            "genre" => ReadGenres(connection, ct),
            "tag" or "tags" or "custom_tags" => ReadTags(connection, ct),
            _ => [],
        };

        return values
            .SelectMany(SplitValues)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    private static IEnumerable<string> ReadGenres(System.Data.IDbConnection connection, CancellationToken ct)
    {
        var canonical = connection.Query<string>(new CommandDefinition(
            """
            SELECT value FROM canonical_value_arrays WHERE key = 'genre' AND NULLIF(TRIM(value), '') IS NOT NULL
            UNION ALL
            SELECT value FROM canonical_values WHERE key = 'genre' AND NULLIF(TRIM(value), '') IS NOT NULL;
            """,
            cancellationToken: ct));
        var overrides = connection.Query<string?>(new CommandDefinition(
            "SELECT json_extract(display_overrides_json, '$.genre') FROM works WHERE json_valid(display_overrides_json);",
            cancellationToken: ct));
        return canonical.Concat(overrides.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static IEnumerable<string> ReadTags(System.Data.IDbConnection connection, CancellationToken ct)
    {
        return connection.Query<string?>(new CommandDefinition(
                """
                WITH canonical_tag_values AS (
                    SELECT entity_id, value FROM canonical_value_arrays
                    WHERE key='custom_tags' AND NULLIF(TRIM(value), '') IS NOT NULL
                    UNION ALL
                    SELECT entity_id, value FROM canonical_values
                    WHERE key='custom_tags' AND NULLIF(TRIM(value), '') IS NOT NULL
                )
                SELECT tags.value
                  FROM canonical_tag_values tags
                 WHERE NOT EXISTS (
                    SELECT 1
                      FROM works owner
                      LEFT JOIN editions edition ON edition.work_id=owner.id
                      LEFT JOIN media_assets asset ON asset.edition_id=edition.id
                     WHERE (owner.id=tags.entity_id OR edition.id=tags.entity_id OR asset.id=tags.entity_id)
                       AND json_valid(owner.display_overrides_json)
                       AND json_type(owner.display_overrides_json, '$.custom_tags') IS NOT NULL
                 )
                   AND NOT EXISTS (
                    SELECT 1 FROM persons owner
                     WHERE owner.id=tags.entity_id
                       AND json_valid(owner.display_overrides_json)
                       AND json_type(owner.display_overrides_json, '$.custom_tags') IS NOT NULL
                 )
                UNION ALL
                SELECT json_extract(display_overrides_json, '$.custom_tags')
                  FROM works
                 WHERE json_valid(display_overrides_json)
                   AND NULLIF(TRIM(json_extract(display_overrides_json, '$.custom_tags')), '') IS NOT NULL
                UNION ALL
                SELECT json_extract(display_overrides_json, '$.custom_tags')
                  FROM persons
                 WHERE json_valid(display_overrides_json)
                   AND NULLIF(TRIM(json_extract(display_overrides_json, '$.custom_tags')), '') IS NOT NULL;
                """,
                cancellationToken: ct))
            .SelectMany(LibraryTagCatalog.ParseDisplayValue);
    }

    private static IEnumerable<string> SplitValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
