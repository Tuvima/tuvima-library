using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class CollectionPlacementRepository : ICollectionPlacementRepository
{
    private const string SelectPlacements =
        "SELECT id AS Id, collection_id AS CollectionId, location AS Location, position AS Position, " +
        "display_limit AS DisplayLimit, display_mode AS DisplayMode, is_visible AS IsVisible, " +
        "created_at AS CreatedAt, profile_id AS ProfileId FROM collection_placements ";

    private readonly IDatabaseConnection _db;

    public CollectionPlacementRepository(IDatabaseConnection db) => _db = db;

    public Task<IReadOnlyList<CollectionPlacement>> GetByCollectionIdAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var results = conn.Query<CollectionPlacement>(
            SelectPlacements + "WHERE collection_id = @CollectionId AND profile_id IS NULL ORDER BY position",
            new { CollectionId = collectionId });
        return Task.FromResult<IReadOnlyList<CollectionPlacement>>(results.ToList());
    }

    public Task<IReadOnlyList<CollectionPlacement>> GetByLocationAsync(string location, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var results = conn.Query<CollectionPlacement>(
            SelectPlacements + "WHERE location = @Location AND is_visible = 1 AND profile_id IS NULL ORDER BY position, created_at",
            new { Location = location });
        return Task.FromResult<IReadOnlyList<CollectionPlacement>>(results.ToList());
    }

    public Task<IReadOnlyList<CollectionPlacement>> GetSharedByLocationAsync(string location, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var results = conn.Query<CollectionPlacement>(
            SelectPlacements + "WHERE location = @Location AND profile_id IS NULL ORDER BY position, created_at",
            new { Location = location });
        return Task.FromResult<IReadOnlyList<CollectionPlacement>>(results.ToList());
    }

    public Task UpsertAsync(CollectionPlacement placement, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO collection_placements (id, collection_id, location, position, display_limit, display_mode, is_visible, created_at, profile_id)
            VALUES (@Id, @CollectionId, @Location, @Position, @DisplayLimit, @DisplayMode, @IsVisible, @CreatedAt, @ProfileId)
            ON CONFLICT(id) DO UPDATE SET
                location = excluded.location,
                position = excluded.position,
                display_limit = excluded.display_limit,
                display_mode = excluded.display_mode,
                is_visible = excluded.is_visible
            """,
            new
            {
                placement.Id,
                placement.CollectionId,
                placement.Location,
                placement.Position,
                placement.DisplayLimit,
                placement.DisplayMode,
                IsVisible = placement.IsVisible ? 1 : 0,
                CreatedAt = placement.CreatedAt.ToString("o"),
                placement.ProfileId,
            });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid placementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("DELETE FROM collection_placements WHERE id = @Id",
            new { Id = placementId });
        return Task.CompletedTask;
    }

    public Task DeleteByCollectionIdAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("DELETE FROM collection_placements WHERE collection_id = @CollectionId",
            new { CollectionId = collectionId });
        return Task.CompletedTask;
    }

    public Task SyncSharedLocationsAsync(
        Guid collectionId,
        IReadOnlyCollection<string> wantedLocations,
        IReadOnlyCollection<string> managedLocations,
        int displayLimit,
        CancellationToken ct = default)
    {
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            var existing = conn.Query<CollectionPlacement>(
                SelectPlacements + "WHERE collection_id = @CollectionId AND profile_id IS NULL",
                new { CollectionId = collectionId },
                tx).ToList();

            foreach (var location in managedLocations.Where(location => !wantedLocations.Contains(location, StringComparer.Ordinal)))
            {
                // A lane that stops being shown also forgets who hid it, so showing it again starts clean.
                conn.Execute(
                    "DELETE FROM collection_placements WHERE collection_id = @CollectionId AND location = @Location",
                    new { CollectionId = collectionId, Location = location },
                    tx);
            }

            foreach (var location in wantedLocations.Where(location =>
                         managedLocations.Contains(location, StringComparer.Ordinal)
                         && !existing.Any(row => string.Equals(row.Location, location, StringComparison.Ordinal))))
            {
                var next = conn.ExecuteScalar<int>(
                    "SELECT COALESCE(MAX(position), -1) + 1 FROM collection_placements WHERE location = @Location AND profile_id IS NULL",
                    new { Location = location },
                    tx);
                conn.Execute(
                    """
                    INSERT INTO collection_placements (id, collection_id, location, position, display_limit, display_mode, is_visible, created_at)
                    VALUES (@Id, @CollectionId, @Location, @Position, @DisplayLimit, 'swimlane', 1, @CreatedAt)
                    """,
                    new
                    {
                        Id = Guid.NewGuid(),
                        CollectionId = collectionId,
                        Location = location,
                        Position = next,
                        DisplayLimit = displayLimit,
                        CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                    },
                    tx);
            }
        }, ct);
    }

    public Task SaveSharedOrderAsync(
        string location,
        IReadOnlyList<(Guid CollectionId, bool Visible)> ordered,
        CancellationToken ct = default)
    {
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                conn.Execute(
                    """
                    UPDATE collection_placements
                    SET position = @Position, is_visible = @Visible
                    WHERE collection_id = @CollectionId AND location = @Location AND profile_id IS NULL
                    """,
                    new
                    {
                        Position = index,
                        Visible = ordered[index].Visible ? 1 : 0,
                        ordered[index].CollectionId,
                        Location = location,
                    },
                    tx);
            }
        }, ct);
    }

    public Task<IReadOnlySet<Guid>> GetHiddenCollectionIdsAsync(Guid profileId, string location, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var ids = conn.Query<Guid>(
            "SELECT collection_id FROM collection_placements WHERE profile_id = @ProfileId AND location = @Location AND is_visible = 0",
            new { ProfileId = profileId, Location = location });
        return Task.FromResult<IReadOnlySet<Guid>>(ids.ToHashSet());
    }

    public Task SetProfileHiddenAsync(Guid profileId, Guid collectionId, string location, bool hidden, CancellationToken ct = default)
    {
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute(
                "DELETE FROM collection_placements WHERE profile_id = @ProfileId AND collection_id = @CollectionId AND location = @Location",
                new { ProfileId = profileId, CollectionId = collectionId, Location = location },
                tx);
            if (hidden)
            {
                conn.Execute(
                    """
                    INSERT INTO collection_placements (id, collection_id, location, position, display_limit, display_mode, is_visible, created_at, profile_id)
                    VALUES (@Id, @CollectionId, @Location, 0, 0, 'swimlane', 0, @CreatedAt, @ProfileId)
                    """,
                    new
                    {
                        Id = Guid.NewGuid(),
                        CollectionId = collectionId,
                        Location = location,
                        CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                        ProfileId = profileId,
                    },
                    tx);
            }
        }, ct);
    }
}
