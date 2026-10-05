using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayFavoriteProjectionReader
{
    private readonly IDatabaseConnection _db;

    public DisplayFavoriteProjectionReader(IDatabaseConnection db)
    {
        _db = db;
    }

    public async Task<IReadOnlySet<Guid>> LoadAsync(Guid? profileId, CancellationToken ct)
    {
        if (!profileId.HasValue)
        {
            return new HashSet<Guid>();
        }

        using var conn = _db.CreateConnection();
        var ids = await conn.QueryAsync<object>(new CommandDefinition(
            """
            SELECT entity_id
            FROM profile_reactions
            WHERE profile_id = @ProfileId
              AND reaction = 'Love'
              AND entity_kind = 'Song';
            """,
            new { ProfileId = GuidSql.ToBlob(profileId.Value) },
            cancellationToken: ct));

        return ids
            .Select(value => value is byte[] bytes && bytes.Length == 16 ? GuidSql.FromDb(bytes) : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }
}
