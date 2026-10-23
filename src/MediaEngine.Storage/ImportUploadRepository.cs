using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite store of the resumable uploads into the Import folder (<c>import_uploads</c> and <c>import_upload_pieces</c>). Every call uses
/// a short-lived connection. A piece is recorded with a single insert that ignores a piece already there, so a resent piece is harmless
/// and two pieces arriving together never overwrite each other.
/// </summary>
public sealed class ImportUploadRepository(IDatabaseConnection db) : IImportUploadRepository
{
    private const string SelectSql = """
        SELECT id AS Id,
               created_by AS CreatedBy,
               kind_key AS KindKey,
               library_id AS LibraryId,
               type_label AS TypeLabel,
               file_name AS FileName,
               total_bytes AS TotalBytes,
               piece_bytes AS PieceBytes,
               piece_count AS PieceCount,
               working_path AS WorkingPath,
               destination_folder AS DestinationFolder,
               state AS State,
               final_path AS FinalPath,
               created_at AS CreatedAt,
               last_piece_at AS LastPieceAt,
               closed_at AS ClosedAt
        FROM import_uploads
        """;

    public Task CreateAsync(ImportUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        connection.Execute(new CommandDefinition(
            """
            INSERT INTO import_uploads
                (id, created_by, kind_key, library_id, type_label, file_name, total_bytes, piece_bytes, piece_count,
                 working_path, destination_folder, state, final_path, created_at, last_piece_at, closed_at)
            VALUES
                (@id, @createdBy, @kindKey, @libraryId, @typeLabel, @fileName, @totalBytes, @pieceBytes, @pieceCount,
                 @workingPath, @destinationFolder, @state, @finalPath, @createdAt, @lastPieceAt, @closedAt);
            """,
            new
            {
                id = upload.Id,
                createdBy = upload.CreatedBy,
                kindKey = upload.KindKey,
                libraryId = upload.LibraryId,
                typeLabel = upload.TypeLabel,
                fileName = upload.FileName,
                totalBytes = upload.TotalBytes,
                pieceBytes = upload.PieceBytes,
                pieceCount = upload.PieceCount,
                workingPath = upload.WorkingPath,
                destinationFolder = upload.DestinationFolder,
                state = upload.State,
                finalPath = upload.FinalPath,
                createdAt = Format(upload.CreatedAt),
                lastPieceAt = Format(upload.LastPieceAt),
                closedAt = upload.ClosedAt is { } closed ? Format(closed) : null,
            },
            cancellationToken: ct));
        return Task.CompletedTask;
    }

    public Task<ImportUpload?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
            SelectSql + " WHERE id = @id;",
            new { id },
            cancellationToken: ct));
        return Task.FromResult(row is null ? null : ToModel(row));
    }

    public Task<IReadOnlyList<int>> ListPiecesAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var pieces = connection.Query<int>(new CommandDefinition(
            "SELECT piece_index FROM import_upload_pieces WHERE upload_id = @id ORDER BY piece_index;",
            new { id },
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<int>>([.. pieces]);
    }

    public Task<bool> RecordPieceAsync(Guid id, int index, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var stamp = Format(at);
        var added = connection.Execute(new CommandDefinition(
            """
            INSERT OR IGNORE INTO import_upload_pieces (upload_id, piece_index, received_at)
            SELECT id, @index, @stamp FROM import_uploads WHERE id = @id AND state = 'open' AND @index >= 0 AND @index < piece_count;
            """,
            new { id, index, stamp },
            cancellationToken: ct));
        if (added > 0)
        {
            connection.Execute(new CommandDefinition(
                "UPDATE import_uploads SET last_piece_at = @stamp WHERE id = @id AND state = 'open';",
                new { id, stamp },
                cancellationToken: ct));
        }

        return Task.FromResult(added > 0);
    }

    public Task<bool> CloseAsync(Guid id, string state, string? finalPath, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        if (state is not (ImportUpload.CompletedState or ImportUpload.CancelledState))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "An upload closes as completed or cancelled.");
        }

        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var closed = connection.Execute(new CommandDefinition(
            "UPDATE import_uploads SET state = @state, final_path = @finalPath, closed_at = @stamp WHERE id = @id AND state = 'open';",
            new { id, state, finalPath, stamp = Format(at) },
            cancellationToken: ct));
        return Task.FromResult(closed > 0);
    }

    public Task<IReadOnlyList<ImportUpload>> ListIdleOpenAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(new CommandDefinition(
            SelectSql + " WHERE state = 'open' AND last_piece_at < @cutoff ORDER BY last_piece_at;",
            new { cutoff = Format(cutoff) },
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ImportUpload>>([.. rows.Select(ToModel)]);
    }

    public Task<int> DeleteClosedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var deleted = connection.Execute(new CommandDefinition(
            "DELETE FROM import_uploads WHERE state <> 'open' AND closed_at IS NOT NULL AND closed_at < @cutoff;",
            new { cutoff = Format(cutoff) },
            cancellationToken: ct));
        return Task.FromResult(deleted);
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static ImportUpload ToModel(Row row) => new(
        row.Id,
        row.CreatedBy,
        row.KindKey,
        row.LibraryId,
        row.TypeLabel,
        row.FileName,
        row.TotalBytes,
        row.PieceBytes,
        (int)row.PieceCount,
        row.WorkingPath,
        row.DestinationFolder,
        row.State,
        row.FinalPath,
        Parse(row.CreatedAt),
        Parse(row.LastPieceAt),
        row.ClosedAt is null ? null : Parse(row.ClosedAt));

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid CreatedBy { get; init; }
        public string KindKey { get; init; } = string.Empty;
        public string? LibraryId { get; init; }
        public string TypeLabel { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long TotalBytes { get; init; }
        public long PieceBytes { get; init; }
        public long PieceCount { get; init; }
        public string WorkingPath { get; init; } = string.Empty;
        public string DestinationFolder { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string? FinalPath { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
        public string LastPieceAt { get; init; } = string.Empty;
        public string? ClosedAt { get; init; }
    }
}
