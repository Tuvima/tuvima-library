using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IVaultNameRepository"/>. Performers, studios and tags live in tables of their own;
/// nothing here reads or writes the shared <c>persons</c>, tag or collection records.
/// </summary>
public sealed class VaultNameRepository(IDatabaseConnection database) : IVaultNameRepository
{
    public Task<Guid> EnsurePerformerAsync(string libraryId, string name, string? sortName, string? source, DateTimeOffset now, CancellationToken ct = default)
    {
        var clean = RequireName(name);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireVaultLibrary(connection, transaction, libraryId);
            var existing = connection.ExecuteScalar<Guid?>(
                "SELECT id FROM vault_performers WHERE library_id = @libraryId AND name = @name;",
                new { libraryId, name = clean }, transaction);
            if (existing is { } found)
            {
                return found;
            }

            var id = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO vault_performers (id, library_id, name, sort_name, source, created_at, updated_at)
                VALUES (@id, @libraryId, @name, @sortName, @source, @now, @now);
                """,
                new { id, libraryId, name = clean, sortName, source, now = Iso(now) }, transaction);
            return id;
        }, ct);
    }

    public Task<Guid> EnsureStudioAsync(string libraryId, string name, string? source, DateTimeOffset now, CancellationToken ct = default)
    {
        var clean = RequireName(name);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireVaultLibrary(connection, transaction, libraryId);
            var existing = connection.ExecuteScalar<Guid?>(
                "SELECT id FROM vault_studios WHERE library_id = @libraryId AND name = @name;",
                new { libraryId, name = clean }, transaction);
            if (existing is { } found)
            {
                return found;
            }

            var id = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO vault_studios (id, library_id, name, source, created_at, updated_at)
                VALUES (@id, @libraryId, @name, @source, @now, @now);
                """,
                new { id, libraryId, name = clean, source, now = Iso(now) }, transaction);
            return id;
        }, ct);
    }

    public Task<Guid> EnsureTagAsync(string libraryId, string name, DateTimeOffset now, CancellationToken ct = default)
    {
        var clean = RequireName(name);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            RequireVaultLibrary(connection, transaction, libraryId);
            var existing = connection.ExecuteScalar<Guid?>(
                "SELECT id FROM vault_tags WHERE library_id = @libraryId AND name = @name;",
                new { libraryId, name = clean }, transaction);
            if (existing is { } found)
            {
                return found;
            }

            var id = Guid.NewGuid();
            connection.Execute(
                "INSERT INTO vault_tags (id, library_id, name, created_at, updated_at) VALUES (@id, @libraryId, @name, @now, @now);",
                new { id, libraryId, name = clean, now = Iso(now) }, transaction);
            return id;
        }, ct);
    }

    public Task LinkPerformerAsync(Guid assetId, Guid performerId, CancellationToken ct = default) =>
        Link("vault_asset_performers", "performer_id", assetId, performerId, "vault_performers", ct);

    public Task LinkStudioAsync(Guid assetId, Guid studioId, CancellationToken ct = default) =>
        Link("vault_asset_studios", "studio_id", assetId, studioId, "vault_studios", ct);

    public Task LinkTagAsync(Guid assetId, Guid tagId, CancellationToken ct = default) =>
        Link("vault_asset_tags", "tag_id", assetId, tagId, "vault_tags", ct);

    public Task UnlinkAllAsync(Guid assetId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM vault_asset_performers WHERE asset_id = @assetId;", new { assetId }, transaction);
            connection.Execute("DELETE FROM vault_asset_studios WHERE asset_id = @assetId;", new { assetId }, transaction);
            connection.Execute("DELETE FROM vault_asset_tags WHERE asset_id = @assetId;", new { assetId }, transaction);
        }, ct);

    public Task<IReadOnlyList<VaultPerformer>> GetPerformersAsync(string? libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<PerformerRow>(
            PerformerSelect + " WHERE (@libraryId IS NULL OR library_id = @libraryId) ORDER BY name;", new { libraryId });
        return Task.FromResult<IReadOnlyList<VaultPerformer>>(rows.Select(MapPerformer).ToList());
    }

    public Task<IReadOnlyList<VaultStudio>> GetStudiosAsync(string? libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<StudioRow>(
            StudioSelect + " WHERE (@libraryId IS NULL OR library_id = @libraryId) ORDER BY name;", new { libraryId });
        return Task.FromResult<IReadOnlyList<VaultStudio>>(rows.Select(MapStudio).ToList());
    }

    public Task<IReadOnlyList<VaultTag>> GetTagsAsync(string? libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<TagRow>(
            TagSelect + " WHERE (@libraryId IS NULL OR library_id = @libraryId) ORDER BY name;", new { libraryId });
        return Task.FromResult<IReadOnlyList<VaultTag>>(rows.Select(MapTag).ToList());
    }

    public Task<IReadOnlyList<VaultPerformer>> GetPerformersOfAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<PerformerRow>(
            PerformerSelect + " WHERE id IN (SELECT performer_id FROM vault_asset_performers WHERE asset_id = @assetId) ORDER BY name;",
            new { assetId });
        return Task.FromResult<IReadOnlyList<VaultPerformer>>(rows.Select(MapPerformer).ToList());
    }

    public Task<IReadOnlyList<VaultStudio>> GetStudiosOfAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<StudioRow>(
            StudioSelect + " WHERE id IN (SELECT studio_id FROM vault_asset_studios WHERE asset_id = @assetId) ORDER BY name;",
            new { assetId });
        return Task.FromResult<IReadOnlyList<VaultStudio>>(rows.Select(MapStudio).ToList());
    }

    public Task<IReadOnlyList<VaultTag>> GetTagsOfAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<TagRow>(
            TagSelect + " WHERE id IN (SELECT tag_id FROM vault_asset_tags WHERE asset_id = @assetId) ORDER BY name;",
            new { assetId });
        return Task.FromResult<IReadOnlyList<VaultTag>>(rows.Select(MapTag).ToList());
    }

    public Task<IReadOnlyList<Guid>> GetAssetIdsOfPerformerAsync(Guid performerId, CancellationToken ct = default) =>
        AssetIds("SELECT asset_id FROM vault_asset_performers WHERE performer_id = @id ORDER BY asset_id;", performerId, ct);

    public Task<IReadOnlyList<Guid>> GetAssetIdsOfStudioAsync(Guid studioId, CancellationToken ct = default) =>
        AssetIds("SELECT asset_id FROM vault_asset_studios WHERE studio_id = @id ORDER BY asset_id;", studioId, ct);

    public Task<IReadOnlyList<Guid>> GetAssetIdsOfTagAsync(Guid tagId, CancellationToken ct = default) =>
        AssetIds("SELECT asset_id FROM vault_asset_tags WHERE tag_id = @id ORDER BY asset_id;", tagId, ct);

    private Task<IReadOnlyList<Guid>> AssetIds(string sql, Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult<IReadOnlyList<Guid>>(connection.Query<Guid>(sql, new { id }).ToList());
    }

    private Task Link(string table, string column, Guid assetId, Guid nameId, string nameTable, CancellationToken ct) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // A name and a scene must sit in the same Vault library, so a link can never carry a name across libraries.
            var sameLibrary = connection.ExecuteScalar<int>(
                $"""
                SELECT EXISTS(
                    SELECT 1 FROM media_assets ma JOIN {nameTable} n ON n.id = @nameId
                    WHERE ma.id = @assetId AND ma.library_id = n.library_id);
                """,
                new { assetId, nameId }, transaction);
            if (sameLibrary != 1)
            {
                throw new InvalidOperationException("A Vault name can only be linked to a scene in the same Vault library.");
            }

            connection.Execute(
                $"INSERT OR IGNORE INTO {table} (asset_id, {column}) VALUES (@assetId, @nameId);",
                new { assetId, nameId }, transaction);
        }, ct);

    private static void RequireVaultLibrary(SqliteConnection connection, SqliteTransaction transaction, string libraryId)
    {
        if (connection.ExecuteScalar<int>(
                "SELECT EXISTS(SELECT 1 FROM vault_libraries WHERE library_id = @libraryId);",
                new { libraryId }, transaction) != 1)
        {
            throw new InvalidOperationException("Vault names belong to a Vault library.");
        }
    }

    private static string RequireName(string name)
    {
        var clean = name?.Trim();
        return string.IsNullOrWhiteSpace(clean) ? throw new ArgumentException("A name is required.", nameof(name)) : clean;
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private const string PerformerSelect =
        "SELECT id AS Id, library_id AS LibraryId, name AS Name, sort_name AS SortName, source AS Source, created_at AS CreatedAt, updated_at AS UpdatedAt FROM vault_performers";

    private const string StudioSelect =
        "SELECT id AS Id, library_id AS LibraryId, name AS Name, source AS Source, created_at AS CreatedAt, updated_at AS UpdatedAt FROM vault_studios";

    private const string TagSelect =
        "SELECT id AS Id, library_id AS LibraryId, name AS Name, created_at AS CreatedAt, updated_at AS UpdatedAt FROM vault_tags";

    private static VaultPerformer MapPerformer(PerformerRow r) => new(r.Id, r.LibraryId, r.Name, r.SortName, r.Source, Parse(r.CreatedAt), Parse(r.UpdatedAt));

    private static VaultStudio MapStudio(StudioRow r) => new(r.Id, r.LibraryId, r.Name, r.Source, Parse(r.CreatedAt), Parse(r.UpdatedAt));

    private static VaultTag MapTag(TagRow r) => new(r.Id, r.LibraryId, r.Name, Parse(r.CreatedAt), Parse(r.UpdatedAt));

    private sealed class PerformerRow
    {
        public Guid Id { get; set; }
        public string LibraryId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? SortName { get; set; }
        public string? Source { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }

    private sealed class StudioRow
    {
        public Guid Id { get; set; }
        public string LibraryId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Source { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }

    private sealed class TagRow
    {
        public Guid Id { get; set; }
        public string LibraryId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
