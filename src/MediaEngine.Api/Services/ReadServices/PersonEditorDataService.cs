using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using Dapper;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class PersonEditorReadService
{
    private readonly IDatabaseConnection _db;
    private readonly IPersonRepository _persons;

    public PersonEditorReadService(IDatabaseConnection db, IPersonRepository persons)
    {
        _db = db;
        _persons = persons;
    }

    public async Task<PersonEditorStateResponse?> GetAsync(Guid personId, Guid? profileId, CancellationToken ct)
    {
        var person = await _persons.FindByIdAsync(personId, ct);
        if (person is null)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var state = conn.QueryFirstOrDefault<PersonEditorStateRow>("""
            SELECT p.display_overrides_json AS DisplayOverridesJson,
                   COALESCE((SELECT MAX(activity.occurred_at)
                      FROM system_activity activity
                     WHERE activity.entity_id=p.id AND activity.entity_type='Person'), '') AS UpdatedAt
            FROM persons p
            WHERE p.id = @personId
            LIMIT 1;
            """, new { personId });

        var history = conn.Query<PersonHistoryRow>("""
            SELECT id AS Id, occurred_at AS OccurredAt, action_type AS ActionType,
                   detail AS Detail, profile_id AS ProfileId
            FROM system_activity
            WHERE entity_id = @personId AND entity_type = 'Person'
            ORDER BY occurred_at DESC
            LIMIT 200;
            """, new { personId })
            .Select(row => new LibraryItemHistoryDto
            {
                Id = row.Id.ToString(),
                EntityId = personId,
                OccurredAt = DateTimeOffset.TryParse(row.OccurredAt, out var occurredAt) ? occurredAt : DateTimeOffset.UtcNow,
                EventType = row.ActionType,
                Label = FormatHistoryLabel(row.ActionType),
                Detail = row.Detail,
                Category = ClassifyHistory(row.ActionType),
                ActorLabel = row.ProfileId.HasValue ? "Library user" : "System",
            })
            .ToList();

        var displayOverrides = new Dictionary<string, string>(
            DeserializeStringMap(state?.DisplayOverridesJson),
            StringComparer.OrdinalIgnoreCase);
        var localTags = displayOverrides.TryGetValue("custom_tags", out var storedTags)
            ? LibraryTagCatalog.ParseDisplayValue(storedTags)
            : [];
        displayOverrides.Remove("custom_tags");

        return new PersonEditorStateResponse
        {
            PersonId = personId,
            BaselineName = person.Name,
            BaselineBiography = person.Biography,
            DisplayOverrides = displayOverrides,
            LocalTags = localTags,
            Revision = Revision(state?.DisplayOverridesJson),
            UpdatedAt = DateTimeOffset.TryParse(state?.UpdatedAt, out var updatedAt) ? updatedAt : null,
            History = history,
        };
    }

    public IReadOnlyDictionary<string, string> GetDisplayOverrides(Guid personId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var json = conn.QueryFirstOrDefault<string?>(
            "SELECT display_overrides_json FROM persons WHERE id = @personId LIMIT 1;",
            new { personId });
        return DeserializeStringMap(json);
    }

    public Task<PersonEditorWriteResult> SaveAsync(Guid personId, PersonEditorSaveRequest request, CancellationToken ct) =>
        _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var currentJson = conn.QueryFirstOrDefault<string?>("""
                SELECT display_overrides_json FROM persons WHERE id=@personId LIMIT 1;
                """, new { personId }, tx);
            var revision = Revision(currentJson);
            if (revision != request.ExpectedRevision)
            {
                return new PersonEditorWriteResult(false, revision);
            }

            if (!LibraryTagCatalog.TryNormalize(request.LocalTags, out var tags, out _))
            {
                return new PersonEditorWriteResult(false, revision);
            }

            var normalizedOverrides = request.DisplayOverrides
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
            normalizedOverrides["custom_tags"] = string.Join("; ", tags);

            var nextJson = normalizedOverrides.Count == 0 ? null : JsonSerializer.Serialize(normalizedOverrides);
            var updated = conn.Execute("""
                UPDATE persons SET display_overrides_json=@json
                WHERE id=@personId AND display_overrides_json IS @currentJson;
                """, new { personId, json = nextJson, currentJson }, tx);
            if (updated == 0)
            {
                var latestJson = conn.QueryFirstOrDefault<string?>("""
                    SELECT display_overrides_json FROM persons WHERE id=@personId LIMIT 1;
                    """, new { personId }, tx);
                return new PersonEditorWriteResult(false, Revision(latestJson));
            }

            conn.Execute("""
                INSERT INTO system_activity (action_type, entity_id, entity_type, profile_id, changes_json, detail)
                VALUES (@actionType, @personId, 'Person', @profileId, @changes, @detail);
                """, new
            {
                actionType = SystemActionType.MetadataManualOverride,
                personId,
                profileId = request.ProfileId,
                changes = JsonSerializer.Serialize(new { display_overrides = normalizedOverrides.Keys, library_tags = tags.Count }),
                detail = "Person details and local library fields updated",
            }, tx);

            return new PersonEditorWriteResult(true, Revision(nextJson));
        }, ct);

    private static IReadOnlyDictionary<string, string> DeserializeStringMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static long Revision(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return BinaryPrimitives.ReadInt64BigEndian(digest) & long.MaxValue;
    }

    private static string FormatHistoryLabel(string actionType) => actionType switch
    {
        SystemActionType.PersonHydrated => "Identity enriched",
        SystemActionType.CoverArtSaved => "Artwork updated",
        SystemActionType.MetadataManualOverride => "Person edited",
        _ => actionType,
    };

    private static string ClassifyHistory(string actionType) => actionType switch
    {
        SystemActionType.CoverArtSaved => "artwork",
        SystemActionType.PersonHydrated => "match",
        _ => "metadata",
    };

    private sealed record PersonEditorStateRow(string? DisplayOverridesJson, string? UpdatedAt);
    private sealed record PersonHistoryRow(long Id, string OccurredAt, string ActionType, string? Detail, Guid? ProfileId);
}

public sealed record PersonEditorWriteResult(bool Saved, long Revision);
