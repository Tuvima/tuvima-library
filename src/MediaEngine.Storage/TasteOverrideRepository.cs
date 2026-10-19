using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="ITasteOverrideRepository"/>, backed by <c>profile_taste_overrides</c>.</summary>
public sealed class TasteOverrideRepository(IDatabaseConnection database) : ITasteOverrideRepository
{
    public Task<IReadOnlyList<TasteOverride>> ListAsync(IReadOnlyCollection<Guid>? profileIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (profileIds is { Count: 0 })
        {
            return Task.FromResult<IReadOnlyList<TasteOverride>>([]);
        }

        using var connection = database.CreateConnection();
        var parameters = new DynamicParameters();
        var filter = string.Empty;
        if (profileIds is not null)
        {
            // The ids are bound as they are stored (BLOB), so the primary key index can be used.
            var names = new List<string>(profileIds.Count);
            foreach (var id in profileIds.Distinct())
            {
                var name = "profile" + names.Count.ToString(CultureInfo.InvariantCulture);
                parameters.Add(name, GuidSql.ToBlob(id), System.Data.DbType.Binary);
                names.Add("@" + name);
            }

            filter = $"WHERE profile_id IN ({string.Join(',', names)}) ";
        }

        var rows = connection.Query<Row>(
            "SELECT profile_id AS ProfileId, taste_group AS TasteGroup, value AS Value, action AS Action FROM profile_taste_overrides " +
            filter + "ORDER BY taste_group, value;",
            parameters);
        return Task.FromResult<IReadOnlyList<TasteOverride>>(
            rows.Select(row => new TasteOverride(row.ProfileId, row.TasteGroup, row.Value, row.Action)).ToList());
    }

    public Task SetAsync(Guid profileId, string group, string value, string action, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile is required.", nameof(profileId));
        }

        if (!TasteGroups.IsKnown(group))
        {
            throw new ArgumentOutOfRangeException(nameof(group), group, "A taste group is genre, mood or era.");
        }

        if (action is not (TasteOverrideActions.Removed or TasteOverrideActions.Added))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "A taste change is removed or added.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        using var connection = database.CreateConnection();
        connection.Execute("""
            INSERT INTO profile_taste_overrides (profile_id, taste_group, value, action, created_at)
            VALUES (@profileId, @group, @value, @action, @createdAt)
            ON CONFLICT(profile_id, taste_group, value) DO UPDATE SET
                action = excluded.action,
                created_at = excluded.created_at;
            """,
            new { profileId, group, value, action, createdAt = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKnownGenresAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        // readiness: a bounded read of the library's genre values; the suggestions only need the most common ones
        var genres = connection.Query<string>("""
            SELECT LOWER(TRIM(value)) AS genre
            FROM canonical_value_arrays
            WHERE key = 'genre' AND TRIM(value) <> ''
            GROUP BY LOWER(TRIM(value))
            ORDER BY COUNT(*) DESC, genre
            LIMIT @limit;
            """, new { limit }).ToList();
        return Task.FromResult<IReadOnlyList<string>>(genres);
    }

    private sealed class Row
    {
        public Guid ProfileId { get; init; }
        public string TasteGroup { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
    }
}
