using System.Data;
using Dapper;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Typed read boundary for library curation. It owns GUID-BLOB SQL shape and
/// batches selected items so endpoint handlers do not perform N+1 lookups.
/// </summary>
public sealed class LibraryCurationReadService(IDatabaseConnection db) : ILibraryCurationReadService
{
    private const int MaxParametersPerQuery = 400;

    // A show sits at most three levels above an episode (show, season, episode); one more level of slack.
    private const int MaxAncestorDepth = 4;

    // Where each kind of title keeps its name (an episode or movie "title", a show's "show_name", and so on), best first.
    private static readonly string[] TitleKeys = ["title", "show_name", "season_title", "album", "series"];

    // SQL literal for an empty JSON object (a raw interpolated string cannot spell the braces itself).
    private const string EmptyJsonObject = "'{}'";

    public async Task<IReadOnlyDictionary<Guid, BatchEditItemRead>> LoadBatchEditItemsAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyCollection<string> fieldKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(fieldKeys);
        ct.ThrowIfCancellationRequested();

        var ids = entityIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var keys = fieldKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, BatchEditItemRead>();
        }

        using var conn = db.CreateConnection();

        // 1. The selected id is a work or a media asset; either way the batch edits the work's presentation overrides.
        var selected = await QueryByIdsAsync<SelectedRow>(conn, ids, values =>
            $"""
            WITH requested(input_id) AS (VALUES {values})
            SELECT r.input_id AS RequestedEntityId, w.id AS WorkId,
                   w.media_type AS MediaType, w.work_kind AS WorkKind, w.parent_work_id AS ParentWorkId
            FROM requested r
            JOIN works w ON w.id = r.input_id
            WHERE {VaultSql.NotVaultEntity("w.id")}
            UNION
            SELECT r.input_id, w.id, w.media_type, w.work_kind, w.parent_work_id
            FROM requested r
            JOIN media_assets ma ON ma.id = r.input_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE {VaultSql.NotVaultEntity("w.id")};
            """, ct).ConfigureAwait(false);
        if (selected.Count == 0)
        {
            return new Dictionary<Guid, BatchEditItemRead>();
        }

        var selectedWorkIds = selected.Select(row => row.WorkId).Distinct().ToArray();

        // 2. The chain above each work, nearest first, for values a work shows from its show, season or album.
        var ancestors = await QueryByIdsAsync<AncestorRow>(conn, selectedWorkIds, values =>
            $"""
            WITH RECURSIVE requested(input_id) AS (VALUES {values}),
            chain(WorkId, AncestorId, Depth) AS (
                SELECT w.id, w.parent_work_id, 1
                FROM requested r JOIN works w ON w.id = r.input_id
                WHERE w.parent_work_id IS NOT NULL
                UNION ALL
                SELECT c.WorkId, p.parent_work_id, c.Depth + 1
                FROM chain c JOIN works p ON p.id = c.AncestorId
                WHERE p.parent_work_id IS NOT NULL AND c.Depth < {MaxAncestorDepth}
            )
            SELECT WorkId, AncestorId, Depth FROM chain;
            """, ct).ConfigureAwait(false);

        var allWorkIds = selectedWorkIds.Concat(ancestors.Select(row => row.AncestorId)).Distinct().ToArray();

        // 3. Every record that can carry a value for a work: the work, its editions and their files.
        var owners = await QueryByIdsAsync<OwnerRow>(conn, allWorkIds, values =>
            $"""
            WITH requested(input_id) AS (VALUES {values})
            SELECT r.input_id AS WorkId, r.input_id AS OwnerId, 0 AS Rank
            FROM requested r
            UNION ALL
            SELECT e.work_id, e.id, 1
            FROM requested r JOIN editions e ON e.work_id = r.input_id
            UNION ALL
            SELECT e.work_id, ma.id, 2
            FROM requested r
            JOIN editions e ON e.work_id = r.input_id
            JOIN media_assets ma ON ma.edition_id = e.id;
            """, ct).ConfigureAwait(false);

        // 4. Stored presentation overrides. SQLite reads the JSON, so a malformed document simply has no entries.
        var overrides = new Dictionary<Guid, Dictionary<string, string>>();
        foreach (var row in await QueryByIdsAsync<OverrideRow>(conn, allWorkIds, values =>
            $"""
            WITH requested(input_id) AS (VALUES {values})
            SELECT w.id AS WorkId, j.key AS Key, CAST(j.value AS TEXT) AS Value
            FROM requested r
            JOIN works w ON w.id = r.input_id
            JOIN json_each(CASE WHEN json_valid(w.display_overrides_json) THEN w.display_overrides_json ELSE {EmptyJsonObject} END) j
            WHERE j.type = 'text';
            """, ct).ConfigureAwait(false))
        {
            if (!overrides.TryGetValue(row.WorkId, out var perWork))
            {
                perWork = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                overrides[row.WorkId] = perWork;
            }

            perWork[row.Key] = row.Value;
        }

        // 5. Provider data on those records for the requested fields (and the title, to name items).
        var ownerIds = owners.Select(row => row.OwnerId).Distinct().ToArray();
        var wantedKeys = keys.Concat(TitleKeys).Distinct(StringComparer.Ordinal).ToArray();
        var arrayRows = await QueryByIdsAsync<ValueRow>(conn, ownerIds, values =>
            $"""
            WITH requested(input_id) AS (VALUES {values})
            SELECT a.entity_id AS OwnerId, a.key AS Key, a.value AS Value, a.ordinal AS Ordinal
            FROM requested r
            JOIN canonical_value_arrays a ON a.entity_id = r.input_id
            WHERE a.key IN ({KeyList(wantedKeys)}) AND NULLIF(TRIM(a.value), '') IS NOT NULL;
            """, ct).ConfigureAwait(false);
        var scalarRows = await QueryByIdsAsync<ValueRow>(conn, ownerIds, values =>
            $"""
            WITH requested(input_id) AS (VALUES {values})
            SELECT c.entity_id AS OwnerId, c.key AS Key, c.value AS Value, 0 AS Ordinal
            FROM requested r
            JOIN canonical_values c ON c.entity_id = r.input_id
            WHERE c.key IN ({KeyList(wantedKeys)}) AND NULLIF(TRIM(c.value), '') IS NOT NULL;
            """, ct).ConfigureAwait(false);

        var ownersByWork = owners
            .GroupBy(row => row.WorkId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => row.Rank).ThenBy(row => row.OwnerId).Select(row => row.OwnerId).ToList());
        var arraysByOwnerAndKey = arrayRows
            .GroupBy(row => (row.OwnerId, Key: row.Key.ToLowerInvariant()))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.OrderBy(row => row.Ordinal).Select(row => row.Value.Trim()).ToList());
        var scalarsByOwnerAndKey = scalarRows
            .GroupBy(row => (row.OwnerId, Key: row.Key.ToLowerInvariant()))
            .ToDictionary(group => group.Key, group => group.First().Value.Trim());
        var ancestorsByWork = ancestors
            .GroupBy(row => row.WorkId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Depth).Select(row => row.AncestorId).ToList());

        // What a work's own provider data says for a key: the first record (work, then edition, then file) that has any.
        IReadOnlyList<string> OwnValues(Guid workId, string key)
        {
            if (!ownersByWork.TryGetValue(workId, out var workOwners))
            {
                return [];
            }

            foreach (var ownerId in workOwners)
            {
                if (arraysByOwnerAndKey.TryGetValue((ownerId, key), out var array))
                {
                    return array;
                }

                if (scalarsByOwnerAndKey.TryGetValue((ownerId, key), out var scalar))
                {
                    return BatchEditFieldCatalog.TryGet(key, out var field) && field.Kind == BatchEditFieldKind.List
                        ? BatchEditFieldCatalog.ParseList(scalar)
                        : [scalar];
                }
            }

            return [];
        }

        string? OverrideOf(Guid workId, string key) =>
            overrides.TryGetValue(workId, out var perWork) && perWork.TryGetValue(key, out var value) ? value : null;

        IReadOnlyList<string> InheritedValues(Guid workId, string key)
        {
            if (!ancestorsByWork.TryGetValue(workId, out var chain))
            {
                return [];
            }

            foreach (var ancestorId in chain)
            {
                var stored = OverrideOf(ancestorId, key);
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    return BatchEditFieldCatalog.TryGet(key, out var field) && field.Kind == BatchEditFieldKind.List
                        ? BatchEditFieldCatalog.ParseList(stored)
                        : [stored.Trim()];
                }

                var own = OwnValues(ancestorId, key);
                if (own.Count > 0)
                {
                    return own;
                }
            }

            return [];
        }

        var result = new Dictionary<Guid, BatchEditItemRead>();
        foreach (var row in selected)
        {
            var fields = new Dictionary<string, BatchEditFieldRead>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                fields[key] = new BatchEditFieldRead(
                    OverrideOf(row.WorkId, key),
                    OwnValues(row.WorkId, key),
                    InheritedValues(row.WorkId, key));
            }

            var title = OverrideOf(row.WorkId, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = TitleKeys.Select(key => OwnValues(row.WorkId, key).FirstOrDefault())
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            }

            result[row.RequestedEntityId] = new BatchEditItemRead(
                row.RequestedEntityId,
                row.WorkId,
                row.MediaType,
                row.WorkKind,
                row.ParentWorkId is not null,
                title,
                fields);
        }

        return result;
    }

    public async Task<IReadOnlySet<Guid>> FindNonTitleIdsAsync(
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ct.ThrowIfCancellationRequested();

        var ids = entityIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }

        using var conn = db.CreateConnection();
        var found = await QueryByIdsAsync<IdRow>(conn, ids, values =>
            $"""
            WITH requested(input_id) AS (VALUES {values})
            SELECT r.input_id AS Id FROM requested r JOIN editions e ON e.id = r.input_id
            UNION
            SELECT r.input_id FROM requested r JOIN collections c ON c.id = r.input_id
            UNION
            SELECT r.input_id FROM requested r JOIN persons p ON p.id = r.input_id;
            """, ct).ConfigureAwait(false);
        return found.Select(row => row.Id).ToHashSet();
    }

    private static string KeyList(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(key => $"'{key.Replace("'", "''", StringComparison.Ordinal)}'"));

    private static async Task<List<T>> QueryByIdsAsync<T>(
        IDbConnection conn,
        IReadOnlyCollection<Guid> ids,
        Func<string, string> sql,
        CancellationToken ct)
    {
        var rows = new List<T>();
        foreach (var batch in ids.Chunk(MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            var values = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                var name = $"id{i}";
                values[i] = $"(@{name})";
                parameters.Add(name, GuidSql.ToBlob(batch[i]));
            }

            rows.AddRange(await conn.QueryAsync<T>(new CommandDefinition(
                sql(string.Join(", ", values)),
                parameters,
                cancellationToken: ct)).ConfigureAwait(false));
        }

        return rows;
    }

    private sealed class SelectedRow
    {
        public Guid RequestedEntityId { get; init; }
        public Guid WorkId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string WorkKind { get; init; } = string.Empty;
        public Guid? ParentWorkId { get; init; }
    }

    private sealed class IdRow
    {
        public Guid Id { get; init; }
    }

    private sealed class AncestorRow
    {
        public Guid WorkId { get; init; }
        public Guid AncestorId { get; init; }
        public int Depth { get; init; }
    }

    private sealed class OwnerRow
    {
        public Guid WorkId { get; init; }
        public Guid OwnerId { get; init; }
        public int Rank { get; init; }
    }

    private sealed class OverrideRow
    {
        public Guid WorkId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    private sealed class ValueRow
    {
        public Guid OwnerId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public int Ordinal { get; init; }
    }
}
