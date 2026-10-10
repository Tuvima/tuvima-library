using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IProfileContentLimitRepository"/>.</summary>
public sealed class ProfileContentLimitRepository(IDatabaseConnection database) : IProfileContentLimitRepository
{
    public async Task<ProfileContentLimit> GetAsync(Guid profileId, CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        var row = await connection.QueryFirstOrDefaultAsync<LimitRow>(new CommandDefinition(
            """
            SELECT content_limit AS ContentLimit, content_limit_allow_unrated AS AllowUnrated
            FROM profiles WHERE id = @profileId LIMIT 1;
            """,
            new { profileId },
            cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? ProfileContentLimit.Strictest : new ProfileContentLimit(row.ContentLimit, row.AllowUnrated);
    }

    private sealed class LimitRow
    {
        public string? ContentLimit { get; set; }
        public bool AllowUnrated { get; set; }
    }
}
