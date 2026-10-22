using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IVaultLibraryRepository"/>: the single list of Vault libraries.</summary>
public sealed class VaultLibraryRepository(IDatabaseConnection database) : IVaultLibraryRepository
{
    private const string Select = """
        SELECT library_id AS LibraryId, lookup_source AS LookupSource, lookup_server_id AS LookupServerId,
               adults_confirmed_at AS AdultsConfirmedAt, created_by_profile_id AS CreatedByProfileId,
               created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM vault_libraries
        """;

    public Task<IReadOnlyList<VaultLibrary>> GetAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<Row>(Select + " ORDER BY created_at, library_id;").Select(Map).ToList();
        return Task.FromResult<IReadOnlyList<VaultLibrary>>(rows);
    }

    public Task<IReadOnlySet<string>> GetIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var ids = connection.Query<string>("SELECT library_id FROM vault_libraries;")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlySet<string>>(ids);
    }

    public Task<bool> IsInVaultAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        // An asset id, an edition id, a work id, or a series or section whose episodes sit in a Vault library (security review L2, L3).
        var found = connection.ExecuteScalar<long>(VaultSql.IsVaultAnyEntityStatement, new { id = GuidSql.ToBlob(id) });
        return Task.FromResult(found != 0);
    }

    public Task<bool> HoldsOrdinaryFilesAsync(string folderPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var folder = Normalize(folderPath);
        if (folder.Length == 0)
        {
            return Task.FromResult(false);
        }

        using var connection = database.CreateConnection();
        var found = connection.ExecuteScalar<int>(
            """
            SELECT EXISTS(
                SELECT 1 FROM media_assets a
                WHERE (REPLACE(a.file_path_root, '\', '/') = @exact COLLATE NOCASE
                       OR REPLACE(a.file_path_root, '\', '/') LIKE @under ESCAPE '\')
                  AND NOT EXISTS (SELECT 1 FROM vault_libraries v WHERE v.library_id = a.library_id COLLATE NOCASE));
            """,
            new { exact = folder, under = EscapeLike(folder) + "/%" });
        return Task.FromResult(found == 1);
    }

    public Task<VaultLibrary?> GetAsync(string libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<Row>(Select + " WHERE library_id = @libraryId;", new { libraryId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<bool> AnyAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM vault_libraries);") == 1);
    }

    public Task AddAsync(VaultLibrary library, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (!VaultLookupSources.IsValid(library.LookupSource))
        {
            throw new ArgumentException("Unknown Vault lookup source.", nameof(library));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<int>(
                    "SELECT EXISTS(SELECT 1 FROM vault_libraries WHERE library_id = @id);",
                    new { id = library.LibraryId }, transaction) == 1)
            {
                throw new InvalidOperationException("That library is already a Vault library.");
            }

            // A library that already holds ordinary items cannot become a Vault: its items have already been shown and linked to shared
            // records. A Vault library starts empty.
            if (connection.ExecuteScalar<int>(
                    "SELECT EXISTS(SELECT 1 FROM media_assets WHERE library_id = @id COLLATE NOCASE);",
                    new { id = library.LibraryId }, transaction) == 1)
            {
                throw new InvalidOperationException("A library that already has items cannot become a Vault library.");
            }

            connection.Execute(
                """
                INSERT INTO vault_libraries
                    (library_id, lookup_source, lookup_server_id, adults_confirmed_at, created_by_profile_id, created_at, updated_at)
                VALUES (@LibraryId, @LookupSource, @LookupServerId, @AdultsConfirmedAt, @CreatedByProfileId, @CreatedAt, @UpdatedAt);
                """,
                new
                {
                    library.LibraryId,
                    library.LookupSource,
                    library.LookupServerId,
                    AdultsConfirmedAt = Iso(library.AdultsConfirmedAt),
                    library.CreatedByProfileId,
                    CreatedAt = Iso(library.CreatedAt),
                    UpdatedAt = Iso(library.UpdatedAt),
                }, transaction);
        }, ct);
    }

    public Task<bool> UpdateLookupAsync(string libraryId, string lookupSource, string? lookupServerId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!VaultLookupSources.IsValid(lookupSource))
        {
            throw new ArgumentException("Unknown Vault lookup source.", nameof(lookupSource));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(
                """
                UPDATE vault_libraries
                SET lookup_source = @lookupSource, lookup_server_id = @lookupServerId, updated_at = @updated
                WHERE library_id = @libraryId;
                """,
                new { libraryId, lookupSource, lookupServerId, updated = Iso(now) }, transaction) == 1;
        }, ct);
    }

    public Task<bool> RemoveAsync(string libraryId, CancellationToken ct = default) => RemoveAsync(libraryId, [], ct);

    public Task<bool> RemoveAsync(string libraryId, IReadOnlyCollection<string> sourcePaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<int>(
                    "SELECT EXISTS(SELECT 1 FROM media_assets WHERE library_id = @libraryId COLLATE NOCASE);",
                    new { libraryId }, transaction) == 1)
            {
                throw new InvalidOperationException("A Vault library that still has items cannot be removed.");
            }

            // Records that are Vault only by the library id or by where the file sat stop being recognised the moment the mark is
            // gone, so they go with it (Videos spec D17, security review L1).
            if (connection.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM vault_libraries WHERE library_id = @libraryId);", new { libraryId }, transaction) == 1)
            {
                connection.Execute("DELETE FROM excluded_source_files WHERE library_id = @libraryId COLLATE NOCASE;", new { libraryId }, transaction);
                foreach (var path in sourcePaths.Select(Normalize).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var parameters = new { exact = path, under = EscapeLike(path) + "/%" };
                    connection.Execute(
                        """
                        DELETE FROM media_operation_events WHERE operation_id IN (
                            SELECT id FROM media_operations
                            WHERE REPLACE(source_path, '\', '/') = @exact COLLATE NOCASE
                               OR REPLACE(source_path, '\', '/') LIKE @under ESCAPE '\');
                        """, parameters, transaction);
                    connection.Execute(
                        """
                        DELETE FROM media_operations
                        WHERE REPLACE(source_path, '\', '/') = @exact COLLATE NOCASE
                           OR REPLACE(source_path, '\', '/') LIKE @under ESCAPE '\';
                        """, parameters, transaction);
                }
            }

            return connection.Execute("DELETE FROM vault_libraries WHERE library_id = @libraryId;", new { libraryId }, transaction) == 1;
        }, ct);
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    internal static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static VaultLibrary Map(Row row) => new(
        row.LibraryId,
        row.LookupSource,
        row.LookupServerId,
        DateTimeOffset.Parse(row.AdultsConfirmedAt, CultureInfo.InvariantCulture),
        row.CreatedByProfileId,
        DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture));

    private sealed class Row
    {
        public string LibraryId { get; set; } = string.Empty;
        public string LookupSource { get; set; } = VaultLookupSources.None;
        public string? LookupServerId { get; set; }
        public string AdultsConfirmedAt { get; set; } = string.Empty;
        public Guid? CreatedByProfileId { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
