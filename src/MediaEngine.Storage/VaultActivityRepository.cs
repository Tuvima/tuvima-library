using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IVaultActivityRepository"/>: the quiet Vault activity list (Videos spec D32).</summary>
public sealed class VaultActivityRepository(IDatabaseConnection database) : IVaultActivityRepository
{
    private const int MaxFileNameLength = 260;
    private const int MaxDetailLength = 200;

    public Task AddAsync(Guid assetId, string kind, string fileName, string detail, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!VaultActivityKinds.IsValid(kind))
        {
            throw new ArgumentException("Unknown Vault activity kind.", nameof(kind));
        }

        var name = Trim(fileName, MaxFileNameLength);
        var text = Trim(detail, MaxDetailLength);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                INSERT INTO vault_activity (id, asset_id, kind, file_name, detail, created_at)
                VALUES (@id, @assetId, @kind, @name, @text, @createdAt);
                """,
                new { id = Guid.NewGuid(), assetId, kind, name, text, createdAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
                transaction);
        }, ct);
    }

    public Task<IReadOnlyList<VaultActivityItem>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<Row>(
            """
            SELECT id AS Id, asset_id AS AssetId, kind AS Kind, file_name AS FileName, detail AS Detail, created_at AS CreatedAt
            FROM vault_activity ORDER BY created_at DESC, id LIMIT @limit;
            """,
            new { limit = Math.Clamp(limit, 1, 500) });
        return Task.FromResult<IReadOnlyList<VaultActivityItem>>(rows
            .Select(r => new VaultActivityItem(r.Id, r.AssetId, r.Kind, r.FileName, r.Detail, DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture)))
            .ToList());
    }

    private static string Trim(string value, int max)
    {
        var text = new string((value ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= max ? text : text[..max];
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid AssetId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }
}
