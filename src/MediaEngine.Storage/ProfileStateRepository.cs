using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class ProfileStateRepository(IDatabaseConnection db) : IProfileStateRepository
{
    public Task<IReadOnlyList<ProfileSavedItem>> GetSavedItemsAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<SavedRow>(new CommandDefinition(
            """
            SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                   saved_at AS SavedAt, position AS Position
            FROM profile_saved_items
            WHERE profile_id = @profileId
            ORDER BY CASE WHEN position IS NULL THEN 1 ELSE 0 END, position, saved_at DESC, entity_id;
            """,
            new { profileId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ProfileSavedItem>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<ProfileSavedItem>> GetSavedItemsAsync(Guid profileId, ProfileEntityKind entityKind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<SavedRow>(new CommandDefinition(
            """
            SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                   saved_at AS SavedAt, position AS Position
            FROM profile_saved_items
            WHERE profile_id = @profileId AND entity_kind = @entityKind
            ORDER BY CASE WHEN position IS NULL THEN 1 ELSE 0 END, position, saved_at DESC, entity_id;
            """,
            new { profileId, entityKind = entityKind.ToString() }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ProfileSavedItem>>(rows.Select(Map).ToList());
    }

    public Task<ProfileSavedItem?> GetSavedItemAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<SavedRow>(new CommandDefinition(
            """
            SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                   saved_at AS SavedAt, position AS Position
            FROM profile_saved_items
            WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;
            """,
            new { profileId, entityKind = entityKind.ToString(), entityId }, cancellationToken: ct));
        return Task.FromResult<ProfileSavedItem?>(row is null ? null : Map(row));
    }

    public Task<ProfileSavedItem> SaveItemAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        int? position = null,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        RequireProfile(connection, transaction, profileId, token);
        RequireTarget(connection, transaction, entityKind, entityId, token);
        var now = DateTimeOffset.UtcNow;
        var alreadySaved = connection.ExecuteScalar<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM profile_saved_items WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;",
            new { profileId, entityKind = entityKind.ToString(), entityId }, transaction, cancellationToken: token)) > 0;
        if (position is null && !alreadySaved && entityKind == ProfileEntityKind.Song)
        {
            // A song hearted just now goes to the top of Favorites: one place before the first saved position. A song that is already a
            // Favorite keeps its place (the conflict clause below leaves its position alone when none is given).
            position = connection.ExecuteScalar<int?>(new CommandDefinition(
                """
                SELECT MIN(position) - 1 FROM profile_saved_items
                WHERE profile_id = @profileId AND entity_kind = 'Song';
                """,
                new { profileId }, transaction, cancellationToken: token)) ?? 0;
        }

        connection.Execute(new CommandDefinition(
            """
            INSERT INTO profile_saved_items(profile_id, entity_kind, entity_id, saved_at, position)
            VALUES (@profileId, @entityKind, @entityId, @savedAt, @position)
            ON CONFLICT(profile_id, entity_kind, entity_id) DO UPDATE SET
                position = COALESCE(excluded.position, profile_saved_items.position);
            """,
            new
            {
                profileId,
                entityKind = entityKind.ToString(),
                entityId,
                savedAt = now.ToString("O"),
                position,
            }, transaction, cancellationToken: token));

        var row = connection.QuerySingle<SavedRow>(new CommandDefinition(
            """
            SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                   saved_at AS SavedAt, position AS Position
            FROM profile_saved_items
            WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;
            """,
            new { profileId, entityKind = entityKind.ToString(), entityId }, transaction, cancellationToken: token));
        return Map(row);
    }, ct);

    public Task<bool> RemoveSavedItemAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
        connection.Execute(new CommandDefinition(
            "DELETE FROM profile_saved_items WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;",
            new { profileId, entityKind = entityKind.ToString(), entityId }, transaction, cancellationToken: token)) > 0, ct);

    public Task<SavedItemMoveResult> MoveSavedItemAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        int toIndex,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        var ordered = connection.Query<Guid>(new CommandDefinition(
            """
            SELECT entity_id FROM profile_saved_items
            WHERE profile_id = @profileId AND entity_kind = @entityKind
            ORDER BY CASE WHEN position IS NULL THEN 1 ELSE 0 END, position, saved_at DESC, entity_id;
            """,
            new { profileId, entityKind = entityKind.ToString() }, transaction, cancellationToken: token)).ToList();

        var from = ordered.IndexOf(entityId);
        if (from < 0)
        {
            return SavedItemMoveResult.NotSaved;
        }

        if (toIndex < 0 || toIndex >= ordered.Count)
        {
            return SavedItemMoveResult.IndexOutOfRange;
        }

        ordered.RemoveAt(from);
        ordered.Insert(toIndex, entityId);
        WriteSavedOrder(connection, transaction, profileId, entityKind, ordered, token);
        return SavedItemMoveResult.Moved;
    }, ct);

    public Task<SavedItemMoveResult> MoveSavedItemBeforeAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        Guid? beforeEntityId,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        // The order is read and the neighbour found inside this one write transaction, so a heart landing meanwhile cannot shift the target.
        var ordered = ReadSavedOrder(connection, transaction, profileId, entityKind, token);
        if (!ordered.Contains(entityId) || (beforeEntityId is { } neighbour && !ordered.Contains(neighbour)))
        {
            return SavedItemMoveResult.NotSaved;
        }

        if (beforeEntityId == entityId)
        {
            return SavedItemMoveResult.Moved;
        }

        ordered.Remove(entityId);
        ordered.Insert(beforeEntityId is { } before ? ordered.IndexOf(before) : ordered.Count, entityId);
        WriteSavedOrder(connection, transaction, profileId, entityKind, ordered, token);
        return SavedItemMoveResult.Moved;
    }, ct);

    private static List<Guid> ReadSavedOrder(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Guid profileId, ProfileEntityKind entityKind, CancellationToken token)
        => connection.Query<Guid>(new CommandDefinition(
            """
            SELECT entity_id FROM profile_saved_items
            WHERE profile_id = @profileId AND entity_kind = @entityKind
            ORDER BY CASE WHEN position IS NULL THEN 1 ELSE 0 END, position, saved_at DESC, entity_id;
            """,
            new { profileId, entityKind = entityKind.ToString() }, transaction, cancellationToken: token)).ToList();

    private static void WriteSavedOrder(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Guid profileId, ProfileEntityKind entityKind, List<Guid> ordered, CancellationToken token)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            connection.Execute(new CommandDefinition(
                """
                UPDATE profile_saved_items SET position = @index
                WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;
                """,
                new { index, profileId, entityKind = entityKind.ToString(), entityId = ordered[index] }, transaction, cancellationToken: token));
        }
    }

    public Task<IReadOnlyList<ProfileSavedItem>> RemoveSavedItemsAsync(
        Guid profileId,
        IReadOnlyList<ProfileSavedKey> items,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        var removed = new List<ProfileSavedItem>();
        foreach (var key in items.Distinct())
        {
            var parameters = new { profileId, entityKind = key.EntityKind.ToString(), entityId = key.EntityId };
            var row = connection.QuerySingleOrDefault<SavedRow>(new CommandDefinition(
                """
                SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                       saved_at AS SavedAt, position AS Position
                FROM profile_saved_items
                WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;
                """,
                parameters, transaction, cancellationToken: token));
            if (row is null)
            {
                continue;
            }

            connection.Execute(new CommandDefinition(
                "DELETE FROM profile_saved_items WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;",
                parameters, transaction, cancellationToken: token));
            removed.Add(Map(row));
        }

        return (IReadOnlyList<ProfileSavedItem>)removed;
    }, ct);

    public Task<IReadOnlyList<ProfileSavedItem>> RestoreSavedItemsAsync(
        Guid profileId,
        IReadOnlyList<ProfileSavedRestore> items,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        RequireProfile(connection, transaction, profileId, token);
        var restored = new List<ProfileSavedItem>();
        foreach (var item in items)
        {
            if (!TargetExists(connection, transaction, item.EntityKind, item.EntityId, token))
            {
                continue;
            }

            var parameters = new
            {
                profileId,
                entityKind = item.EntityKind.ToString(),
                entityId = item.EntityId,
                savedAt = item.SavedAt.ToUniversalTime().ToString("O"),
                position = item.Position,
            };
            connection.Execute(new CommandDefinition(
                """
                INSERT INTO profile_saved_items(profile_id, entity_kind, entity_id, saved_at, position)
                VALUES (@profileId, @entityKind, @entityId, @savedAt, @position)
                ON CONFLICT(profile_id, entity_kind, entity_id) DO UPDATE SET
                    saved_at = excluded.saved_at,
                    position = COALESCE(excluded.position, profile_saved_items.position);
                """,
                parameters, transaction, cancellationToken: token));
            var row = connection.QuerySingle<SavedRow>(new CommandDefinition(
                """
                SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                       saved_at AS SavedAt, position AS Position
                FROM profile_saved_items
                WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;
                """,
                parameters, transaction, cancellationToken: token));
            restored.Add(Map(row));
        }

        return (IReadOnlyList<ProfileSavedItem>)restored;
    }, ct);

    public Task<IReadOnlyList<ProfileReactionState>> GetReactionsAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<ReactionRow>(new CommandDefinition(
            """
            SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                   reaction AS Reaction, updated_at AS UpdatedAt
            FROM profile_reactions
            WHERE profile_id = @profileId
            ORDER BY updated_at DESC;
            """,
            new { profileId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ProfileReactionState>>(rows.Select(Map).ToList());
    }

    public Task<ProfileReactionState?> GetReactionAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<ReactionRow>(new CommandDefinition(
            """
            SELECT profile_id AS ProfileId, entity_kind AS EntityKind, entity_id AS EntityId,
                   reaction AS Reaction, updated_at AS UpdatedAt
            FROM profile_reactions
            WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;
            """,
            new { profileId, entityKind = entityKind.ToString(), entityId }, cancellationToken: ct));
        return Task.FromResult<ProfileReactionState?>(row is null ? null : Map(row));
    }

    public Task<ProfileReactionState> SetReactionAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        ProfileReactionKind reaction,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
    {
        RequireProfile(connection, transaction, profileId, token);
        RequireTarget(connection, transaction, entityKind, entityId, token);
        var now = DateTimeOffset.UtcNow;
        connection.Execute(new CommandDefinition(
            """
            INSERT INTO profile_reactions(profile_id, entity_kind, entity_id, reaction, updated_at)
            VALUES (@profileId, @entityKind, @entityId, @reaction, @updatedAt)
            ON CONFLICT(profile_id, entity_kind, entity_id) DO UPDATE SET
                reaction = excluded.reaction,
                updated_at = excluded.updated_at;
            """,
            new
            {
                profileId,
                entityKind = entityKind.ToString(),
                entityId,
                reaction = reaction.ToString(),
                updatedAt = now.ToString("O"),
            }, transaction, cancellationToken: token));
        return new ProfileReactionState(profileId, entityKind, entityId, reaction, now);
    }, ct);

    public Task<bool> RemoveReactionAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default) => db.ExecuteWriteAsync((connection, transaction, token) =>
        connection.Execute(new CommandDefinition(
            "DELETE FROM profile_reactions WHERE profile_id = @profileId AND entity_kind = @entityKind AND entity_id = @entityId;",
            new { profileId, entityKind = entityKind.ToString(), entityId }, transaction, cancellationToken: token)) > 0, ct);

    private static void RequireProfile(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, Guid profileId, CancellationToken ct)
    {
        if (connection.ExecuteScalar<long>(new CommandDefinition(
                "SELECT COUNT(1) FROM profiles WHERE id = @profileId;",
                new { profileId }, transaction, cancellationToken: ct)) == 0)
        {
            throw new KeyNotFoundException($"Profile '{profileId:D}' was not found.");
        }
    }

    private static void RequireTarget(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct)
    {
        if (!TargetExists(connection, transaction, entityKind, entityId, ct))
        {
            throw new KeyNotFoundException($"{entityKind} '{entityId:D}' was not found.");
        }
    }

    private static bool TargetExists(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct) => entityKind == ProfileEntityKind.Album
            ? connection.ExecuteScalar<long>(new CommandDefinition(
                "SELECT (SELECT COUNT(1) FROM works WHERE id = @entityId) + (SELECT COUNT(1) FROM collections WHERE id = @entityId);",
                new { entityId }, transaction, cancellationToken: ct)) > 0
            : connection.ExecuteScalar<long>(new CommandDefinition(
                $"SELECT COUNT(1) FROM {(entityKind is ProfileEntityKind.Collection or ProfileEntityKind.Playlist ? "collections" : "works")} WHERE id = @entityId;",
                new { entityId }, transaction, cancellationToken: ct)) > 0;

    private static ProfileSavedItem Map(SavedRow row) => new(
        row.ProfileId,
        Enum.Parse<ProfileEntityKind>(row.EntityKind, ignoreCase: true),
        row.EntityId,
        DateTimeOffset.Parse(row.SavedAt, System.Globalization.CultureInfo.InvariantCulture),
        row.Position);

    private static ProfileReactionState Map(ReactionRow row) => new(
        row.ProfileId,
        Enum.Parse<ProfileEntityKind>(row.EntityKind, ignoreCase: true),
        row.EntityId,
        Enum.Parse<ProfileReactionKind>(row.Reaction, ignoreCase: true),
        DateTimeOffset.Parse(row.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture));

    private sealed class SavedRow
    {
        public Guid ProfileId { get; set; }
        public string EntityKind { get; set; } = string.Empty;
        public Guid EntityId { get; set; }
        public string SavedAt { get; set; } = string.Empty;
        public int? Position { get; set; }
    }

    private sealed class ReactionRow
    {
        public Guid ProfileId { get; set; }
        public string EntityKind { get; set; } = string.Empty;
        public Guid EntityId { get; set; }
        public string Reaction { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
