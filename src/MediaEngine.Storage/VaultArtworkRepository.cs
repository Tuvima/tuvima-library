using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IVaultArtworkRepository"/>: Vault artwork records, kept apart from every normal artwork table.</summary>
public sealed class VaultArtworkRepository(IDatabaseConnection database) : IVaultArtworkRepository
{
    private const string Select = """
        SELECT id AS Id, owner_kind AS OwnerKind, owner_id AS OwnerId, artwork_kind AS Kind, file_path AS FilePath,
               content_hash AS ContentHash, width AS Width, height AS Height, source AS Source, created_at AS CreatedAt
        FROM vault_artwork
        """;

    public Task<Guid> AddAsync(string ownerKind, Guid ownerId, string kind, string filePath, string? contentHash, int? width, int? height, string? source, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!VaultArtworkOwnerKinds.IsValid(ownerKind))
        {
            throw new ArgumentException("Unknown Vault artwork owner.", nameof(ownerKind));
        }

        if (!VaultArtworkKinds.IsValid(kind))
        {
            throw new ArgumentException("Unknown Vault artwork kind.", nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var id = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO vault_artwork (id, owner_kind, owner_id, artwork_kind, file_path, content_hash, width, height, source, created_at)
                VALUES (@id, @ownerKind, @ownerId, @kind, @filePath, @contentHash, @width, @height, @source, @now);
                """,
                new { id, ownerKind, ownerId, kind, filePath, contentHash, width, height, source, now = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
                transaction);
            return id;
        }, ct);
    }

    public Task<VaultArtworkRecord?> GetAsync(Guid artworkId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<Row>(Select + " WHERE id = @artworkId;", new { artworkId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<IReadOnlyList<VaultArtworkRecord>> GetForOwnerAsync(string ownerKind, Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<Row>(
            Select + " WHERE owner_kind = @ownerKind AND owner_id = @ownerId ORDER BY created_at, id;", new { ownerKind, ownerId });
        return Task.FromResult<IReadOnlyList<VaultArtworkRecord>>(rows.Select(Map).ToList());
    }

    public Task<bool> DeleteAsync(Guid artworkId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("DELETE FROM vault_artwork WHERE id = @artworkId;", new { artworkId }, transaction) == 1;
        }, ct);

    private static VaultArtworkRecord Map(Row r) => new(
        r.Id, r.OwnerKind, r.OwnerId, r.Kind, r.FilePath, r.ContentHash, r.Width, r.Height, r.Source,
        DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture));

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string OwnerKind { get; set; } = string.Empty;
        public Guid OwnerId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string? ContentHash { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public string? Source { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
    }
}
