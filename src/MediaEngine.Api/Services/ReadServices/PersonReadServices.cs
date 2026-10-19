using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class PersonAliasReadService : IPersonAliasReadService
{
    private readonly IPersonRepository _personRepo;
    private readonly IDatabaseConnection _db;

    public PersonAliasReadService(IPersonRepository personRepo, IDatabaseConnection db)
    {
        _personRepo = personRepo;
        _db = db;
    }

    public async Task<PersonAliasResponse?> GetAliasesAsync(Guid personId, CancellationToken ct)
    {
        var person = await _personRepo.FindByIdAsync(personId, ct);
        if (person is null)
        {
            return null;
        }

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = person.IsPseudonym
            ? "SELECT real_person_id FROM person_aliases WHERE pseudonym_person_id = @id"
            : "SELECT pseudonym_person_id FROM person_aliases WHERE real_person_id = @id";
        cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(personId);

        var aliases = new List<PersonAliasItemResponse>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var aliasId = GuidSql.FromDb(reader.GetValue(0));
            var aliasPerson = await _personRepo.FindByIdAsync(aliasId, ct);
            if (aliasPerson is null)
            {
                continue;
            }

            aliases.Add(new PersonAliasItemResponse
            {
                Id = aliasPerson.Id,
                Name = aliasPerson.Name,
                Roles = aliasPerson.Roles,
                HeadshotUrl = ApiImageUrls.BuildPersonHeadshotUrl(aliasPerson.Id, aliasPerson.LocalHeadshotPath, aliasPerson.HeadshotUrl),
                IsPseudonym = aliasPerson.IsPseudonym,
                WikidataQid = aliasPerson.WikidataQid,
                Relationship = person.IsPseudonym ? "real_person" : "pen_name",
            });
        }

        return new PersonAliasResponse
        {
            PersonId = personId,
            PersonName = person.Name,
            IsPseudonym = person.IsPseudonym,
            Aliases = aliases,
        };
    }
}

public sealed class PersonPresenceReadService : IPersonPresenceReadService
{
    private readonly IPersonRepository _personRepo;

    public PersonPresenceReadService(IPersonRepository personRepo)
    {
        _personRepo = personRepo;
    }

    public async Task<IReadOnlyDictionary<string, Dictionary<string, int>>> GetPresenceAsync(
        IReadOnlyList<Guid> personIds,
        CancellationToken ct)
    {
        var presence = await _personRepo.GetPresenceBatchAsync(personIds, ct);
        return presence.ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value);
    }
}

public sealed class PersonWorksReadService : IPersonWorksReadService
{
    private readonly IPersonRepository _personRepo;
    private readonly IDatabaseConnection _db;

    public PersonWorksReadService(IPersonRepository personRepo, IDatabaseConnection db)
    {
        _personRepo = personRepo;
        _db = db;
    }

