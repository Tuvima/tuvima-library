using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class ProfileGridPreferenceRepository(IDatabaseConnection db) : IProfileGridPreferenceRepository
{
    public Task<IReadOnlyDictionary<string, string>> GetSortsAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<(string GridKey, string Sort)>(new CommandDefinition(
            """
            SELECT grid_key AS GridKey, sort AS Sort
              FROM profile_grid_preferences
             WHERE profile_id = @profileId;
            """,
            new { profileId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            rows.ToDictionary(row => row.GridKey, row => row.Sort, StringComparer.Ordinal));
    }

    public Task SaveSortAsync(Guid profileId, string gridKey, string sort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gridKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sort);
        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (connection.ExecuteScalar<long>(new CommandDefinition(
                    "SELECT COUNT(1) FROM profiles WHERE id = @profileId;",
                    new { profileId }, transaction, cancellationToken: token)) == 0)
            {
                throw new KeyNotFoundException($"Profile '{profileId:D}' was not found.");
            }

            connection.Execute(new CommandDefinition(
                """
                INSERT INTO profile_grid_preferences (profile_id, grid_key, sort, updated_at)
                VALUES (@profileId, @gridKey, @sort, @updatedAt)
                ON CONFLICT(profile_id, grid_key) DO UPDATE SET
                    sort = excluded.sort,
                    updated_at = excluded.updated_at;
                """,
                new { profileId, gridKey, sort, updatedAt = DateTimeOffset.UtcNow.ToString("O") },
                transaction, cancellationToken: token));
        }, ct);
    }
}
