using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IEntityAssetRepository"/>.
/// Uses Dapper for type-safe column-to-property mapping.
///
/// Manages typed image assets (Cover Art, Headshot, Logo, Background)
/// for any entity — Work, Person, Universe, or FictionalEntity.
/// </summary>
public sealed class EntityAssetRepository : IEntityAssetRepository
{
    private readonly IDatabaseConnection _db;

    // Reusable SELECT list with aliases matching EntityAsset property names.
    private const string SelectColumns = """
        id               AS Id,
        """ + GuidSql.EntityIdProjection + """
                         AS EntityId,
        entity_type      AS EntityType,
        asset_type       AS AssetTypeValue,
        image_url        AS ImageUrl,
        local_image_path AS LocalImagePath,
        local_image_path_s AS LocalImagePathSmall,
        local_image_path_m AS LocalImagePathMedium,
        local_image_path_l AS LocalImagePathLarge,
        source_provider  AS SourceProvider,
        width_px         AS WidthPx,
        height_px        AS HeightPx,
        aspect_class     AS AspectClass,
        primary_hex      AS PrimaryHex,
        secondary_hex    AS SecondaryHex,
        accent_hex       AS AccentHex,
        asset_class      AS AssetClassValue,
        storage_location AS StorageLocationValue,
        owner_scope      AS OwnerScope,
        is_preferred     AS IsPreferred,
        is_user_override AS IsUserOverride,
        is_locally_exported   AS IsLocallyExported,
        is_preferred_exported AS IsPreferredExported,
        created_at       AS CreatedAt,
        updated_at       AS UpdatedAt
        """;

