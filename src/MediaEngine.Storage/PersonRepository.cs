using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IPersonRepository"/>.
/// Uses Dapper for type-safe column-to-property mapping.
///
/// Persons are looked up by QID or name at ingestion time and enriched
/// asynchronously by the Wikidata adapter.  Roles are stored in the
/// <c>person_roles</c> junction table.  Person-to-asset links live in
/// the <c>person_media_links</c> junction table.
///
/// Spec: Phase 9 - Recursive Person Enrichment.
/// </summary>
public sealed class PersonRepository : IPersonRepository
{
    private readonly IDatabaseConnection _db;

    // Reusable SELECT list with aliases matching Person property names.
    // Note: 'role' column has been removed; roles come from person_roles.
    private const string SelectColumns = """
        id                 AS Id,
        name               AS Name,
        wikidata_qid       AS WikidataQid,
        headshot_url       AS HeadshotUrl,
        biography          AS Biography,
        created_at         AS CreatedAt,
        enriched_at        AS EnrichedAt,
        occupation         AS Occupation,
        instagram          AS Instagram,
        twitter            AS Twitter,
        tiktok             AS TikTok,
        mastodon           AS Mastodon,
        website            AS Website,
        local_headshot_path AS LocalHeadshotPath,
        date_of_birth      AS DateOfBirth,
        date_of_death      AS DateOfDeath,
        place_of_birth     AS PlaceOfBirth,
        place_of_death     AS PlaceOfDeath,
        nationality        AS Nationality,
        is_pseudonym       AS IsPseudonym,
        is_group           AS IsGroup
        """;

    /// <summary>Helper record for reading character-performer link rows.</summary>
    private sealed record CharacterLinkRow(Guid FictionalEntityId, string? WorkQid);
    private sealed record CharacterLinkByWorkRow(Guid PersonId, Guid FictionalEntityId);
    private sealed record CharacterPerformerRow(
        Guid PersonId,
        Guid FictionalEntityId,
        string? WorkQid,
        string PerformerName,
        string? HeadshotUrl,
        string? LocalHeadshotPath);
    private sealed record PersonRoleRow(Guid PersonId, string Role);