    public async Task<IReadOnlySet<Guid>> GetCollectionIdsForPersonAsync(Guid personId, CancellationToken ct)
    {
        var person = await _personRepo.FindByIdAsync(personId, ct);
        if (person is null)
        {
            return new HashSet<Guid>();
        }

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            -- readiness: membership lookup from a person or collection id to files, not a listing
            SELECT DISTINCT w.collection_id
            FROM primary_person_media_credits primary_credit
            JOIN media_assets ma ON ma.id = primary_credit.media_asset_id
            JOIN editions e      ON e.id  = ma.edition_id
            JOIN works w         ON w.id  = e.work_id
            WHERE primary_credit.person_id = @personId
              AND w.collection_id IS NOT NULL;
            """;
        cmd.Parameters.Add("@personId", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(personId);

        var collectionIds = new HashSet<Guid>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                collectionIds.Add(GuidSql.FromDb(reader.GetValue(0)));
            }
        }

        return collectionIds;
    }
}

public sealed class PersonAssetScopeReadService : IPersonAssetScopeReadService
{
    private sealed class RoleCountRow
    {
        public string Role { get; init; } = string.Empty;
        public int Count { get; init; }
    }

    private readonly IPersonRepository _personRepo;
    private readonly IDatabaseConnection _db;

    public PersonAssetScopeReadService(IPersonRepository personRepo, IDatabaseConnection db)
    {
        _personRepo = personRepo;
        _db = db;
    }

    public async Task<IReadOnlyList<PersonSummaryResponse>> GetByCollectionAsync(Guid collectionId, CancellationToken ct)
    {
        return await GetByAssetQueryAsync("""
            -- readiness: membership lookup from a person or collection id to files, not a listing
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w    ON w.id = e.work_id
            WHERE w.collection_id = @id;
            """, collectionId, ct);
    }

    public async Task<IReadOnlyList<PersonSummaryResponse>> GetByWorkAsync(Guid workId, CancellationToken ct)
    {
        return await GetByAssetQueryAsync("""
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE e.work_id = @id;
            """, workId, ct);
    }

    public Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ids = NormalizeAssetIds(assetIds);
        if (ids.Length == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        }

        using var connection = _db.CreateConnection();
        var people = connection.Query<Guid>(
            "SELECT DISTINCT person_id FROM primary_person_media_credits WHERE media_asset_id IN @assetIds;",
            new { assetIds = ids.Select(GuidSql.ToBlob).ToArray() });
        return Task.FromResult<IReadOnlySet<Guid>>(people.ToHashSet());
    }

    public Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsForCollectionAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ids = NormalizeAssetIds(assetIds);
        if (ids.Length == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        }

        using var connection = _db.CreateConnection();
        var people = connection.Query<Guid>(
            """
            -- readiness: membership lookup from a person or collection id to files, not a listing
            SELECT DISTINCT credit.person_id
            FROM primary_person_media_credits credit
            JOIN media_assets asset ON asset.id=credit.media_asset_id
            JOIN editions edition ON edition.id=asset.edition_id
            JOIN works work ON work.id=edition.work_id
            LEFT JOIN collection_items item
                ON item.work_id=work.id AND item.collection_id=@collectionId
            WHERE credit.media_asset_id IN @assetIds
              AND (work.collection_id=@collectionId OR item.collection_id=@collectionId);
            """,
            new
            {
                collectionId,
                assetIds = ids.Select(GuidSql.ToBlob).ToArray(),
            });
        return Task.FromResult<IReadOnlySet<Guid>>(people.ToHashSet());
    }

    public Task<IReadOnlyDictionary<string, int>> GetCanonicalRoleCountsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ids = NormalizeAssetIds(assetIds);
        if (ids.Length == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, int>>(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        }

        using var connection = _db.CreateConnection();
        var rows = connection.Query<RoleCountRow>(
            """
            SELECT role AS Role, COUNT(DISTINCT person_id) AS Count
            FROM primary_person_media_credits
            WHERE media_asset_id IN @assetIds AND role <> 'Composer'
            GROUP BY role;
            """,
            new { assetIds = ids.Select(GuidSql.ToBlob).ToArray() });
        return Task.FromResult<IReadOnlyDictionary<string, int>>(
            rows.ToDictionary(row => row.Role, row => row.Count, StringComparer.OrdinalIgnoreCase));
    }

    private static Guid[] NormalizeAssetIds(IEnumerable<Guid> assetIds) =>
        assetIds.Where(id => id != Guid.Empty).Distinct().ToArray();

    private async Task<IReadOnlyList<PersonSummaryResponse>> GetByAssetQueryAsync(
        string query,
        Guid id,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(id);

        var assetIds = new List<Guid>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                assetIds.Add(GuidSql.FromDb(reader.GetValue(0)));
            }
        }

        var linked = await _personRepo.GetByMediaAssetsAsync(assetIds, ct);
        return linked
            .GroupBy(p => p.Id)
            .Select(group => group.First())
            .Select(p => new PersonSummaryResponse
            {
                Id = p.Id,
                Name = p.Name,
                Roles = p.Roles,
                WikidataQid = p.WikidataQid,
                HeadshotUrl = ApiImageUrls.BuildPersonHeadshotUrl(p.Id, p.LocalHeadshotPath, p.HeadshotUrl),
                HasLocalHeadshot = !string.IsNullOrEmpty(p.LocalHeadshotPath) && File.Exists(p.LocalHeadshotPath),
                Biography = p.Biography,
                Occupation = p.Occupation,
            })
            .ToList();
    }
}
