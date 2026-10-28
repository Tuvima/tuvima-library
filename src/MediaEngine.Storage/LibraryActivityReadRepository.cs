using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Reads one library's events from the tables that already hold them (Library pages D16): <c>system_activity</c>, <c>folder_first_checks</c>
/// and <c>folder_organise_log</c>. Files are tied to a library by <c>media_assets.library_id</c>, folders by the library's own source folders
/// (the first check's path rule, <see cref="FolderPathSql"/>), so nothing is guessed from a title. Short-lived connection per call.
/// </summary>
public sealed class LibraryActivityReadRepository(IDatabaseConnection db) : ILibraryActivityReadRepository
{
    public Task<IReadOnlyList<SystemActivityEntry>> GetEntriesAsync(
        Guid libraryId, IReadOnlyList<LibraryActivitySource> sources, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var parameters = new DynamicParameters();
        parameters.Add("libraryId", libraryId.ToString("D"));
        parameters.Add("take", Math.Clamp(take, 1, 500));
        parameters.Add("added", SystemActionType.MediaAdded);
        parameters.Add("reviewCreated", SystemActionType.ReviewItemCreated);
        parameters.Add("reviewResolved", SystemActionType.ReviewItemResolved);
        parameters.Add("failed", SystemActionType.MediaFailed);
        parameters.Add("kindChanged", SystemActionType.FolderKindChanged);
        parameters.Add("sourceIds", sources.Count == 0 ? "[]" : GuidSql.ToJsonHexArray(sources.Select(source => source.SourceId)));

        // A failed file is tied to the folder it was found in by its source path (the writer keeps it in the change text).
        var failedInFolder = new List<string>();
        for (var index = 0; index < sources.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(sources[index].Path))
            {
                continue;
            }

            var prefix = FolderFirstCheckPaths.Prefix(sources[index].Path);
            var folded = FolderFirstCheckPaths.Folded(prefix);
            parameters.Add($"low{index}", folded);
            parameters.Add($"high{index}", FolderFirstCheckPaths.RangeEnd(folded));
            parameters.Add($"prefix{index}", prefix);
            failedInFolder.Add(FolderPathSql.InFolder(
                "json_extract(s.changes_json, '$.source_path')", $"@low{index}", $"@high{index}", $"@prefix{index}"));
        }

        var failedClause = failedInFolder.Count == 0
            ? "0"
            : $"(s.action_type = @failed AND json_valid(s.changes_json) AND ({string.Join(" OR ", failedInFolder)}))";

        using var connection = db.CreateConnection();
        var rows = connection.Query<SystemActivityEntry>($"""
            SELECT s.id           AS Id,
                   s.occurred_at  AS OccurredAt,
                   s.action_type  AS ActionType,
                   s.collection_name AS CollectionName,
                   s.entity_id    AS EntityId,
                   s.entity_type  AS EntityType,
                   s.profile_id   AS ProfileId,
                   s.changes_json AS ChangesJson,
                   s.detail       AS Detail
            FROM system_activity s
            WHERE (
                    s.action_type IN (@added, @reviewCreated, @reviewResolved)
                    AND s.entity_id IN (
                        SELECT a.id FROM media_assets a
                        WHERE lower(a.library_id) = @libraryId AND a.is_orphaned = 0
                        UNION
                        SELECT e.work_id FROM media_assets a JOIN editions e ON e.id = a.edition_id
                        WHERE lower(a.library_id) = @libraryId AND a.is_orphaned = 0)
                  )
               OR {failedClause}
               OR (
                    s.action_type = @kindChanged
                    AND (s.entity_id IN (SELECT unhex(value) FROM json_each(@sourceIds))
                         OR (json_valid(s.changes_json) AND lower(json_extract(s.changes_json, '$.to_library_id')) = @libraryId))
                  )
            ORDER BY s.occurred_at DESC, s.id DESC
            LIMIT @take;
            """, parameters).AsList();
        return Task.FromResult<IReadOnlyList<SystemActivityEntry>>(rows);
    }

    public Task<IReadOnlyList<LibraryActivityFolderCheck>> GetFolderChecksAsync(Guid libraryId, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<CheckRow>("""
            SELECT source_id  AS SourceId,
                   path       AS Path,
                   state      AS State,
                   tried      AS Tried,
                   unmatched  AS Unmatched,
                   started_at AS StartedAt,
                   ended_at   AS EndedAt
            FROM folder_first_checks
            WHERE lower(library_id) = @libraryId
            ORDER BY COALESCE(ended_at, started_at) DESC
            LIMIT @take;
            """, new { libraryId = libraryId.ToString("D"), take = Math.Clamp(take, 1, 500) })
            .Select(row => new LibraryActivityFolderCheck(
                row.SourceId, row.Path, row.State, (int)row.Tried, (int)row.Unmatched,
                DateTimeOffset.Parse(row.StartedAt, System.Globalization.CultureInfo.InvariantCulture),
                row.EndedAt is null ? null : DateTimeOffset.Parse(row.EndedAt, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        return Task.FromResult<IReadOnlyList<LibraryActivityFolderCheck>>(rows);
    }

    public Task<IReadOnlyList<LibraryActivityOrganiseRun>> GetOrganiseRunsAsync(
        IReadOnlyCollection<Guid> sourceIds, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (sourceIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<LibraryActivityOrganiseRun>>([]);
        }

        using var connection = db.CreateConnection();
        var rows = connection.Query<RunRow>("""
            SELECT operation_id AS OperationId,
                   source_id    AS SourceId,
                   SUM(CASE WHEN outcome IN ('moved', 'moved_unique_name') THEN 1 ELSE 0 END) AS Moved,
                   SUM(CASE WHEN outcome IN ('copied', 'copied_unique_name') THEN 1 ELSE 0 END) AS Copied,
                   SUM(CASE WHEN outcome = 'kind_changed' THEN 1 ELSE 0 END) AS KindChanged,
                   SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END) AS Failed,
                   MAX(at) AS LastAt
            FROM folder_organise_log
            WHERE source_id IN (SELECT unhex(value) FROM json_each(@sourceIds))
            GROUP BY operation_id, source_id
            ORDER BY MAX(at) DESC
            LIMIT @take;
            """, new { sourceIds = GuidSql.ToJsonHexArray(sourceIds), take = Math.Clamp(take, 1, 500) })
            .Select(row => new LibraryActivityOrganiseRun(
                row.OperationId, row.SourceId, (int)row.Moved, (int)row.Copied, (int)row.KindChanged, (int)row.Failed,
                DateTimeOffset.Parse(row.LastAt, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        return Task.FromResult<IReadOnlyList<LibraryActivityOrganiseRun>>(rows);
    }

    // Plain row shapes for Dapper: counts come back as 64-bit numbers and times as text.
    private sealed class CheckRow
    {
        public string SourceId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public long Tried { get; set; }
        public long Unmatched { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string? EndedAt { get; set; }
    }

    private sealed class RunRow
    {
        public Guid OperationId { get; set; }
        public Guid SourceId { get; set; }
        public long Moved { get; set; }
        public long Copied { get; set; }
        public long KindChanged { get; set; }
        public long Failed { get; set; }
        public string LastAt { get; set; } = string.Empty;
    }
}
