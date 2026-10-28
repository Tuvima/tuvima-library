using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Stores the Import and Rejected area records (Photo and video filing spec D1, D9, D15, D16): the tracked files, which double as the
/// filing journal, and the duplicate groups the cleanup job found.
/// </summary>
public sealed class ViewIntakeRepository(IDatabaseConnection database) : IViewIntakeRepository
{
    private const string FileColumns = """
        id AS Id, content_kind AS ContentKind, scope_kind AS ScopeKind, owner_profile_id AS OwnerProfileId,
        added_by_profile_id AS AddedByProfileId, original_name AS OriginalName, current_path AS CurrentPath,
        target_path AS TargetPath, filed_path AS FiledPath, group_key AS GroupKey, group_role AS GroupRole,
        byte_size AS ByteSize, content_hash AS ContentHash, state AS State, reason_code AS ReasonCode,
        reason_text AS ReasonText, created_at AS CreatedAt, updated_at AS UpdatedAt, expires_at AS ExpiresAt
        """;

    private const string GroupColumns = """
        id AS Id, scope_kind AS ScopeKind, library_id AS LibraryId, content_hash AS ContentHash, copy_count AS CopyCount,
        state AS State, first_found_at AS FirstFoundAt, last_seen_at AS LastSeenAt, dismissed_at AS DismissedAt,
        dismissed_by AS DismissedBy
        """;

