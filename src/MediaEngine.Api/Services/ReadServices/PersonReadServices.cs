using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
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

    public async Task<PersonAliasResponse?> GetAliasesAsync(Guid personId, ViewerCatalogue viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
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

        var linkedIds = new List<Guid>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                linkedIds.Add(GuidSql.FromDb(reader.GetValue(0)));
            }
        }

        // A linked person is shown only when the viewer could open them (see LinkedPersonVisibility).
        var visible = await LinkedPersonVisibility.VisibleAsync(_personRepo, linkedIds, viewer, ct);
        var aliases = new List<PersonAliasItemResponse>();
        foreach (var aliasId in linkedIds.Where(visible.Contains))
        {
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
        ViewerCatalogue viewer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        // Only the credits on files the viewer may see are counted, so a title in the Vault, in a hidden library or above the viewer's
        // content limit adds nothing. A person with no visible credit has no entry at all (Vault sections spec D27, D32).
        var credits = await _personRepo.GetPresenceCreditsAsync(personIds, ct);
        return credits
            .Where(credit => viewer.CanSeeCredit(credit.WorkId, credit.AssetId, credit.LibraryId))
            .GroupBy(credit => credit.PersonId)
            .ToDictionary(
                person => person.Key.ToString(),
                person => person
                    .GroupBy(credit => credit.MediaType, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        type => type.First().MediaType,
                        type => type.Select(credit => credit.DisplayWorkId).Distinct().Count(),
                        StringComparer.OrdinalIgnoreCase));
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
    private sealed class CreditRow
    {
        public Guid PersonId { get; init; }
        public Guid AssetId { get; init; }
        public string Role { get; init; } = string.Empty;
    }

    private const string CreditsKey = "person-credits:primary";

    private readonly IPersonRepository _personRepo;
    private readonly IDatabaseConnection _db;
    private readonly SharedListCache? _shared;

    /// <param name="shared">
    /// When given, the library's primary credits are read once and shared (reloaded behind the reader when they get old), because the
    /// People section asks for them in four separate requests and each one scanned the credits view for about a second. Without it
    /// every call reads the view itself.
    /// </param>
    public PersonAssetScopeReadService(IPersonRepository personRepo, IDatabaseConnection db, SharedListCache? shared = null)
    {
        _personRepo = personRepo;
        _db = db;
        _shared = shared;
    }

    // Every primary credit of the library, as (person, file, role). Filtering by the viewer's files happens in memory.
    private async Task<IReadOnlyList<CreditRow>> LoadCreditsAsync(CancellationToken ct)
    {
        Task<IReadOnlyList<CreditRow>> Load()
        {
            using var connection = _db.CreateConnection();
            IReadOnlyList<CreditRow> rows = connection.Query<CreditRow>(
                "SELECT DISTINCT person_id AS PersonId, media_asset_id AS AssetId, role AS Role FROM primary_person_media_credits;").ToList();
            return Task.FromResult(rows);
        }

        return _shared is null ? await Load() : await _shared.GetAsync(CreditsKey, Load, ct);
    }

    public async Task<IReadOnlyList<PersonSummaryResponse>> GetByCollectionAsync(
        Guid collectionId, ViewerCatalogue viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var visiblePersonIds = await GetCanonicalPersonIdsForCollectionAsync(collectionId, viewer.PrimaryAssetIds, ct);
        var persons = await GetByAssetQueryAsync("""
            -- readiness: membership lookup from a person or collection id to files, not a listing
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w    ON w.id = e.work_id
            WHERE w.collection_id = @id;
            """, collectionId, ct);
        return persons.Where(person => visiblePersonIds.Contains(person.Id)).ToList();
    }

    public async Task<IReadOnlyList<PersonSummaryResponse>> GetByWorkAsync(
        Guid workId, ViewerCatalogue viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var visiblePersonIds = await GetCanonicalPersonIdsAsync(viewer.PrimaryAssetIdsOf(workId), ct);
        var persons = await GetByAssetQueryAsync("""
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE e.work_id = @id;
            """, workId, ct);
        return persons.Where(person => visiblePersonIds.Contains(person.Id)).ToList();
    }

    public async Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsAsync(ViewerCatalogue viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ct.ThrowIfCancellationRequested();
        var assets = NormalizeAssetIds(viewer.PrimaryAssetIds).ToHashSet();
        if (assets.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var credits = await LoadCreditsAsync(ct);
        return credits.Where(credit => assets.Contains(credit.AssetId)).Select(credit => credit.PersonId).ToHashSet();
    }

    private Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsAsync(
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

    private Task<IReadOnlySet<Guid>> GetCanonicalPersonIdsForCollectionAsync(
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

    public async Task<IReadOnlyDictionary<string, int>> GetCanonicalRoleCountsAsync(ViewerCatalogue viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ct.ThrowIfCancellationRequested();
        var assets = NormalizeAssetIds(viewer.PrimaryAssetIds).ToHashSet();
        if (assets.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var credits = await LoadCreditsAsync(ct);
        return credits
            .Where(credit => assets.Contains(credit.AssetId) && !string.Equals(credit.Role, "Composer", StringComparison.Ordinal))
            .GroupBy(credit => credit.Role)
            .ToDictionary(group => group.Key, group => group.Select(credit => credit.PersonId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);
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
