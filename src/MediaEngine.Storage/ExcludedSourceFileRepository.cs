using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite list of media files that stayed on disk when their title was deleted from the library, so scans skip them.
/// One row per place (<c>source_id</c> and <c>relative_path</c>). Every call uses a short-lived connection.
/// </summary>
public sealed class ExcludedSourceFileRepository(IDatabaseConnection db) : IExcludedSourceFileRepository
{
    private const string SelectSql = """
        SELECT id AS Id,
               library_id AS LibraryId,
               source_id AS SourceId,
               relative_path AS RelativePath,
               content_hash AS ContentHash,
               file_size AS FileSize,
               last_write_utc AS LastWriteUtc,
               title AS Title,
               excluded_at AS ExcludedAt,
               excluded_by_account_id AS ExcludedByAccountId,
               excluded_by_name AS ExcludedByName,
               reason AS Reason
        FROM excluded_source_files
        """;

    public Task AddOrReplaceAsync(ExcludedSourceFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(file.LibraryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(file.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(file.RelativePath);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        // The row already at this place keeps its id; everything else describes the file as it is now.
        connection.Execute("""
            INSERT INTO excluded_source_files
                (id, library_id, source_id, relative_path, content_hash, file_size, last_write_utc, title,
                 excluded_at, excluded_by_account_id, excluded_by_name, reason)
            VALUES
                (@id, @libraryId, @sourceId, @relativePath, @contentHash, @fileSize, @lastWriteUtc, @title,
                 @excludedAt, @excludedByAccountId, @excludedByName, @reason)
            ON CONFLICT(source_id, relative_path) DO UPDATE SET
                library_id = excluded.library_id,
                content_hash = excluded.content_hash,
                file_size = excluded.file_size,
                last_write_utc = excluded.last_write_utc,
                title = excluded.title,
                excluded_at = excluded.excluded_at,
                excluded_by_account_id = excluded.excluded_by_account_id,
                excluded_by_name = excluded.excluded_by_name,
                reason = excluded.reason;
            """,
            new
            {
                id = file.Id,
                libraryId = file.LibraryId,
                sourceId = file.SourceId,
                relativePath = file.RelativePath,
                contentHash = file.ContentHash,
                fileSize = file.FileSize,
                lastWriteUtc = file.LastWriteUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                title = file.Title,
                excludedAt = file.ExcludedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                excludedByAccountId = file.ExcludedByAccountId,
                excludedByName = file.ExcludedByName,
                reason = file.Reason,
            });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExcludedSourceFile>> ListByLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(new CommandDefinition(
            SelectSql + " WHERE library_id = @libraryId ORDER BY excluded_at DESC, relative_path;",
            new { libraryId },
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ExcludedSourceFile>>([.. rows.Select(ToModel)]);
    }

    public Task<IReadOnlyList<ExcludedSourceFile>> ListAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(new CommandDefinition(
            SelectSql + " ORDER BY excluded_at DESC, relative_path;",
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ExcludedSourceFile>>([.. rows.Select(ToModel)]);
    }

    public Task<ExcludedSourceFile?> GetAsync(string sourceId, string relativePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
            SelectSql + " WHERE source_id = @sourceId AND relative_path = @relativePath;",
            new { sourceId, relativePath },
            cancellationToken: ct));
        return Task.FromResult(row is null ? null : ToModel(row));
    }

    public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var removed = connection.Execute(new CommandDefinition(
            "DELETE FROM excluded_source_files WHERE id = @id;",
            new { id },
            cancellationToken: ct));
        return Task.FromResult(removed > 0);
    }

    private static ExcludedSourceFile ToModel(Row row) => new(
        row.Id,
        row.LibraryId,
        row.SourceId,
        row.RelativePath,
        row.ContentHash,
        row.FileSize,
        ParseTime(row.LastWriteUtc),
        row.Title,
        ParseTime(row.ExcludedAt) ?? DateTimeOffset.MinValue,
        row.ExcludedByAccountId,
        row.ExcludedByName,
        row.Reason);

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string LibraryId { get; init; } = string.Empty;
        public string SourceId { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public string? ContentHash { get; init; }
        public long? FileSize { get; init; }
        public string? LastWriteUtc { get; init; }
        public string? Title { get; init; }
        public string? ExcludedAt { get; init; }
        public Guid? ExcludedByAccountId { get; init; }
        public string? ExcludedByName { get; init; }
        public string? Reason { get; init; }
    }
}