    /// <summary>Helper record for the ListAllAsync GROUP_CONCAT query.</summary>
    private sealed record PersonWithRolesCsv
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? WikidataQid { get; init; }
        public string? HeadshotUrl { get; init; }
        public string? Biography { get; init; }
        public string? CreatedAt { get; init; }
        public string? EnrichedAt { get; init; }
        public string? Occupation { get; init; }
        public string? Instagram { get; init; }
        public string? Twitter { get; init; }
        public string? TikTok { get; init; }
        public string? Mastodon { get; init; }
        public string? Website { get; init; }
        public string? LocalHeadshotPath { get; init; }
        public string? DateOfBirth { get; init; }
        public string? DateOfDeath { get; init; }
        public string? PlaceOfBirth { get; init; }
        public string? PlaceOfDeath { get; init; }
        public string? Nationality { get; init; }
        public int IsPseudonym { get; init; }
        public int IsGroup { get; init; }
        public string? RolesCsv { get; init; }
    }

    /// <summary>Helper record for the presence batch query.</summary>
    private sealed record PresenceRow
    {
        public Guid PersonId { get; init; }
        public string MediaType { get; init; } = "";
        public Guid DisplayWorkId { get; init; }
        public Guid AssetId { get; init; }
        public Guid WorkId { get; init; }
        public string? LibraryId { get; init; }
    }

    public PersonRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    // -------------------------------------------------------------------------
    // IPersonRepository
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<Person?> FindByNameAsync(
        string name,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var conn = _db.CreateConnection();
        // COLLATE NOCASE: SQLite case-insensitive comparison for ASCII names.
        var p = new DynamicParameters();
        p.Add("name", name);
        var result = conn.QueryFirstOrDefault<Person>($"""
            SELECT {SelectColumns}
            FROM   persons
            WHERE  name = @name COLLATE NOCASE
            LIMIT  1;
            """, p);

        if (result is not null)
        {
            PopulateRoles(conn, result);
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<Person> CreateAsync(Person person, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(person);

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var p = new DynamicParameters();
            p.Add("id", person.Id);
            p.Add("name", person.Name);
            p.Add("wikidataQid", person.WikidataQid);
            p.Add("headshotUrl", person.HeadshotUrl);
            p.Add("biography", person.Biography);
            p.Add("createdAt", person.CreatedAt.ToString("o"));
            p.Add("enrichedAt", person.EnrichedAt.HasValue ? person.EnrichedAt.Value.ToString("o") : null);
            p.Add("dateOfBirth", person.DateOfBirth);
            p.Add("dateOfDeath", person.DateOfDeath);
            p.Add("placeOfBirth", person.PlaceOfBirth);
            p.Add("placeOfDeath", person.PlaceOfDeath);
            p.Add("nationality", person.Nationality);
            p.Add("isPseudonym", person.IsPseudonym ? 1 : 0);
            p.Add("isGroup", person.IsGroup ? 1 : 0);
            conn.Execute("""
                INSERT INTO persons
                    (id, name, wikidata_qid, headshot_url, biography,
                     created_at, enriched_at, date_of_birth, date_of_death,
                     place_of_birth, place_of_death, nationality, is_pseudonym, is_group)
                VALUES
                    (@id, @name, @wikidataQid, @headshotUrl, @biography,
                     @createdAt, @enrichedAt, @dateOfBirth, @dateOfDeath,
                     @placeOfBirth, @placeOfDeath, @nationality, @isPseudonym, @isGroup);
                """, p, tx);

            // Insert each role into person_roles junction table in the same transaction.
            foreach (var role in person.Roles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }

                var rp = new DynamicParameters();
                rp.Add("personId", person.Id);
                rp.Add("role", role);
                conn.Execute("""
                    INSERT OR IGNORE INTO person_roles (person_id, role)
                    VALUES (@personId, @role);
                    """, rp, tx);
            }

            return person;
        }, ct);
    }

    /// <inheritdoc/>
    public Task UpdateEnrichmentAsync(
        Guid personId,
        string? wikidataQid,
        string? headshotUrl,
        string? biography,
        string? name,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("name", name);
        p.Add("wikidataQid", wikidataQid);
        p.Add("headshotUrl", headshotUrl);
        p.Add("biography", biography);
        p.Add("enrichedAt", DateTimeOffset.UtcNow.ToString("o"));
        p.Add("id", personId);
        conn.Execute("""
            UPDATE persons
            SET    name         = COALESCE(@name, name),
                   wikidata_qid = COALESCE(@wikidataQid, wikidata_qid),
                   headshot_url = COALESCE(@headshotUrl, headshot_url),
                   biography    = COALESCE(@biography, biography),
                   enriched_at  = @enrichedAt
            WHERE  id = @id;
            """, p);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateNameAsync(
        Guid personId,
        string name,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute(
                "UPDATE persons SET name = @name WHERE id = @id;",
                new { id = personId, name = name.Trim() },
                tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task UpdateBiographicalFieldsAsync(
        Guid personId,
        string? dateOfBirth,
        string? dateOfDeath,
        string? placeOfBirth,
        string? placeOfDeath,
        string? nationality,
        bool isPseudonym,
        bool isGroup = false,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("dateOfBirth", dateOfBirth);
        p.Add("dateOfDeath", dateOfDeath);
        p.Add("placeOfBirth", placeOfBirth);
        p.Add("placeOfDeath", placeOfDeath);
        p.Add("nationality", nationality);
        p.Add("isPseudonym", isPseudonym ? 1 : 0);
        p.Add("isGroup", isGroup ? 1 : 0);
        p.Add("id", personId);
        conn.Execute("""
            UPDATE persons
            SET    date_of_birth  = @dateOfBirth,
                   date_of_death  = @dateOfDeath,
                   place_of_birth = @placeOfBirth,
                   place_of_death = @placeOfDeath,
                   nationality    = @nationality,
                   is_pseudonym   = @isPseudonym,
                   is_group       = @isGroup
            WHERE  id = @id;
            """, p);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateSocialFieldsAsync(
        Guid personId,
        string? occupation,
        string? instagram,
        string? twitter,
        string? tiktok,
        string? mastodon,
        string? website,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("occupation", occupation);
        p.Add("instagram", instagram);
        p.Add("twitter", twitter);
        p.Add("tiktok", tiktok);
        p.Add("mastodon", mastodon);
        p.Add("website", website);
        p.Add("id", personId);
        conn.Execute("""
            UPDATE persons
            SET    occupation = COALESCE(@occupation, occupation),
                   instagram  = COALESCE(@instagram,  instagram),
                   twitter    = COALESCE(@twitter,    twitter),
                   tiktok     = COALESCE(@tiktok,     tiktok),
                   mastodon   = COALESCE(@mastodon,   mastodon),
                   website    = COALESCE(@website,    website)
            WHERE  id = @id;
            """, p);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task LinkToMediaAssetAsync(
        Guid mediaAssetId,
        Guid personId,
        string role,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        using var conn = _db.CreateConnection();
        // INSERT OR IGNORE: composite PK (media_asset_id, person_id, role) prevents
        // duplicate links; repeated calls for the same triplet are safe no-ops.
        var p = new DynamicParameters();
        p.Add("mediaAssetId", mediaAssetId);
        p.Add("personId", personId);
        p.Add("role", role);
        conn.Execute("""
            INSERT OR IGNORE INTO person_media_links
                (media_asset_id, person_id, role)
            VALUES
                (@mediaAssetId, @personId, @role);
            """, p);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> GetByMediaAssetAsync(
        Guid mediaAssetId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("mediaAssetId", mediaAssetId);
        var rows = conn.Query<PersonWithRolesCsv>($"""
            SELECT p.id                  AS Id,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) AS Name,
                   p.wikidata_qid        AS WikidataQid,
                   p.headshot_url        AS HeadshotUrl,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.biography')), ''), p.biography) AS Biography,
                   p.created_at          AS CreatedAt,
                   p.enriched_at         AS EnrichedAt,
                   p.occupation          AS Occupation,
                   p.instagram           AS Instagram,
                   p.twitter             AS Twitter,
                   p.tiktok              AS TikTok,
                   p.mastodon            AS Mastodon,
                   p.website             AS Website,
                   p.local_headshot_path AS LocalHeadshotPath,
                   p.date_of_birth       AS DateOfBirth,
                   p.date_of_death       AS DateOfDeath,
                   p.place_of_birth      AS PlaceOfBirth,
                   p.place_of_death      AS PlaceOfDeath,
                   p.nationality         AS Nationality,
                   p.is_pseudonym        AS IsPseudonym,
                   p.is_group            AS IsGroup,
                   GROUP_CONCAT(pr.role, ',') AS RolesCsv
            FROM   persons p
            JOIN   primary_person_media_credits l ON l.person_id = p.id
            LEFT JOIN person_roles pr ON pr.person_id = p.id
            WHERE  l.media_asset_id = @mediaAssetId
            GROUP  BY p.id
            ORDER  BY COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.sort_name')), ''), NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) ASC;
            """, p).AsList();

        var results = rows.Select(MapFromCsvRow).ToList();
        return Task.FromResult<IReadOnlyList<Person>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> GetByMediaAssetsAsync(
        IEnumerable<Guid> mediaAssetIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var ids = mediaAssetIds
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Person>>([]);
        }

        using var conn = _db.CreateConnection();
        var rows = new List<PersonWithRolesCsv>();
        foreach (var batch in ids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var p = new DynamicParameters();
            var mediaAssetIdClause = AddGuidBlobList(p, "mediaAssetId", batch);
            rows.AddRange(conn.Query<PersonWithRolesCsv>($"""
                SELECT p.id                  AS Id,
                       COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) AS Name,
                       p.wikidata_qid        AS WikidataQid,
                       p.headshot_url        AS HeadshotUrl,
                       COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.biography')), ''), p.biography) AS Biography,
                       p.created_at          AS CreatedAt,
                       p.enriched_at         AS EnrichedAt,
                       p.occupation          AS Occupation,
                       p.instagram           AS Instagram,
                       p.twitter             AS Twitter,
                       p.tiktok              AS TikTok,
                       p.mastodon            AS Mastodon,
                       p.website             AS Website,
                       p.local_headshot_path AS LocalHeadshotPath,
                       p.date_of_birth       AS DateOfBirth,
                       p.date_of_death       AS DateOfDeath,
                       p.place_of_birth      AS PlaceOfBirth,
                       p.place_of_death      AS PlaceOfDeath,
                       p.nationality         AS Nationality,
                       p.is_pseudonym        AS IsPseudonym,
                       p.is_group            AS IsGroup,
                       GROUP_CONCAT(pr.role, ',') AS RolesCsv
                FROM   persons p
                JOIN   primary_person_media_credits l ON l.person_id = p.id
                LEFT JOIN person_roles pr ON pr.person_id = p.id
                WHERE  l.media_asset_id IN ({mediaAssetIdClause})
                GROUP  BY p.id
                ORDER  BY COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.sort_name')), ''), NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) ASC;
                """, p));
        }

        var results = rows
            .GroupBy(row => row.Id)
            .Select(group => MapFromCsvRow(group.First()))
            .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<Person>>(results);
    }

    /// <inheritdoc/>
    public Task UpdateLocalHeadshotPathAsync(
        Guid id,
        string path,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("path", path);
        p.Add("id", id);
        conn.Execute("""
            UPDATE persons
            SET    local_headshot_path = @path
            WHERE  id = @id;
            """, p);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> GetWithLocalHeadshotWithoutManagedAssetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<Person>($"""
            SELECT {SelectColumns}
            FROM   persons
            WHERE  local_headshot_path IS NOT NULL
              AND  TRIM(local_headshot_path) <> ''
              AND  NOT EXISTS (
                       SELECT 1 FROM entity_assets
                       WHERE  entity_assets.entity_id = persons.id
                         AND  entity_assets.asset_type = 'Headshot');
            """).ToList();

        return Task.FromResult<IReadOnlyList<Person>>(rows);
    }

    /// <inheritdoc/>
    public Task<Person?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("id", id);
        var result = conn.QueryFirstOrDefault<Person>($"""
            SELECT {SelectColumns}
            FROM   persons
            WHERE  id = @id
            LIMIT  1;
            """, p);

        if (result is not null)
        {
            PopulateRoles(conn, result);
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> ListAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<PersonWithRolesCsv>("""
            SELECT p.id                  AS Id,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) AS Name,
                   p.wikidata_qid        AS WikidataQid,
                   p.headshot_url        AS HeadshotUrl,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.biography')), ''), p.biography) AS Biography,
                   p.created_at          AS CreatedAt,
                   p.enriched_at         AS EnrichedAt,
                   p.occupation          AS Occupation,
                   p.instagram           AS Instagram,
                   p.twitter             AS Twitter,
                   p.tiktok              AS TikTok,
                   p.mastodon            AS Mastodon,
                   p.website             AS Website,
                   p.local_headshot_path AS LocalHeadshotPath,
                   p.date_of_birth       AS DateOfBirth,
                   p.date_of_death       AS DateOfDeath,
                   p.place_of_birth      AS PlaceOfBirth,
                   p.place_of_death      AS PlaceOfDeath,
                   p.nationality         AS Nationality,
                   p.is_pseudonym        AS IsPseudonym,
                   p.is_group            AS IsGroup,
                   GROUP_CONCAT(pr.role, ',') AS RolesCsv
            FROM   persons p
            LEFT JOIN person_roles pr ON pr.person_id = p.id
            GROUP  BY p.id
            ORDER  BY COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.sort_name')), ''), NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) ASC;
            """).AsList();

        var results = rows.Select(MapFromCsvRow).ToList();
        return Task.FromResult<IReadOnlyList<Person>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> ListPagedAsync(
        string? role,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("role", role);
        p.Add("offset", Math.Max(0, offset));
        p.Add("limit", Math.Clamp(limit, 1, 501));

        var roleFilter = string.IsNullOrWhiteSpace(role)
            ? ""
            : "WHERE EXISTS (SELECT 1 FROM person_roles role_filter WHERE role_filter.person_id = p.id AND role_filter.role = @role COLLATE NOCASE)";

        var rows = conn.Query<PersonWithRolesCsv>($"""
            SELECT p.id                  AS Id,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) AS Name,
                   p.wikidata_qid        AS WikidataQid,
                   p.headshot_url        AS HeadshotUrl,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.biography')), ''), p.biography) AS Biography,
                   p.created_at          AS CreatedAt,
                   p.enriched_at         AS EnrichedAt,
                   p.occupation          AS Occupation,
                   p.instagram           AS Instagram,
                   p.twitter             AS Twitter,
                   p.tiktok              AS TikTok,
                   p.mastodon            AS Mastodon,
                   p.website             AS Website,
                   p.local_headshot_path AS LocalHeadshotPath,
                   p.date_of_birth       AS DateOfBirth,
                   p.date_of_death       AS DateOfDeath,
                   p.place_of_birth      AS PlaceOfBirth,
                   p.place_of_death      AS PlaceOfDeath,
                   p.nationality         AS Nationality,
                   p.is_pseudonym        AS IsPseudonym,
                   p.is_group            AS IsGroup,
                   GROUP_CONCAT(pr.role, ',') AS RolesCsv
            FROM   persons p
            LEFT JOIN person_roles pr ON pr.person_id = p.id
            {roleFilter}
            GROUP  BY p.id
            HAVING RolesCsv IS NOT NULL
            ORDER  BY COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.sort_name')), ''), NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) ASC
            LIMIT  @limit OFFSET @offset;
            """, p).AsList();

        var results = rows.Select(MapFromCsvRow).ToList();
        return Task.FromResult<IReadOnlyList<Person>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> ListCatalogPagedAsync(
        string? search,
        string? role,
        int offset,
        int limit,
        string? lane = null,
        string? sort = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("search", string.IsNullOrWhiteSpace(search) ? null : search.Trim());
        p.Add("role", string.IsNullOrWhiteSpace(role) ? null : role.Trim());
        p.Add("lane", string.IsNullOrWhiteSpace(lane) ? null : lane.Trim());
        p.Add("sort", string.Equals(sort, "count", StringComparison.OrdinalIgnoreCase) ? "count" : "title");
        p.Add("offset", Math.Max(0, offset));
        p.Add("limit", Math.Clamp(limit, 1, 501));

        var rows = conn.Query<PersonWithRolesCsv>("""
            SELECT p.id                  AS Id,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) AS Name,
                   p.wikidata_qid        AS WikidataQid,
                   p.headshot_url        AS HeadshotUrl,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.biography')), ''), p.biography) AS Biography,
                   p.created_at          AS CreatedAt,
                   p.enriched_at         AS EnrichedAt,
                   p.occupation          AS Occupation,
                   p.instagram           AS Instagram,
                   p.twitter             AS Twitter,
                   p.tiktok              AS TikTok,
                   p.mastodon            AS Mastodon,
                   p.website             AS Website,
                   p.local_headshot_path AS LocalHeadshotPath,
                   p.date_of_birth       AS DateOfBirth,
                   p.date_of_death       AS DateOfDeath,
                   p.place_of_birth      AS PlaceOfBirth,
                   p.place_of_death      AS PlaceOfDeath,
                   p.nationality         AS Nationality,
                   p.is_pseudonym        AS IsPseudonym,
                   p.is_group            AS IsGroup,
                   GROUP_CONCAT(DISTINCT primary_role.role) AS RolesCsv
            FROM persons p
            JOIN primary_person_media_credits primary_role
              ON primary_role.person_id = p.id
             AND primary_role.role <> 'Composer' COLLATE NOCASE
             AND NOT (p.is_group = 1 AND primary_role.role = 'Narrator' COLLATE NOCASE)
            JOIN media_assets ma ON ma.id = primary_role.media_asset_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE w.ownership = 'Owned'
              AND w.is_catalog_only = 0
              AND (
                  @search IS NULL
                  OR COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) LIKE '%' || @search || '%' COLLATE NOCASE
                  OR COALESCE(p.occupation, '') LIKE '%' || @search || '%' COLLATE NOCASE
                  OR EXISTS (
                      SELECT 1
                      FROM primary_person_media_credits role_search
                      JOIN media_assets role_asset ON role_asset.id = role_search.media_asset_id
                      JOIN editions role_edition ON role_edition.id = role_asset.edition_id
                      JOIN works role_work ON role_work.id = role_edition.work_id
                      WHERE role_search.person_id = p.id
                        AND role_search.role <> 'Composer' COLLATE NOCASE
                        AND NOT (p.is_group = 1 AND role_search.role = 'Narrator' COLLATE NOCASE)
                        AND role_search.role LIKE '%' || @search || '%' COLLATE NOCASE
                        AND role_work.ownership = 'Owned'
                        AND role_work.is_catalog_only = 0
                  )
              )
              AND (
                  @role IS NULL
                  OR primary_role.role = @role COLLATE NOCASE
              )
              AND (
                  @lane IS NULL
                  OR (@lane = 'Read' COLLATE NOCASE AND (
                      w.media_type LIKE '%book%' COLLATE NOCASE
                      OR w.media_type LIKE '%comic%' COLLATE NOCASE
                      OR w.media_type LIKE '%epub%' COLLATE NOCASE
                  ))
                  OR (@lane = 'Watch' COLLATE NOCASE AND (
                      w.media_type LIKE '%movie%' COLLATE NOCASE
                      OR w.media_type LIKE '%tv%' COLLATE NOCASE
                      OR w.media_type LIKE '%video%' COLLATE NOCASE
                  ))
                  OR (@lane = 'Listen' COLLATE NOCASE AND (
                      w.media_type LIKE '%audio%' COLLATE NOCASE
                      OR w.media_type LIKE '%music%' COLLATE NOCASE
                      OR w.media_type LIKE '%track%' COLLATE NOCASE
                  ))
              )
            GROUP BY p.id
            HAVING RolesCsv IS NOT NULL
            ORDER BY CASE WHEN @sort = 'count' THEN COUNT(DISTINCT w.id) END DESC,
                     COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.sort_name')), ''), NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) COLLATE NOCASE ASC
            LIMIT @limit OFFSET @offset;
            """, p).AsList();

        var results = rows.Select(MapFromCsvRow).ToList();
        return Task.FromResult<IReadOnlyList<Person>>(results);
    }

    /// <inheritdoc/>
    public Task<int> CountMediaLinksAsync(Guid personId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("id", personId);
        var count = conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM person_media_links WHERE person_id = @id;
            """, p);

        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<int> CountGraphReferencesAsync(Guid personId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>(
            """
            SELECT
                (SELECT COUNT(*) FROM person_media_links WHERE person_id = @id)
              + (SELECT COUNT(*) FROM person_group_members WHERE group_id = @id OR member_id = @id)
              + (SELECT COUNT(*) FROM person_aliases WHERE pseudonym_person_id = @id OR real_person_id = @id)
              + (SELECT COUNT(*) FROM character_performer_links WHERE person_id = @id)
              + (SELECT COUNT(*) FROM character_portraits WHERE person_id = @id);
            """,
            new { id = personId });

        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<Person?> FindByQidAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("qid", qid);
        var result = conn.QueryFirstOrDefault<Person>($"""
            SELECT {SelectColumns}
            FROM   persons
            WHERE  wikidata_qid = @qid COLLATE NOCASE
            LIMIT  1;
            """, p);

        if (result is not null)
        {
            PopulateRoles(conn, result);
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(Guid personId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();

        // Delete links first (FK-safe even without ON DELETE CASCADE).
        var p = new DynamicParameters();
        p.Add("id", personId);
        conn.Execute(
            "DELETE FROM person_roles WHERE person_id = @id;",
            p);
        conn.Execute(
            "DELETE FROM person_media_links WHERE person_id = @id;",
            p);

        conn.Execute(
            "DELETE FROM persons WHERE id = @id;",
            p);

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Role management (person_roles junction table)
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task AddRoleAsync(Guid personId, string role, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("personId", personId);
        p.Add("role", role);
        conn.Execute("""
            INSERT OR IGNORE INTO person_roles (person_id, role)
            VALUES (@personId, @role);
            """, p);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetRolesAsync(Guid personId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("id", personId);
        var roles = conn.Query<string>("""
            SELECT role FROM person_roles WHERE person_id = @id ORDER BY role;
            """, p).AsList();

        return Task.FromResult<IReadOnlyList<string>>(roles);
    }

    /// <inheritdoc/>
    public Task<Dictionary<string, int>> GetRoleCountsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string Role, int Count)>("""
            SELECT role AS Role, COUNT(DISTINCT person_id) AS Count
            FROM   person_roles
            GROUP  BY role;
            """).AsList();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, count) in rows)
        {
            result[role] = count;
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<Dictionary<string, int>> GetCatalogRoleCountsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string Role, int Count)>("""
            SELECT primary_credit.role AS Role,
                   COUNT(DISTINCT primary_credit.person_id) AS Count
            FROM primary_person_media_credits primary_credit
            JOIN persons p ON p.id = primary_credit.person_id
            JOIN media_assets ma ON ma.id = primary_credit.media_asset_id
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE primary_credit.role <> 'Composer' COLLATE NOCASE
              AND NOT (p.is_group = 1 AND primary_credit.role = 'Narrator' COLLATE NOCASE)
              AND w.ownership = 'Owned'
              AND w.is_catalog_only = 0
            GROUP BY primary_credit.role;
            """).AsList();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, count) in rows)
        {
            result[role] = count;
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public async Task<Dictionary<Guid, Dictionary<string, int>>> GetPresenceBatchAsync(
        IEnumerable<Guid> personIds,
        CancellationToken ct = default)
    {
        var credits = await GetPresenceCreditsAsync(personIds, ct).ConfigureAwait(false);
        var result = new Dictionary<Guid, Dictionary<string, int>>();
        foreach (var person in credits.GroupBy(credit => credit.PersonId))
        {
            result[person.Key] = person
                .GroupBy(credit => credit.MediaType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    type => type.First().MediaType,
                    type => type.Select(credit => credit.DisplayWorkId).Distinct().Count(),
                    StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    public Task<IReadOnlyList<PersonPresenceCredit>> GetPresenceCreditsAsync(
        IEnumerable<Guid> personIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var idList = personIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PersonPresenceCredit>>([]);
        }

        using var conn = _db.CreateConnection();
        // Presence is presentation data, so it follows the same canonical credits
        // as details and browse instead of the broader enrichment relationship graph.
        var rows = new List<PresenceRow>();
        foreach (var batch in idList.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var p = new DynamicParameters();
            var personIdClause = AddGuidBlobList(p, "personId", batch);
            // Credits are worked out only for the files that could credit these people, never for the whole library.
            var candidateAssetsCte = PersonCreditSql.CandidateAssetsCte(
                "presence_candidate_assets",
                $"SELECT id, name, wikidata_qid FROM persons WHERE id IN ({personIdClause})",
                "pc_");
            var scopedCreditsCte = PersonCreditSql.ScopedCreditsCte(
                "presence_credit_rows",
                "SELECT asset_id FROM presence_candidate_assets",
                "pr_");
            // A file in a Vault library never counts here, whoever asks: a read with no viewer keeps the Vault out through this
            // predicate, and a read for a viewer starts from these rows (security review leak 2).
            rows.AddRange(conn.Query<PresenceRow>($"""
                WITH {candidateAssetsCte},
                {scopedCreditsCte}
                SELECT DISTINCT p.id AS PersonId,
                       COALESCE(gp.media_type, parent.media_type, w.media_type) AS MediaType,
                       CASE
                           WHEN w.work_kind = 'child' THEN COALESCE(gp.id, parent.id, w.id)
                           WHEN w.work_kind = 'parent' AND parent.id IS NOT NULL THEN COALESCE(gp.id, parent.id, w.id)
                           ELSE w.id
                       END AS DisplayWorkId,
                       ma.id AS AssetId,
                       w.id AS WorkId,
                       ma.library_id AS LibraryId
                FROM persons p
                JOIN presence_credit_rows primary_credit ON primary_credit.person_id = p.id
                JOIN media_assets ma ON ma.id = primary_credit.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works parent ON parent.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = parent.parent_work_id
                WHERE p.id IN ({personIdClause})
                  AND w.ownership = 'Owned'
                  AND w.is_catalog_only = 0
                  AND {VaultSql.NotVaultAsset("ma.id")};
                """, p));
        }

        return Task.FromResult<IReadOnlyList<PersonPresenceCredit>>(
            rows.Select(row => new PersonPresenceCredit(row.PersonId, row.MediaType, row.DisplayWorkId, row.AssetId, row.WorkId, row.LibraryId)).ToList());
    }

    // -------------------------------------------------------------------------
    // Alias (pseudonym) management
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task LinkAliasAsync(
        Guid pseudonymPersonId,
        Guid realPersonId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("pseudonymId", pseudonymPersonId);
        p.Add("realId", realPersonId);
        conn.Execute("""
            INSERT OR IGNORE INTO person_aliases
                (pseudonym_person_id, real_person_id)
            VALUES
                (@pseudonymId, @realId);
            """, p);

        return Task.CompletedTask;
    }

    private sealed record CreditedFileRow
    {
        public Guid PersonId { get; init; }
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
        public string? LibraryId { get; init; }
    }

    public Task<IReadOnlyList<PersonCreditedFile>> GetCreditedFilesAsync(
        IEnumerable<Guid> personIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var idList = personIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PersonCreditedFile>>([]);
        }

        using var conn = _db.CreateConnection();
        var rows = new List<CreditedFileRow>();
        foreach (var batch in idList.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var p = new DynamicParameters();
            var personIdClause = AddGuidBlobList(p, "personId", batch);
            var candidateAssetsCte = PersonCreditSql.CandidateAssetsCte(
                "footprint_candidate_assets",
                $"SELECT id, name, wikidata_qid FROM persons WHERE id IN ({personIdClause})",
                "fc_");
            var scopedCreditsCte = PersonCreditSql.ScopedCreditsCte(
                "footprint_credit_rows",
                "SELECT asset_id FROM footprint_candidate_assets",
                "ff_");
            // Deliberately no Vault predicate: the caller needs to know a person IS credited somewhere the viewer cannot see.
            rows.AddRange(conn.Query<CreditedFileRow>($"""
                WITH {candidateAssetsCte},
                {scopedCreditsCte}
                SELECT DISTINCT credit.person_id AS PersonId, w.id AS WorkId, ma.id AS AssetId, ma.library_id AS LibraryId
                FROM (
                    SELECT person_id, media_asset_id FROM footprint_credit_rows WHERE person_id IN ({personIdClause})
                    UNION
                    SELECT person_id, media_asset_id FROM person_media_links WHERE person_id IN ({personIdClause})
                ) credit
                JOIN media_assets ma ON ma.id = credit.media_asset_id
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                WHERE ma.is_orphaned = 0;
                """, p));
        }

        return Task.FromResult<IReadOnlyList<PersonCreditedFile>>(
            rows.Select(row => new PersonCreditedFile(row.PersonId, row.WorkId, row.AssetId, row.LibraryId)).ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> FindAliasesAsync(
        Guid personId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        // Returns both directions: real people behind a pseudonym,
        // and pseudonyms used by a real person.
        var p = new DynamicParameters();
        p.Add("id", personId);
        var rows = conn.Query<PersonWithRolesCsv>($"""
            SELECT p.id                  AS Id,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) AS Name,
                   p.wikidata_qid        AS WikidataQid,
                   p.headshot_url        AS HeadshotUrl,
                   COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.biography')), ''), p.biography) AS Biography,
                   p.created_at          AS CreatedAt,
                   p.enriched_at         AS EnrichedAt,
                   p.occupation          AS Occupation,
                   p.instagram           AS Instagram,
                   p.twitter             AS Twitter,
                   p.tiktok              AS TikTok,
                   p.mastodon            AS Mastodon,
                   p.website             AS Website,
                   p.local_headshot_path AS LocalHeadshotPath,
                   p.date_of_birth       AS DateOfBirth,
                   p.date_of_death       AS DateOfDeath,
                   p.place_of_birth      AS PlaceOfBirth,
                   p.place_of_death      AS PlaceOfDeath,
                   p.nationality         AS Nationality,
                   p.is_pseudonym        AS IsPseudonym,
                   p.is_group            AS IsGroup,
                   GROUP_CONCAT(pr.role, ',') AS RolesCsv
            FROM   persons p
            LEFT JOIN person_roles pr ON pr.person_id = p.id
            WHERE  p.id IN (
                SELECT real_person_id FROM person_aliases WHERE pseudonym_person_id = @id
                UNION
                SELECT pseudonym_person_id FROM person_aliases WHERE real_person_id = @id
            )
            GROUP  BY p.id
            ORDER  BY COALESCE(NULLIF(TRIM(json_extract(p.display_overrides_json, '$.sort_name')), ''), NULLIF(TRIM(json_extract(p.display_overrides_json, '$.name')), ''), p.name) ASC;
            """, p).AsList();

        var results = rows.Select(MapFromCsvRow).ToList();
        return Task.FromResult<IReadOnlyList<Person>>(results);
    }

    // -------------------------------------------------------------------------
    // Character-performer links
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task LinkToCharacterAsync(
        Guid personId,
        Guid fictionalEntityId,
        string? workQid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("personId", personId);
        p.Add("entityId", fictionalEntityId);
        p.Add("workQid", workQid);
        conn.Execute("""
            INSERT OR IGNORE INTO character_performer_links
                (person_id, fictional_entity_id, work_qid)
            VALUES
                (@personId, @entityId, @workQid);
            """, p);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<(Guid FictionalEntityId, string? WorkQid)>> GetCharacterLinksAsync(
        Guid personId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("personId", personId);
        var rows = conn.Query<CharacterLinkRow>("""
            SELECT fictional_entity_id AS FictionalEntityId,
                   work_qid            AS WorkQid
            FROM   character_performer_links
            WHERE  person_id = @personId;
            """, p).AsList();

        IReadOnlyList<(Guid, string?)> result = rows
            .Select(r => (r.FictionalEntityId, r.WorkQid))
            .ToList();

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<(Guid PersonId, Guid FictionalEntityId)>> GetCharacterLinksByWorkAsync(
        string workQid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(workQid);

        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("workQid", workQid);
        var rows = conn.Query<CharacterLinkByWorkRow>("""
            SELECT person_id           AS PersonId,
                   fictional_entity_id AS FictionalEntityId
            FROM   character_performer_links
            WHERE  work_qid = @workQid;
            """, p).AsList();

        IReadOnlyList<(Guid, Guid)> result = rows
            .Select(r => (r.PersonId, r.FictionalEntityId))
            .ToList();

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> FindByQidsAsync(
        IEnumerable<string> qids,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(qids);
        ct.ThrowIfCancellationRequested();

        var normalizedQids = qids
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Select(qid => qid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedQids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Person>>([]);
        }

        using var conn = _db.CreateConnection();
        var people = new List<Person>();
        foreach (var batch in normalizedQids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            people.AddRange(conn.Query<Person>($"""
                SELECT {SelectColumns}
                FROM   persons
                WHERE  wikidata_qid COLLATE NOCASE IN @qids
                ORDER  BY name;
                """, new { qids = batch }));
        }

        PopulateRoles(conn, people, ct);
        return Task.FromResult<IReadOnlyList<Person>>(people);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Person>> FindByNamesAsync(
        IEnumerable<string> names,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ct.ThrowIfCancellationRequested();

        var normalizedNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedNames.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Person>>([]);
        }

        using var conn = _db.CreateConnection();
        var people = new List<Person>();
        foreach (var batch in normalizedNames.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            people.AddRange(conn.Query<Person>($"""
                SELECT {SelectColumns}
                FROM   persons
                WHERE  name COLLATE NOCASE IN @names
                ORDER  BY name;
                """, new { names = batch }));
        }

        PopulateRoles(conn, people, ct);
        return Task.FromResult<IReadOnlyList<Person>>(people);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CharacterPerformerCredit>> GetCharacterPerformersAsync(
        IEnumerable<Guid> fictionalEntityIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fictionalEntityIds);
        ct.ThrowIfCancellationRequested();

        var ids = fictionalEntityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<CharacterPerformerCredit>>([]);
        }

        using var conn = _db.CreateConnection();
        var rows = new List<CharacterPerformerRow>();
        foreach (var batch in ids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            var idClause = AddGuidBlobList(parameters, "fictionalEntityId", batch);
            rows.AddRange(conn.Query<CharacterPerformerRow>($"""
                SELECT cpl.person_id           AS PersonId,
                       cpl.fictional_entity_id AS FictionalEntityId,
                       cpl.work_qid            AS WorkQid,
                       p.name                  AS PerformerName,
                       p.headshot_url          AS HeadshotUrl,
                       p.local_headshot_path   AS LocalHeadshotPath
                FROM   character_performer_links cpl
                JOIN   persons p ON p.id = cpl.person_id
                WHERE  cpl.fictional_entity_id IN ({idClause})
                ORDER  BY cpl.fictional_entity_id, p.name, cpl.work_qid;
                """, parameters));
        }

        IReadOnlyList<CharacterPerformerCredit> result = rows
            .Select(row => new CharacterPerformerCredit(
                row.PersonId,
                row.FictionalEntityId,
                row.WorkQid,
                row.PerformerName,
                row.HeadshotUrl,
                row.LocalHeadshotPath))
            .ToList();
        return Task.FromResult(result);
    }

    // -------------------------------------------------------------------------
    // QID-based deduplication merge
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task ReassignAllLinksAsync(
        Guid fromPersonId,
        Guid toPersonId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            // 1. Reassign media links (OR IGNORE handles PK conflicts)
            var pToFrom = new DynamicParameters();
            pToFrom.Add("to", toPersonId);
            pToFrom.Add("from", fromPersonId);

            var pFrom = new DynamicParameters();
            pFrom.Add("from", fromPersonId);

            conn.Execute(
                "UPDATE OR IGNORE person_media_links SET person_id = @to WHERE person_id = @from;",
                pToFrom, transaction: tx);
            conn.Execute(
                "DELETE FROM person_media_links WHERE person_id = @from;",
                pFrom, transaction: tx);

            // 2. Reassign person_roles (merge roles from source into target)
            conn.Execute(
                "INSERT OR IGNORE INTO person_roles (person_id, role) SELECT @to, role FROM person_roles WHERE person_id = @from;",
                pToFrom, transaction: tx);
            conn.Execute(
                "DELETE FROM person_roles WHERE person_id = @from;",
                pFrom, transaction: tx);

            // 3. Reassign character-performer links
            conn.Execute(
                "UPDATE OR IGNORE character_performer_links SET person_id = @to WHERE person_id = @from;",
                pToFrom, transaction: tx);
            conn.Execute(
                "DELETE FROM character_performer_links WHERE person_id = @from;",
                pFrom, transaction: tx);

            // 4. Reassign alias links (both directions)
            conn.Execute(
                "UPDATE OR IGNORE person_aliases SET pseudonym_person_id = @to WHERE pseudonym_person_id = @from;",
                pToFrom, transaction: tx);
            conn.Execute(
                "UPDATE OR IGNORE person_aliases SET real_person_id = @to WHERE real_person_id = @from;",
                pToFrom, transaction: tx);
            conn.Execute(
                "DELETE FROM person_aliases WHERE pseudonym_person_id = @from OR real_person_id = @from;",
                pFrom, transaction: tx);

            // 5. Preserve musical-group membership in both directions. Group
            // member hydration often discovers that a temporary Wikidata stub
            // is the same person as an existing credited artist. Deleting that
            // stub without moving these rows would cascade away the band's
            // membership edge. The same rule applies when duplicate group
            // identities themselves are merged.
            conn.Execute(
                """
                INSERT OR IGNORE INTO person_group_members
                    (group_id, member_id, start_date, end_date)
                SELECT group_id, @to, start_date, end_date
                FROM person_group_members
                WHERE member_id = @from
                  AND group_id <> @to;
                """,
                pToFrom, transaction: tx);
            conn.Execute(
                """
                INSERT OR IGNORE INTO person_group_members
                    (group_id, member_id, start_date, end_date)
                SELECT @to, member_id, start_date, end_date
                FROM person_group_members
                WHERE group_id = @from
                  AND member_id <> @to;
                """,
                pToFrom, transaction: tx);
            conn.Execute(
                "DELETE FROM person_group_members WHERE group_id = @from OR member_id = @from;",
                pFrom, transaction: tx);

        }, ct);
    }

    /// <inheritdoc/>
    public Task<bool> IsPseudonymOrAliasAsync(Guid personId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();

        // Check persons.is_pseudonym flag first - cheapest query.
        var pId = new DynamicParameters();
        pId.Add("id", personId);

        var isPseudo = conn.ExecuteScalar<int>(
            "SELECT COUNT(1) FROM persons WHERE id = @id AND is_pseudonym = 1;",
            pId) > 0;

        if (isPseudo)
        {
            return Task.FromResult(true);
        }

        // Check person_aliases in either direction - pen name or real author.
        var isAlias = conn.ExecuteScalar<int>("""
            SELECT COUNT(1) FROM person_aliases
            WHERE pseudonym_person_id = @id OR real_person_id = @id;
            """, pId) > 0;

        return Task.FromResult(isAlias);
    }

    /// <inheritdoc/>
    public Task LinkGroupMemberAsync(Guid groupId, Guid memberId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute(
            """
            INSERT OR IGNORE INTO person_group_members (group_id, member_id)
            VALUES (@GroupId, @MemberId)
            """,
                new { GroupId = groupId, MemberId = memberId },
                tx);
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static string AddGuidBlobList(
        DynamicParameters parameters,
        string prefix,
        IReadOnlyList<Guid> ids)
    {
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var name = $"{prefix}{i}";
            names[i] = $"@{name}";
            parameters.Add(name, GuidSql.ToBlob(ids[i]));
        }

        return string.Join(", ", names);
    }

    /// <summary>
    /// Populates the <see cref="Person.Roles"/> list from the <c>person_roles</c> table
    /// for a single person that was loaded without a GROUP_CONCAT join.
    /// </summary>
    private static void PopulateRoles(Microsoft.Data.Sqlite.SqliteConnection conn, Person person)
    {
        var p = new DynamicParameters();
        p.Add("id", person.Id);
        person.Roles = conn.Query<string>("""
            SELECT role FROM person_roles WHERE person_id = @id ORDER BY role;
            """, p).AsList();
    }

    private static void PopulateRoles(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        IReadOnlyList<Person> people,
        CancellationToken ct)
    {
        if (people.Count == 0)
        {
            return;
        }

        var rolesByPerson = new Dictionary<Guid, List<string>>();
        foreach (var batch in people.Select(person => person.Id)
                     .Distinct()
                     .Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            var idClause = AddGuidBlobList(parameters, "personId", batch);
            foreach (var row in conn.Query<PersonRoleRow>($"""
                         SELECT person_id AS PersonId,
                                role      AS Role
                         FROM   person_roles
                         WHERE  person_id IN ({idClause})
                         ORDER  BY person_id, role;
                         """, parameters))
            {
                if (!rolesByPerson.TryGetValue(row.PersonId, out var roles))
                {
                    roles = [];
                    rolesByPerson[row.PersonId] = roles;
                }

                roles.Add(row.Role);
            }
        }

        foreach (var person in people)
        {
            person.Roles = rolesByPerson.GetValueOrDefault(person.Id) ?? [];
        }
    }

    /// <summary>
    /// Maps a <see cref="PersonWithRolesCsv"/> row (from GROUP_CONCAT query) to a
    /// <see cref="Person"/> entity with the <see cref="Person.Roles"/> list populated.
    /// </summary>
    private static Person MapFromCsvRow(PersonWithRolesCsv row)
    {
        var roles = string.IsNullOrEmpty(row.RolesCsv)
            ? new List<string>()
            : row.RolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList();

        return new Person
        {
            Id = row.Id,
            Name = row.Name,
            Roles = roles,
            WikidataQid = row.WikidataQid,
            HeadshotUrl = row.HeadshotUrl,
            Biography = row.Biography,
            CreatedAt = row.CreatedAt is not null ? DateTimeOffset.Parse(row.CreatedAt) : DateTimeOffset.UtcNow,
            EnrichedAt = row.EnrichedAt is not null ? DateTimeOffset.Parse(row.EnrichedAt) : null,
            Occupation = row.Occupation,
            Instagram = row.Instagram,
            Twitter = row.Twitter,
            TikTok = row.TikTok,
            Mastodon = row.Mastodon,
            Website = row.Website,
            LocalHeadshotPath = row.LocalHeadshotPath,
            DateOfBirth = row.DateOfBirth,
            DateOfDeath = row.DateOfDeath,
            PlaceOfBirth = row.PlaceOfBirth,
            PlaceOfDeath = row.PlaceOfDeath,
            Nationality = row.Nationality,
            IsPseudonym = row.IsPseudonym != 0,
            IsGroup = row.IsGroup != 0,
        };
    }
}