    public EntityAssetRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EntityAsset>> GetByEntityAsync(
        string entityId, string? assetType = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        using var conn = _db.CreateConnection();

        IEnumerable<EntityAsset> rows;

        if (assetType is null)
        {
            rows = conn.Query<EntityAsset>($"""
                SELECT {SelectColumns}
                FROM   entity_assets
                WHERE  entity_id = @entityId
                ORDER BY asset_type, is_preferred DESC, created_at;
                """, new { entityId = ToEntityIdParameter(entityId) });
        }
        else
        {
            rows = conn.Query<EntityAsset>($"""
                SELECT {SelectColumns}
                FROM   entity_assets
                WHERE  entity_id = @entityId
                AND    asset_type = @assetType
                ORDER BY is_preferred DESC, created_at;
                """, new { entityId = ToEntityIdParameter(entityId), assetType });
        }

        return Task.FromResult<IReadOnlyList<EntityAsset>>(rows.ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EntityAsset>> GetByEntitiesAsync(
        IReadOnlyCollection<string> entityIds,
        string? assetType = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entityIds);

        var normalizedIds = entityIds
            .Where(entityId => !string.IsNullOrWhiteSpace(entityId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ToEntityIdParameter)
            .ToArray();
        if (normalizedIds.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<EntityAsset>>([]);
        }

        using var conn = _db.CreateConnection();
        var results = new List<EntityAsset>();
        foreach (var batch in normalizedIds.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(assetType is null
                ? conn.Query<EntityAsset>($"""
                    SELECT {SelectColumns}
                    FROM   entity_assets
                    WHERE  entity_id IN @entityIds
                    ORDER BY entity_id, asset_type, is_preferred DESC, created_at;
                    """, new { entityIds = batch })
                : conn.Query<EntityAsset>($"""
                    SELECT {SelectColumns}
                    FROM   entity_assets
                    WHERE  entity_id IN @entityIds
                    AND    asset_type = @assetType
                    ORDER BY entity_id, is_preferred DESC, created_at;
                    """, new { entityIds = batch, assetType }));
        }

        return Task.FromResult<IReadOnlyList<EntityAsset>>(results);
    }

    /// <inheritdoc/>
    public Task<EntityAsset?> FindByIdAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var result = conn.QuerySingleOrDefault<EntityAsset>($"""
            SELECT {SelectColumns}
            FROM   entity_assets
            WHERE  id = @assetId
            LIMIT  1;
            """, new { assetId });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task UpsertAsync(EntityAsset asset, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(asset);
        if (!Enum.TryParse<MediaEngine.Domain.Enums.AssetType>(asset.AssetTypeValue, ignoreCase: true, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(asset), asset.AssetTypeValue, "Unsupported entity artwork type.");
        }

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO entity_assets
                (id, entity_id, entity_type, asset_type, image_url,
                 local_image_path, local_image_path_s, local_image_path_m, local_image_path_l,
                 source_provider, width_px, height_px, aspect_class, primary_hex, secondary_hex, accent_hex,
                 asset_class, storage_location, owner_scope,
                 is_preferred, is_user_override, is_locally_exported, is_preferred_exported, created_at)
            VALUES
                (@Id, @EntityId, @EntityType, @AssetTypeValue, @ImageUrl,
                 @LocalImagePath, @LocalImagePathSmall, @LocalImagePathMedium, @LocalImagePathLarge,
                 @SourceProvider, @WidthPx, @HeightPx, @AspectClass, @PrimaryHex, @SecondaryHex, @AccentHex,
                 @AssetClassValue, @StorageLocationValue, @OwnerScope,
                 @IsPreferred, @IsUserOverride, @IsLocallyExported, @IsPreferredExported, @CreatedAt)
            ON CONFLICT(id) DO UPDATE SET
                image_url        = excluded.image_url,
                local_image_path = excluded.local_image_path,
                local_image_path_s = excluded.local_image_path_s,
                local_image_path_m = excluded.local_image_path_m,
                local_image_path_l = excluded.local_image_path_l,
                width_px         = excluded.width_px,
                height_px        = excluded.height_px,
                aspect_class     = excluded.aspect_class,
                primary_hex      = excluded.primary_hex,
                secondary_hex    = excluded.secondary_hex,
                accent_hex       = excluded.accent_hex,
                asset_class      = excluded.asset_class,
                storage_location = excluded.storage_location,
                owner_scope      = excluded.owner_scope,
                is_preferred     = excluded.is_preferred,
                is_user_override = excluded.is_user_override,
                is_locally_exported   = excluded.is_locally_exported,
                is_preferred_exported = excluded.is_preferred_exported,
                updated_at       = datetime('now');
            """,
            new
            {
                asset.Id,
                EntityId = ToEntityIdParameter(asset.EntityId),
                asset.EntityType,
                asset.AssetTypeValue,
                asset.ImageUrl,
                asset.LocalImagePath,
                asset.LocalImagePathSmall,
                asset.LocalImagePathMedium,
                asset.LocalImagePathLarge,
                asset.SourceProvider,
                asset.WidthPx,
                asset.HeightPx,
                asset.AspectClass,
                asset.PrimaryHex,
                asset.SecondaryHex,
                asset.AccentHex,
                asset.AssetClassValue,
                asset.StorageLocationValue,
                asset.OwnerScope,
                IsPreferred = asset.IsPreferred ? 1 : 0,
                IsUserOverride = asset.IsUserOverride ? 1 : 0,
                IsLocallyExported = asset.IsLocallyExported ? 1 : 0,
                IsPreferredExported = asset.IsPreferredExported ? 1 : 0,
                CreatedAt = asset.CreatedAt.ToString("O"),
            });

        SyncCanonicalArtwork(conn, asset);

        return Task.CompletedTask;
    }

    private static void SyncCanonicalArtwork(System.Data.IDbConnection conn, EntityAsset asset)
    {
        if (!string.Equals(asset.AssetClassValue, "Artwork", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var contentHash = ComputeArtworkIdentity(asset);
        conn.Execute("""
            INSERT OR IGNORE INTO artwork_assets (
                id, content_hash, original_path, small_path, medium_path, large_path,
                width_px, height_px, aspect_class, primary_hex, secondary_hex, accent_hex,
                source_provider, source_url, created_at, updated_at)
            VALUES (
                @Id, @ContentHash, @LocalImagePath, @LocalImagePathSmall, @LocalImagePathMedium, @LocalImagePathLarge,
                @WidthPx, @HeightPx, @AspectClass, @PrimaryHex, @SecondaryHex, @AccentHex,
                @SourceProvider, @ImageUrl, @CreatedAt, @UpdatedAt);

            UPDATE artwork_assets SET
                original_path=COALESCE(original_path, @LocalImagePath),
                small_path=COALESCE(small_path, @LocalImagePathSmall),
                medium_path=COALESCE(medium_path, @LocalImagePathMedium),
                large_path=COALESCE(large_path, @LocalImagePathLarge),
                width_px=COALESCE(width_px, @WidthPx),
                height_px=COALESCE(height_px, @HeightPx),
                source_provider=COALESCE(source_provider, @SourceProvider),
                source_url=COALESCE(source_url, @ImageUrl),
                updated_at=COALESCE(@UpdatedAt, @CreatedAt)
            WHERE content_hash=@ContentHash;
            """, new
        {
            asset.Id,
            ContentHash = contentHash,
            asset.LocalImagePath,
            asset.LocalImagePathSmall,
            asset.LocalImagePathMedium,
            asset.LocalImagePathLarge,
            asset.WidthPx,
            asset.HeightPx,
            asset.AspectClass,
            asset.PrimaryHex,
            asset.SecondaryHex,
            asset.AccentHex,
            asset.SourceProvider,
            asset.ImageUrl,
            CreatedAt = asset.CreatedAt.ToString("O"),
            UpdatedAt = asset.UpdatedAt?.ToString("O"),
        });

        var canonicalId = conn.ExecuteScalar<Guid>(
            "SELECT id FROM artwork_assets WHERE content_hash=@contentHash LIMIT 1;",
            new { contentHash });
        var role = asset.AssetTypeValue switch
        {
            "Headshot" or "CharacterPortrait" => "Portrait",
            "Background" or "Banner" or "SeasonThumb" => "Background",
            "Logo" or "NetworkLogo" or "StudioLogo" => "Logo",
            _ => "Primary",
        };
        var context = asset.AssetTypeValue switch
        {
            "SeasonPoster" => "Season",
            "EpisodeStill" => "Episode",
            "NetworkLogo" => "Network",
            "StudioLogo" => "Studio",
            _ => string.Empty,
        };
        conn.Execute("""
            DELETE FROM entity_artwork_links
            WHERE id=@Id
               OR (entity_id=@EntityId
                   AND entity_type=@EntityType
                   AND artwork_asset_id=@CanonicalId
                   AND role=@Role
                   AND context=@Context);
            INSERT INTO entity_artwork_links (
                id, entity_id, entity_type, artwork_asset_id, role, context,
                source_asset_type, is_preferred, is_user_override, created_at, updated_at)
            VALUES (
                @Id, @EntityId, @EntityType, @CanonicalId, @Role, @Context,
                @AssetType, @IsPreferred, @IsUserOverride, @CreatedAt, @UpdatedAt);
            """, new
        {
            asset.Id,
            EntityId = ToEntityIdParameter(asset.EntityId),
            asset.EntityType,
            CanonicalId = canonicalId,
            Role = role,
            Context = context,
            AssetType = asset.AssetTypeValue,
            IsPreferred = asset.IsPreferred ? 1 : 0,
            IsUserOverride = asset.IsUserOverride ? 1 : 0,
            CreatedAt = asset.CreatedAt.ToString("O"),
            UpdatedAt = asset.UpdatedAt?.ToString("O"),
        });

        SyncCanonicalArtworkContext(
            conn,
            canonicalId,
            ToEntityIdParameter(asset.EntityId),
            asset.EntityType,
            role,
            asset.SourceProvider,
            asset.CreatedAt);
    }

    private static void SyncCanonicalArtworkContext(
        IDbConnection conn,
        Guid artworkAssetId,
        object entityId,
        string entityType,
        string role,
        string? provider,
        DateTimeOffset createdAt)
    {
        const string upsertSuffix = """
            ON CONFLICT(artwork_asset_id, entity_id, entity_type, role) DO UPDATE SET
                entity_label=excluded.entity_label,
                media_type=excluded.media_type,
                year=excluded.year,
                provider=excluded.provider,
                canonical_id=excluded.canonical_id,
                search_text=excluded.search_text,
                updated_at=excluded.created_at;
            """;
        var parameters = new
        {
            artworkAssetId,
            entityId,
            entityType,
            role,
            provider,
            createdAt = createdAt.ToString("O"),
        };

        if (entityType.Equals("Work", StringComparison.OrdinalIgnoreCase))
        {
            conn.Execute($"""
                INSERT INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, media_type,
                    year, role, provider, canonical_id, search_text, created_at)
                SELECT @artworkAssetId, work.id, 'Work',
                       COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='title'), 'Untitled media'),
                       work.media_type,
                       (SELECT value FROM canonical_values WHERE entity_id=work.id AND key IN ('release_year','year') ORDER BY CASE key WHEN 'release_year' THEN 0 ELSE 1 END LIMIT 1),
                       @role, @provider,
                       (SELECT value FROM canonical_values WHERE entity_id=work.id AND key='wikidata_qid' LIMIT 1),
                       trim(COALESCE((SELECT value FROM canonical_values WHERE entity_id=work.id AND key='title'), 'Untitled media') || ' ' ||
                            COALESCE((SELECT group_concat(value, ' ') FROM canonical_value_arrays WHERE entity_id=work.id AND key IN ('title_alias','alternate_title','author','creator')), '') || ' ' ||
                            work.media_type || ' ' || @role || ' ' || COALESCE(@provider, '')),
                       @createdAt
                FROM works work WHERE work.id=@entityId
                {upsertSuffix}
                """, parameters);
            return;
        }

        if (entityType.Equals("Person", StringComparison.OrdinalIgnoreCase))
        {
            conn.Execute($"""
                INSERT INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, role, provider, canonical_id, search_text, created_at)
                SELECT @artworkAssetId, person.id, 'Person', person.name, @role, @provider,
                       person.wikidata_qid,
                       trim(person.name || ' ' || COALESCE(person.occupation, '') || ' ' ||
                            COALESCE(person.wikidata_qid, '') || ' ' || @role || ' ' || COALESCE(@provider, '')),
                       @createdAt
                FROM persons person WHERE person.id=@entityId
                {upsertSuffix}
                """, parameters);
            return;
        }

        if (entityType.Equals("Collection", StringComparison.OrdinalIgnoreCase)
            || entityType.Equals("Universe", StringComparison.OrdinalIgnoreCase))
        {
            conn.Execute($"""
                INSERT INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, media_type,
                    role, provider, canonical_id, search_text, created_at)
                SELECT @artworkAssetId, collection.id, @entityType, collection.display_name,
                       collection.primary_area, @role, @provider, collection.wikidata_qid,
                       trim(collection.display_name || ' ' || collection.collection_type || ' ' ||
                            COALESCE(collection.primary_area, '') || ' ' || @role || ' ' ||
                            COALESCE(collection.wikidata_qid, '') || ' ' || COALESCE(@provider, '')),
                       @createdAt
                FROM collections collection WHERE collection.id=@entityId
                {upsertSuffix}
                """, parameters);
            return;
        }

        if (entityType.Equals("FictionalEntity", StringComparison.OrdinalIgnoreCase))
        {
            conn.Execute($"""
                INSERT INTO artwork_asset_context (
                    artwork_asset_id, entity_id, entity_type, entity_label, role, provider, canonical_id, search_text, created_at)
                SELECT @artworkAssetId, entity.id, 'FictionalEntity', entity.label, @role, @provider, entity.wikidata_qid,
                       trim(entity.label || ' ' || COALESCE(entity.entity_sub_type, '') || ' ' ||
                            COALESCE(entity.fictional_universe_label, '') || ' ' || @role || ' ' ||
                            COALESCE(entity.wikidata_qid, '') || ' ' || COALESCE(@provider, '')),
                       @createdAt
                FROM fictional_entities entity WHERE entity.id=@entityId
                {upsertSuffix}
                """, parameters);
        }
    }

    private static string ComputeArtworkIdentity(EntityAsset asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.LocalImagePath) && File.Exists(asset.LocalImagePath))
        {
            using var stream = File.OpenRead(asset.LocalImagePath);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        var fallback = asset.ImageUrl ?? $"legacy:{asset.Id:D}";
        return "source:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fallback)));
    }

    /// <inheritdoc/>
    public Task SetPreferredAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            // Find the target asset's entity_id and asset_type.
            var target = conn.QuerySingleOrDefault<EntityAssetTargetRow>($"""
                SELECT {GuidSql.EntityIdProjection} AS EntityId, asset_type AS AssetType
                FROM   entity_assets
                WHERE  id = @assetId;
                """, new { assetId }, tx);

            if (target is null)
            {
                return;
            }

            // Clear preferred flag on all assets with the same entity + asset type.
            conn.Execute("""
                UPDATE entity_assets
                SET    is_preferred = 0,
                       updated_at  = datetime('now')
                WHERE  entity_id  = @entityId
                AND    asset_type = @assetType
                AND    is_preferred = 1;
                """, new { entityId = ToEntityIdParameter(target.EntityId), assetType = target.AssetType }, tx);

            // Set the target as preferred.
            conn.Execute("""
                UPDATE entity_assets
                SET    is_preferred = 1,
                       updated_at  = datetime('now')
                WHERE  id = @assetId;
                """, new { assetId }, tx);

            var role = target.AssetType switch
            {
                "Headshot" or "CharacterPortrait" => "Portrait",
                "Background" or "Banner" or "SeasonThumb" => "Background",
                "Logo" or "NetworkLogo" or "StudioLogo" => "Logo",
                _ => "Primary",
            };
            conn.Execute("""
                UPDATE entity_artwork_links
                SET is_preferred=0, updated_at=datetime('now')
                WHERE entity_id=@entityId AND role=@role;
                UPDATE entity_artwork_links
                SET is_preferred=1, updated_at=datetime('now')
                WHERE id=@assetId;
                """, new { entityId = ToEntityIdParameter(target.EntityId), role, assetId }, tx);

        }, ct);
    }

    /// <inheritdoc/>
    public Task DeleteByEntityAsync(string entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            DELETE FROM entity_artwork_links
            WHERE entity_id = @entityId;
            DELETE FROM entity_assets
            WHERE  entity_id = @entityId;
            """, new { entityId = ToEntityIdParameter(entityId) });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            DELETE FROM entity_artwork_links
            WHERE id = @assetId;
            DELETE FROM entity_assets
            WHERE  id = @assetId;
            """, new { assetId });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<EntityAsset?> GetPreferredAsync(
        string entityId, string assetType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetType);

        using var conn = _db.CreateConnection();
        var result = conn.QuerySingleOrDefault<EntityAsset>($"""
            SELECT {SelectColumns}
            FROM   entity_assets
            WHERE  entity_id   = @entityId
            AND    asset_type  = @assetType
            AND    is_preferred = 1
            LIMIT  1;
            """, new { entityId = ToEntityIdParameter(entityId), assetType });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EntityAsset>> GetPreferredByEntitiesAsync(
        IReadOnlyCollection<string> entityIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (entityIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<EntityAsset>>([]);
        }

        using var conn = _db.CreateConnection();
        var entityIdValues = entityIds
            .Select(ToEntityIdParameter)
            .ToArray();
        var results = conn.Query<EntityAsset>($"""
            SELECT {SelectColumns}
            FROM   entity_assets
            WHERE  is_preferred = 1
            AND    entity_id IN @entityIds;
            """, new { entityIds = entityIdValues }).ToList();

        return Task.FromResult<IReadOnlyList<EntityAsset>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EntityAsset>> GetPreferredArtworkAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var results = conn.Query<EntityAsset>($"""
            SELECT {SelectColumns}
            FROM   entity_assets
            WHERE  is_preferred = 1
            AND    asset_class = 'Artwork'
            ORDER BY updated_at DESC, created_at DESC;
            """).ToList();

        return Task.FromResult<IReadOnlyList<EntityAsset>>(results);
    }

    private static object ToEntityIdParameter(string entityId) =>
        Guid.TryParse(entityId, out var guid)
            ? GuidSql.ToBlob(guid)
            : entityId;

    private sealed class EntityAssetTargetRow
    {
        public string EntityId { get; set; } = "";
        public string AssetType { get; set; } = "";
    }
}
