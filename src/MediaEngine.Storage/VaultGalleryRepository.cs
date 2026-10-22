using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IVaultGalleryRepository"/>: Vault galleries made by hand or by a rule (Videos spec D44). They share
/// View's rule document but live in tables of their own and only ever hold Vault images, so a View gallery can never show a Vault image
/// and a Vault gallery can never show a View one.
/// </summary>
public sealed class VaultGalleryRepository(IDatabaseConnection database) : IVaultGalleryRepository
{
    public const int MaximumNameLength = 120;
    public const int MaximumDescriptionLength = 1000;

    private const string Select = """
        SELECT id AS Id, library_id AS LibraryId, name AS Name, description AS Description, gallery_kind AS Kind,
               smart_rule_json AS SmartRuleJson, cover_item_id AS CoverItemId, sort_order AS SortOrder,
               created_at AS CreatedAt, updated_at AS UpdatedAt
          FROM vault_galleries
        """;

    public Task<IReadOnlyList<VaultGallery>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<Row>(Select + " ORDER BY sort_order, name COLLATE NOCASE, id;").Select(Map).ToList();
        return Task.FromResult<IReadOnlyList<VaultGallery>>(rows);
    }

    public Task<VaultGallery?> GetAsync(Guid galleryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<Row>(Select + " WHERE id = @galleryId;", new { galleryId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<VaultGallery> CreateAsync(
        Guid libraryId, string name, string? description, VaultGalleryKind kind, string? smartRuleJson, Guid? createdByProfileId,
        CancellationToken ct = default)
    {
        var cleanName = CleanName(name);
        var cleanDescription = CleanDescription(description);
        if ((kind == VaultGalleryKind.Smart) == string.IsNullOrWhiteSpace(smartRuleJson))
        {
            throw new ArgumentException("A smart gallery needs a rule and a hand-made gallery cannot have one.", nameof(smartRuleJson));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM vault_image_libraries WHERE library_id = @libraryId;", new { libraryId }, transaction) == 0)
            {
                throw new InvalidOperationException("That is not a Vault image library.");
            }

            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var order = connection.ExecuteScalar<long>(
                "SELECT COALESCE(MAX(sort_order), 0) + 1 FROM vault_galleries WHERE library_id = @libraryId;", new { libraryId }, transaction);
            connection.Execute("""
                INSERT INTO vault_galleries
                    (id, library_id, created_by_profile_id, name, description, gallery_kind, smart_rule_json, sort_order, created_at, updated_at)
                VALUES
                    (@id, @libraryId, @createdByProfileId, @cleanName, @cleanDescription, @kind, @smartRuleJson, @order, @now, @now);
                """, new
            {
                id,
                libraryId,
                createdByProfileId,
                cleanName,
                cleanDescription,
                kind = kind == VaultGalleryKind.Smart ? "smart" : "manual",
                smartRuleJson = kind == VaultGalleryKind.Smart ? smartRuleJson : null,
                order,
                now,
            }, transaction);
            return Map(connection.QuerySingle<Row>(Select + " WHERE id = @id;", new { id }, transaction));
        }, ct);
    }

    public Task<VaultGallery?> UpdateAsync(
        Guid galleryId, string name, string? description, string? smartRuleJson, Guid? coverItemId, CancellationToken ct = default)
    {
        var cleanName = CleanName(name);
        var cleanDescription = CleanDescription(description);
        return database.ExecuteWriteAsync<VaultGallery?>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var kind = connection.QuerySingleOrDefault<string>(
                "SELECT gallery_kind FROM vault_galleries WHERE id = @galleryId;", new { galleryId }, transaction);
            if (kind is null)
            {
                return null;
            }

            if (kind == "smart" && string.IsNullOrWhiteSpace(smartRuleJson))
            {
                // No new rule: the saved one stays.
                smartRuleJson = connection.QuerySingle<string>(
                    "SELECT smart_rule_json FROM vault_galleries WHERE id = @galleryId;", new { galleryId }, transaction);
            }
            else if (kind == "manual")
            {
                smartRuleJson = null;
            }

            connection.Execute("""
                UPDATE vault_galleries
                   SET name = @cleanName, description = @cleanDescription, smart_rule_json = @smartRuleJson,
                       cover_item_id = @coverItemId, updated_at = @now
                 WHERE id = @galleryId;
                """, new { galleryId, cleanName, cleanDescription, smartRuleJson, coverItemId, now = DateTimeOffset.UtcNow }, transaction);
            return Map(connection.QuerySingle<Row>(Select + " WHERE id = @galleryId;", new { galleryId }, transaction));
        }, ct);
    }

    public Task<bool> DeleteAsync(Guid galleryId, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("DELETE FROM vault_galleries WHERE id = @galleryId;", new { galleryId }, transaction) > 0;
        }, ct);

    public Task<int> AddItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var kind = connection.QuerySingleOrDefault<string>(
                "SELECT gallery_kind FROM vault_galleries WHERE id = @galleryId;", new { galleryId }, transaction);
            if (kind != "manual")
            {
                return 0;
            }

            var added = 0;
            var now = DateTimeOffset.UtcNow;
            var position = connection.ExecuteScalar<long>(
                "SELECT COALESCE(MAX(position), 0) FROM vault_gallery_items WHERE gallery_id = @galleryId;", new { galleryId }, transaction);
            foreach (var itemId in itemIds.Where(id => id != Guid.Empty).Distinct())
            {
                token.ThrowIfCancellationRequested();
                // Only an image of the gallery's own Vault library goes in; anything else is left out without a word about why.
                var inLibrary = connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM vault_image_items vi JOIN vault_galleries g ON g.library_id = vi.library_id
                     WHERE vi.item_id = @itemId AND g.id = @galleryId;
                    """, new { itemId, galleryId }, transaction);
                if (inLibrary == 0)
                {
                    continue;
                }

                position++;
                added += connection.Execute("""
                    INSERT OR IGNORE INTO vault_gallery_items (gallery_id, item_id, position, added_at)
                    VALUES (@galleryId, @itemId, @position, @now);
                    """, new { galleryId, itemId, position, now }, transaction);
            }

            if (added > 0)
            {
                connection.Execute("UPDATE vault_galleries SET updated_at = @now WHERE id = @galleryId;", new { galleryId, now }, transaction);
            }

            return added;
        }, ct);
    }

    public Task<int> RemoveItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var removed = 0;
            foreach (var itemId in itemIds.Where(id => id != Guid.Empty).Distinct())
            {
                token.ThrowIfCancellationRequested();
                removed += connection.Execute(
                    "DELETE FROM vault_gallery_items WHERE gallery_id = @galleryId AND item_id = @itemId;",
                    new { galleryId, itemId }, transaction);
            }

            if (removed > 0)
            {
                connection.Execute(
                    "UPDATE vault_galleries SET updated_at = @now WHERE id = @galleryId;",
                    new { galleryId, now = DateTimeOffset.UtcNow }, transaction);
            }

            return removed;
        }, ct);
    }

    public Task<int> CountManualItemsAsync(Guid galleryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult((int)connection.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM vault_gallery_items vgi JOIN local_items li ON li.id = vgi.item_id
             WHERE vgi.gallery_id = @galleryId AND li.trashed_at IS NULL AND li.archived_at IS NULL;
            """, new { galleryId }));
    }

    private static string CleanName(string name)
    {
        var clean = name?.Trim() ?? string.Empty;
        return clean.Length is 0 or > MaximumNameLength
            ? throw new ArgumentException($"A gallery name needs between 1 and {MaximumNameLength} characters.", nameof(name))
            : clean;
    }

    private static string? CleanDescription(string? description)
    {
        var clean = description?.Trim();
        return string.IsNullOrEmpty(clean)
            ? null
            : clean.Length > MaximumDescriptionLength
                ? throw new ArgumentException($"A description can have up to {MaximumDescriptionLength} characters.", nameof(description))
                : clean;
    }

    private static VaultGallery Map(Row row) => new(
        row.Id, row.LibraryId, row.Name, row.Description,
        row.Kind == "smart" ? VaultGalleryKind.Smart : VaultGalleryKind.Manual,
        row.SmartRuleJson, row.CoverItemId, row.SortOrder, row.CreatedAt, row.UpdatedAt);

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid LibraryId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string Kind { get; init; } = "manual";
        public string? SmartRuleJson { get; init; }
        public Guid? CoverItemId { get; init; }
        public int SortOrder { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }
}