    public Task<ViewIntakeFile> AddAsync(ViewIntakeFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = connection.QuerySingleOrDefault<ViewIntakeFile>(new CommandDefinition($"""
                SELECT {FileColumns} FROM view_intake_files
                 WHERE current_path = @CurrentPath COLLATE NOCASE AND state IN ('waiting', 'filing', 'rejected', 'stray');
                """, new { file.CurrentPath }, transaction, cancellationToken: token));
            if (existing is not null)
            {
                return existing;
            }

            connection.Execute(new CommandDefinition("""
                INSERT INTO view_intake_files
                    (id, content_kind, scope_kind, owner_profile_id, added_by_profile_id, original_name, current_path, target_path,
                     filed_path, group_key, group_role, byte_size, content_hash, state, reason_code, reason_text,
                     created_at, updated_at, expires_at)
                VALUES
                    (@Id, @ContentKind, @ScopeKind, @OwnerProfileId, @AddedByProfileId, @OriginalName, @CurrentPath, @TargetPath,
                     @FiledPath, @GroupKey, @GroupRole, @ByteSize, @ContentHash, @State, @ReasonCode, @ReasonText,
                     @CreatedAt, @UpdatedAt, @ExpiresAt);
                """, file, transaction, cancellationToken: token));
            return file;
        }, ct);
    }

    public Task<ViewIntakeFile?> GetByPathAsync(string currentPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<ViewIntakeFile>(new CommandDefinition($"""
            SELECT {FileColumns} FROM view_intake_files
             WHERE current_path = @currentPath COLLATE NOCASE
             ORDER BY CASE WHEN state IN ('waiting', 'filing', 'rejected', 'stray') THEN 0 ELSE 1 END, updated_at DESC;
            """, new { currentPath }, cancellationToken: ct));
        return Task.FromResult(row);
    }

    public Task<ViewIntakeFile?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<ViewIntakeFile>(new CommandDefinition(
            $"SELECT {FileColumns} FROM view_intake_files WHERE id = @id;", new { id }, cancellationToken: ct));
        return Task.FromResult(row);
    }

    public Task<IReadOnlyList<ViewIntakeFile>> GetByStateAsync(IReadOnlyCollection<string> states, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (states.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ViewIntakeFile>>([]);
        }

        using var connection = database.CreateConnection();
        var rows = connection.Query<ViewIntakeFile>(new CommandDefinition($"""
            SELECT {FileColumns} FROM view_intake_files
             WHERE state IN @states
             ORDER BY created_at, id
             LIMIT @limit;
            """, new { states = states.ToArray(), limit = Math.Max(1, limit) }, cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ViewIntakeFile>>(rows);
    }

    public Task<IReadOnlyList<ViewIntakeFile>> GetGroupAsync(string groupKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<ViewIntakeFile>(new CommandDefinition($"""
            SELECT {FileColumns} FROM view_intake_files WHERE group_key = @groupKey ORDER BY created_at, id;
            """, new { groupKey }, cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ViewIntakeFile>>(rows);
    }

    public Task<IReadOnlyList<ViewIntakeFile>> GetRejectedAsync(string? contentKind, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<ViewIntakeFile>(new CommandDefinition($"""
            SELECT {FileColumns} FROM view_intake_files
             WHERE state IN ('rejected', 'stray') AND (@contentKind IS NULL OR content_kind = @contentKind)
             ORDER BY updated_at DESC, id
             LIMIT @limit;
            """, new { contentKind, limit = Math.Max(1, limit) }, cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ViewIntakeFile>>(rows);
    }

    public Task<IReadOnlyList<ViewIntakeFile>> GetExpiredAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<ViewIntakeFile>(new CommandDefinition($"""
            SELECT {FileColumns} FROM view_intake_files
             WHERE state = 'rejected' AND expires_at IS NOT NULL AND expires_at <= @now
             ORDER BY expires_at, id
             LIMIT @limit;
            """, new { now = now.ToUniversalTime().ToString("o"), limit = Math.Max(1, limit) }, cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ViewIntakeFile>>(rows);
    }

    public Task<int> CountWaitingAsync(string? contentKind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var count = connection.ExecuteScalar<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM view_intake_files
             WHERE state IN ('waiting', 'filing') AND (@contentKind IS NULL OR content_kind = @contentKind);
            """, new { contentKind }, cancellationToken: ct));
        return Task.FromResult(count);
    }

    public Task UpdateAsync(
        Guid id,
        string state,
        string? currentPath = null,
        string? targetPath = null,
        string? filedPath = null,
        string? contentHash = null,
        string? reasonCode = null,
        string? reasonText = null,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition("""
                UPDATE view_intake_files
                   SET state = @state,
                       current_path = COALESCE(@currentPath, current_path),
                       target_path = COALESCE(@targetPath, target_path),
                       filed_path = COALESCE(@filedPath, filed_path),
                       content_hash = COALESCE(@contentHash, content_hash),
                       reason_code = COALESCE(@reasonCode, reason_code),
                       reason_text = COALESCE(@reasonText, reason_text),
                       expires_at = COALESCE(@expiresAt, expires_at),
                       updated_at = @now
                 WHERE id = @id;
                """, new
            {
                id,
                state,
                currentPath,
                targetPath,
                filedPath,
                contentHash,
                reasonCode,
                reasonText,
                expiresAt = expiresAt?.ToUniversalTime().ToString("o"),
                now = DateTimeOffset.UtcNow.ToUniversalTime().ToString("o"),
            }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task SetSizeAsync(Guid id, long byteSize, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition(
                "UPDATE view_intake_files SET byte_size = @byteSize, updated_at = @now WHERE id = @id;",
                new { id, byteSize, now = DateTimeOffset.UtcNow.ToUniversalTime().ToString("o") }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task ClearTargetAsync(Guid id, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition(
                "UPDATE view_intake_files SET target_path = NULL, updated_at = @now WHERE id = @id;",
                new { id, now = DateTimeOffset.UtcNow.ToUniversalTime().ToString("o") }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition(
                "DELETE FROM view_intake_files WHERE id = @id;", new { id }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task<int> PruneFinishedAsync(DateTimeOffset olderThan, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(new CommandDefinition("""
                DELETE FROM view_intake_files WHERE state IN ('filed', 'duplicate') AND updated_at < @olderThan;
                """, new { olderThan = olderThan.ToUniversalTime().ToString("o") }, transaction, cancellationToken: token));
        }, ct);

    // ── Duplicate groups ─────────────────────────────────────────────────────

    public Task UpsertDuplicateGroupAsync(
        Guid libraryId, string scopeKind, string contentHash, int copyCount, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var stamp = now.ToUniversalTime().ToString("o");
            // A dismissed group stays dismissed unless more copies appeared since it was dismissed.
            connection.Execute(new CommandDefinition("""
                INSERT INTO view_duplicate_groups
                    (id, scope_kind, library_id, content_hash, copy_count, state, first_found_at, last_seen_at)
                VALUES (@id, @scopeKind, @libraryId, @contentHash, @copyCount, 'open', @stamp, @stamp)
                ON CONFLICT (library_id, content_hash) DO UPDATE SET
                    state = CASE WHEN view_duplicate_groups.state = 'dismissed' AND excluded.copy_count > view_duplicate_groups.copy_count
                                 THEN 'open' ELSE view_duplicate_groups.state END,
                    dismissed_at = CASE WHEN view_duplicate_groups.state = 'dismissed' AND excluded.copy_count > view_duplicate_groups.copy_count
                                        THEN NULL ELSE view_duplicate_groups.dismissed_at END,
                    copy_count = excluded.copy_count,
                    last_seen_at = excluded.last_seen_at;
                """, new { id = Guid.NewGuid(), scopeKind, libraryId, contentHash, copyCount, stamp }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task<int> RemoveUnseenDuplicateGroupsAsync(string scopeKind, DateTimeOffset seenSince, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(new CommandDefinition("""
                DELETE FROM view_duplicate_groups WHERE scope_kind = @scopeKind AND last_seen_at < @seenSince;
                """, new { scopeKind, seenSince = seenSince.ToUniversalTime().ToString("o") }, transaction, cancellationToken: token));
        }, ct);

    public Task<IReadOnlyList<ViewDuplicateGroup>> GetDuplicateGroupsAsync(
        string? scopeKind, Guid? libraryId, bool includeDismissed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<ViewDuplicateGroup>(new CommandDefinition($"""
            SELECT {GroupColumns} FROM view_duplicate_groups
             WHERE (@scopeKind IS NULL OR scope_kind = @scopeKind)
               AND (@libraryId IS NULL OR library_id = @libraryId)
               AND (@includeDismissed = 1 OR state = 'open')
             ORDER BY copy_count DESC, first_found_at, id;
            """, new { scopeKind, libraryId, includeDismissed = includeDismissed ? 1 : 0 }, cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ViewDuplicateGroup>>(rows);
    }

    public Task<ViewDuplicateGroup?> GetDuplicateGroupAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<ViewDuplicateGroup>(new CommandDefinition(
            $"SELECT {GroupColumns} FROM view_duplicate_groups WHERE id = @id;", new { id }, cancellationToken: ct));
        return Task.FromResult(row);
    }

    public Task<bool> DismissDuplicateGroupAsync(Guid id, Guid dismissedBy, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(new CommandDefinition("""
                UPDATE view_duplicate_groups SET state = 'dismissed', dismissed_at = @now, dismissed_by = @dismissedBy
                 WHERE id = @id AND state = 'open';
                """, new { id, dismissedBy, now = now.ToUniversalTime().ToString("o") }, transaction, cancellationToken: token)) > 0;
        }, ct);
}
