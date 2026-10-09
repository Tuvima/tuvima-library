using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Security;

internal enum CatalogueResourceAccess { NotFound, Denied, Allowed }

internal sealed class CatalogueResourceAuthorizationService(
    IDatabaseConnection database,
    IRequestAuthorityResolver authorities,
    IAccountAccessDecisionService accounts,
    IAuthorizationEvaluator evaluator)
{
    public async ValueTask<CatalogueResourceAccess> EvaluateArtworkLinkAsync(
        HttpContext context, Guid linkId, ApplicationPermissionId permission, CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        var owner = await connection.QuerySingleOrDefaultAsync<ArtworkOwner>(new CommandDefinition(
            $"SELECT {GuidSql.EntityIdProjection} AS EntityId, entity_type AS EntityType FROM entity_artwork_links WHERE id=@linkId;",
            new { linkId }, cancellationToken: ct)).ConfigureAwait(false);
        return owner is not null && Guid.TryParse(owner.EntityId, out var entityId)
            ? await EvaluateEntityAsync(context, owner.EntityType, entityId, permission, ct).ConfigureAwait(false)
            : CatalogueResourceAccess.NotFound;
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateArtworkVariantAsync(
        HttpContext context,
        Guid variantId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        if (variantId == Guid.Empty)
        {
            return CatalogueResourceAccess.NotFound;
        }

        using var connection = database.CreateConnection();
        var owner = await connection.QuerySingleOrDefaultAsync<ArtworkOwner>(new CommandDefinition(
            $"""
            SELECT {GuidSql.EntityIdProjection} AS EntityId, entity_type AS EntityType
            FROM entity_assets
            WHERE id=@variantId
            LIMIT 1;
            """,
            new { variantId },
            cancellationToken: ct)).ConfigureAwait(false);
        if (owner is null || !Guid.TryParse(owner.EntityId, out var ownerId))
        {
            return CatalogueResourceAccess.NotFound;
        }

        var access = await EvaluateEntityAsync(
            context, owner.EntityType, ownerId, applicationPermission, ct).ConfigureAwait(false);
        return access == CatalogueResourceAccess.NotFound
            ? CatalogueResourceAccess.Denied
            : access;
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateEntityAsync(
        HttpContext context,
        string entityType,
        Guid entityId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        if (entityId == Guid.Empty || string.IsNullOrWhiteSpace(entityType))
        {
            return CatalogueResourceAccess.NotFound;
        }

        var authority = await authorities.ResolveAsync(context, ct).ConfigureAwait(false);
        var candidates = await GetEntityAssetCandidatesAsync(entityType, entityId, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return CatalogueResourceAccess.NotFound;
        }

        foreach (var assetId in candidates)
        {
            if (await EvaluateAssetAsync(authority, assetId, applicationPermission, ct).ConfigureAwait(false) ==
                CatalogueResourceAccess.Allowed)
            {
                return CatalogueResourceAccess.Allowed;
            }
        }
        return CatalogueResourceAccess.Denied;
    }

    public async ValueTask<Guid?> FindAuthorizedAssetForWorkAsync(
        HttpContext context,
        Guid workId,
        Guid? profileId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        var candidates = await GetAuthorizedAssetIdsForWorkAsync(
            context, workId, profileId, applicationPermission, ct).ConfigureAwait(false);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    public async ValueTask<IReadOnlyList<Guid>> GetAuthorizedAssetIdsForWorkAsync(
        HttpContext context,
        Guid workId,
        Guid? profileId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        if (workId == Guid.Empty)
        {
            return [];
        }

        var authority = await authorities.ResolveAsync(context, ct).ConfigureAwait(false);
        using var connection = database.CreateConnection();
        workId = WorkRedirects.Resolve(connection, workId);
        var candidates = (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id=ma.edition_id
            LEFT JOIN user_states us ON us.asset_id=ma.id AND us.user_id=@profileId
            WHERE e.work_id=@workId AND ma.status='Normal' AND ma.is_orphaned=0
            ORDER BY CASE WHEN LOWER(ma.file_path_root) LIKE '%.epub' THEN 0 ELSE 1 END,
                     us.last_accessed DESC, ma.id;
            """,
            new { workId, profileId },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        var allowed = new List<Guid>(candidates.Count);
        foreach (var assetId in candidates)
        {
            if (await EvaluateAssetAsync(authority, assetId, applicationPermission, ct).ConfigureAwait(false) ==
                CatalogueResourceAccess.Allowed)
            {
                allowed.Add(assetId);
            }
        }
        return allowed;
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateAssetAsync(
        HttpContext context,
        Guid assetId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        var authority = await authorities.ResolveAsync(context, ct).ConfigureAwait(false);
        return await EvaluateAssetAsync(authority, assetId, applicationPermission, ct).ConfigureAwait(false);
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateAnyEntityAsync(
        HttpContext context,
        Guid entityId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        var asset = await EvaluateAssetAsync(context, entityId, applicationPermission, ct).ConfigureAwait(false);
        if (asset != CatalogueResourceAccess.NotFound)
        {
            return asset;
        }

        var found = false;
        foreach (var entityType in new[] { "Work", "Edition", "Collection", "Person", "Character" })
        {
            var result = await EvaluateEntityAsync(
                context, entityType, entityId, applicationPermission, ct).ConfigureAwait(false);
            if (result == CatalogueResourceAccess.Allowed)
            {
                return result;
            }

            found |= result == CatalogueResourceAccess.Denied;
        }

        return found ? CatalogueResourceAccess.Denied : CatalogueResourceAccess.NotFound;
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateCharacterPortraitAsync(
        HttpContext context,
        Guid portraitId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        var characterId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT fictional_entity_id FROM character_portraits WHERE id=@portraitId LIMIT 1;",
            new { portraitId },
            cancellationToken: ct)).ConfigureAwait(false);
        return characterId is { } id && id != Guid.Empty
            ? await EvaluateEntityAsync(context, "Character", id, applicationPermission, ct).ConfigureAwait(false)
            : CatalogueResourceAccess.NotFound;
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateQidAsync(
        HttpContext context,
        string qid,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(qid))
        {
            return CatalogueResourceAccess.NotFound;
        }

        using var connection = database.CreateConnection();
        var candidates = new List<(string EntityType, Guid EntityId)>();
        candidates.AddRange((await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM works WHERE wikidata_qid=@qid;", new { qid }, cancellationToken: ct)))
            .Select(id => ("Work", id)));
        candidates.AddRange((await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM collections WHERE wikidata_qid=@qid;", new { qid }, cancellationToken: ct)))
            .Select(id => ("Collection", id)));
        candidates.AddRange((await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM persons WHERE wikidata_qid=@qid;", new { qid }, cancellationToken: ct)))
            .Select(id => ("Person", id)));
        candidates.AddRange((await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM fictional_entities WHERE wikidata_qid=@qid OR fictional_universe_qid=@qid;",
            new { qid }, cancellationToken: ct)).ConfigureAwait(false))
            .Select(id => ("Character", id)));

        var found = false;
        foreach (var candidate in candidates.Distinct())
        {
            var result = await EvaluateEntityAsync(
                context, candidate.EntityType, candidate.EntityId, applicationPermission, ct).ConfigureAwait(false);
            if (result == CatalogueResourceAccess.Allowed)
            {
                return result;
            }

            found |= result == CatalogueResourceAccess.Denied;
        }
        return found ? CatalogueResourceAccess.Denied : CatalogueResourceAccess.NotFound;
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateEntityAssetContainerAsync(
        HttpContext context,
        Guid entityId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        using var connection = database.CreateConnection();
        var entityTypes = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT entity_type FROM entity_assets WHERE entity_id=@entityId;",
            new { entityId },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        if (entityTypes.Count == 0)
        {
            return CatalogueResourceAccess.NotFound;
        }

        foreach (var entityType in entityTypes)
        {
            if (await EvaluateEntityAsync(
                    context, entityType, entityId, applicationPermission, ct).ConfigureAwait(false) ==
                CatalogueResourceAccess.Allowed)
            {
                return CatalogueResourceAccess.Allowed;
            }
        }
        return CatalogueResourceAccess.Denied;
    }

    public async ValueTask<CatalogueResourceAccess> EvaluateAssetAsync(
        RequestAuthority authority,
        Guid assetId,
        ApplicationPermissionId applicationPermission,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty)
        {
            return CatalogueResourceAccess.NotFound;
        }

        using var connection = database.CreateConnection();
        var resource = await connection.QuerySingleOrDefaultAsync<AssetResource>(new CommandDefinition(
            """
            SELECT ma.library_id AS LibraryId, w.media_type AS MediaType
            FROM media_assets ma
            JOIN editions e ON e.id=ma.edition_id
            JOIN works w ON w.id=e.work_id
            WHERE ma.id=@assetId;
            """,
            new { assetId },
            cancellationToken: ct)).ConfigureAwait(false);
        if (resource is null)
        {
            return CatalogueResourceAccess.NotFound;
        }

        if (!Guid.TryParse(resource.LibraryId, out var libraryId) || libraryId == Guid.Empty)
        {
            return CatalogueResourceAccess.Denied;
        }

        if (AuthorityValidity.Validate(authority) is not null)
        {
            return CatalogueResourceAccess.Denied;
        }

        var resourceContext = new ResourceAuthorizationContext(
            "catalogue-asset", assetId.ToString("D"), LibraryId: libraryId);
        if (authority.PrincipalKind is PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication)
        {
            var application = await evaluator.EvaluateAsync(
                authority,
                new AuthorizationRequirement(ApplicationPermission: applicationPermission),
                resourceContext,
                ct).ConfigureAwait(false);
            if (!application.IsAllowed)
            {
                return CatalogueResourceAccess.Denied;
            }
        }

        if (authority.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            return CatalogueResourceAccess.Allowed;
        }

        if (authority.PrincipalKind is not (PrincipalKind.Human or PrincipalKind.DelegatedUserClient))
        {
            return CatalogueResourceAccess.Denied;
        }

        var feature = FeatureFor(resource.MediaType);
        if (feature is null || !(await accounts.EvaluateFeatureAsync(
                authority, feature.Value, ct).ConfigureAwait(false)).IsAllowed)
        {
            return CatalogueResourceAccess.Denied;
        }

        return (await accounts.EvaluateLibraryAsync(authority, libraryId, ct).ConfigureAwait(false)).IsAllowed
            ? CatalogueResourceAccess.Allowed
            : CatalogueResourceAccess.Denied;
    }

    private static AccountFeatureId? FeatureFor(string mediaType) =>
        DisplayMediaRules.IsReadKind(mediaType) ? AccountFeatureId.Read :
        DisplayMediaRules.IsWatchKind(mediaType) ? AccountFeatureId.Watch :
        DisplayMediaRules.IsListenKind(mediaType) ? AccountFeatureId.Listen :
        null;

    private async Task<IReadOnlyList<Guid>> GetEntityAssetCandidatesAsync(
        string entityType,
        Guid entityId,
        CancellationToken ct)
    {
        var normalized = entityType.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim();
        using var connection = database.CreateConnection();
        entityId = WorkRedirects.Resolve(connection, entityId);
        var sql = normalized.ToLowerInvariant() switch
        {
            "work" or "movie" or "book" or "audiobook" or "comicissue" or "tvepisode" =>
                """
                WITH RECURSIVE work_tree(id) AS (
                    SELECT @entityId
                    UNION ALL
                    SELECT child.id
                    FROM works child
                    JOIN work_tree parent ON child.parent_work_id=parent.id
                )
                SELECT ma.id
                FROM work_tree member
                JOIN editions e ON e.work_id=member.id
                JOIN media_assets ma ON ma.edition_id=e.id
                WHERE ma.status='Normal' AND ma.is_orphaned=0
                ORDER BY ma.id;
                """,
            "edition" =>
                """
                SELECT ma.id
                FROM media_assets ma
                WHERE ma.edition_id=@entityId AND ma.status='Normal' AND ma.is_orphaned=0
                ORDER BY ma.id;
                """,
            "person" =>
                """
                SELECT DISTINCT ma.id
                FROM primary_person_media_credits pml
                JOIN media_assets ma ON ma.id=pml.media_asset_id
                WHERE pml.person_id=@entityId AND ma.status='Normal' AND ma.is_orphaned=0
                ORDER BY ma.id;
                """,
            "character" or "fictionalentity" =>
                """
                SELECT DISTINCT ma.id
                FROM fictional_entity_work_links link
                JOIN works w ON w.wikidata_qid=link.work_qid
                    OR EXISTS (
                        SELECT 1 FROM canonical_values cv
                        WHERE cv.entity_id=w.id AND cv.key='wikidata_qid' AND cv.value=link.work_qid)
                JOIN editions e ON e.work_id=w.id
                JOIN media_assets ma ON ma.edition_id=e.id
                WHERE link.entity_id=@entityId AND ma.status='Normal' AND ma.is_orphaned=0
                ORDER BY ma.id;
                """,
            "collection" or "universe" or "movieseries" or "bookseries" or "comicseries"
                or "musicalbum" or "tvshow" or "tvseason" =>
                """
                WITH RECURSIVE collection_tree(id) AS (
                    SELECT @entityId
                    UNION ALL
                    SELECT c.id FROM collections c JOIN collection_tree parent ON c.parent_collection_id=parent.id
                ),
                work_tree(id) AS (
                    SELECT @entityId
                    UNION ALL
                    SELECT w.id FROM works w JOIN work_tree parent ON w.parent_work_id=parent.id
                ),
                member_works(id) AS (
                    SELECT w.id FROM works w WHERE w.collection_id IN (SELECT id FROM collection_tree)
                    UNION
                    SELECT ci.work_id FROM collection_items ci WHERE ci.collection_id IN (SELECT id FROM collection_tree)
                    UNION
                    SELECT id FROM work_tree
                )
                SELECT DISTINCT ma.id
                FROM member_works member
                JOIN editions e ON e.work_id=member.id
                JOIN media_assets ma ON ma.edition_id=e.id
                WHERE ma.status='Normal' AND ma.is_orphaned=0
                ORDER BY ma.id;
                """,
            _ => string.Empty,
        };
        if (sql.Length == 0)
        {
            return [];
        }

        var candidates = (await connection.QueryAsync<Guid>(new CommandDefinition(
            sql,
            new { entityId },
            cancellationToken: ct)).ConfigureAwait(false)).AsList();

        if (!IsCollectionEntityType(normalized))
        {
            return candidates;
        }

        var dynamicCandidates = await GetDynamicCollectionAssetCandidatesAsync(
            connection,
            entityId,
            ct).ConfigureAwait(false);
        return candidates
            .Concat(dynamicCandidates)
            .Distinct()
            .ToList();
    }

    private async Task<IReadOnlyList<Guid>> GetDynamicCollectionAssetCandidatesAsync(
        System.Data.IDbConnection connection,
        Guid collectionId,
        CancellationToken ct)
    {
        var collection = await connection.QuerySingleOrDefaultAsync<DynamicCollectionRule>(new CommandDefinition(
            """
            SELECT rule_json AS RuleJson,
                   sort_field AS SortField,
                   sort_direction AS SortDirection,
                   secondary_sort_field AS SecondarySortField,
                   secondary_sort_direction AS SecondarySortDirection
            FROM collections
            WHERE id=@collectionId
              AND resolution='query'
              AND is_enabled=1
              AND rule_json IS NOT NULL
              AND TRIM(rule_json) <> ''
            LIMIT 1;
            """,
            new { collectionId },
            cancellationToken: ct)).ConfigureAwait(false);
        if (collection is null)
        {
            return [];
        }

        IReadOnlyList<Guid> workIds;
        try
        {
            var definition = CollectionRuleEvaluator.ParseDefinition(collection.RuleJson);
            if (definition.AllConditions.Count == 0)
            {
                return [];
            }

            workIds = new CollectionRuleEvaluator(database).Evaluate(
                definition,
                collection.SortField,
                string.IsNullOrWhiteSpace(collection.SortDirection) ? "desc" : collection.SortDirection,
                secondarySortField: collection.SecondarySortField,
                secondarySortDirection: collection.SecondarySortDirection);
        }
        catch (FormatException)
        {
            // Unsupported persisted rules must fail closed instead of making the
            // authorization filter crash or granting access without a proven asset.
            return [];
        }

        if (workIds.Count == 0)
        {
            return [];
        }

        var assetIds = new List<Guid>();
        foreach (var chunk in workIds.Distinct().Chunk(400))
        {
            var resolved = await connection.QueryAsync<Guid>(new CommandDefinition(
                """
                WITH RECURSIVE work_tree(id) AS (
                    SELECT id FROM works WHERE id IN @workIds
                    UNION
                    SELECT child.id
                    FROM works child
                    JOIN work_tree parent ON child.parent_work_id=parent.id
                )
                SELECT DISTINCT ma.id
                FROM work_tree member
                JOIN editions e ON e.work_id=member.id
                JOIN media_assets ma ON ma.edition_id=e.id
                WHERE ma.status='Normal' AND ma.is_orphaned=0
                ORDER BY ma.id;
                """,
                new { workIds = chunk.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct)).ConfigureAwait(false);
            assetIds.AddRange(resolved);
        }

        return assetIds.Distinct().ToList();
    }

    private static bool IsCollectionEntityType(string normalized) => normalized.ToLowerInvariant() is
        "collection" or "universe" or "movieseries" or "bookseries" or "comicseries"
        or "musicalbum" or "tvshow" or "tvseason";

    private sealed class DynamicCollectionRule
    {
        public string RuleJson { get; init; } = string.Empty;
        public string? SortField { get; init; }
        public string SortDirection { get; init; } = "desc";
        public string? SecondarySortField { get; init; }
        public string? SecondarySortDirection { get; init; }
    }

    private sealed class AssetResource
    {
        public string? LibraryId { get; init; }
        public string MediaType { get; init; } = string.Empty;
    }

    private sealed class ArtworkOwner
    {
        public string? EntityId { get; init; }
        public string EntityType { get; init; } = string.Empty;
    }
}

public sealed record CatalogueAssetAccessMetadata(string Permission);
public sealed record CatalogueArtworkAccessMetadata(string Permission);
public sealed record CatalogueEntityAccessMetadata(string Permission);
public sealed record CatalogueAnyEntityAccessMetadata(string Permission);
public sealed record CatalogueQidAccessMetadata(string Permission);
public sealed record CatalogueCharacterPortraitAccessMetadata(string Permission);
public sealed record CatalogueEntityAssetContainerAccessMetadata(string Permission);
public sealed record ProfileOperationAccessMetadata(string Permission);

internal sealed class CatalogueAssetAccessFilter(ApplicationPermissionId permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var raw = Convert.ToString(context.HttpContext.Request.RouteValues["assetId"],
            System.Globalization.CultureInfo.InvariantCulture);
        if (!Guid.TryParse(raw, out var assetId))
        {
            return Results.NotFound();
        }

        var service = context.HttpContext.RequestServices
            .GetRequiredService<CatalogueResourceAuthorizationService>();
        var access = await service.EvaluateAssetAsync(
            context.HttpContext, assetId, permission, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (access == CatalogueResourceAccess.Allowed && await IngestionEditGuard.IsLockedAsync(context.HttpContext, permission, assetId))
        {
            return IngestionEditGuard.Conflict();
        }
        return access switch
        {
            CatalogueResourceAccess.Allowed => await next(context),
            CatalogueResourceAccess.NotFound => Results.NotFound(),
            _ => Results.Forbid(),
        };
    }
}

internal sealed class CatalogueArtworkAccessFilter(ApplicationPermissionId permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var raw = Convert.ToString(context.HttpContext.Request.RouteValues["variantId"],
            System.Globalization.CultureInfo.InvariantCulture);
        if (!Guid.TryParse(raw, out var variantId))
        {
            return Results.NotFound();
        }

        var service = context.HttpContext.RequestServices
            .GetRequiredService<CatalogueResourceAuthorizationService>();
        return await service.EvaluateArtworkVariantAsync(
            context.HttpContext, variantId, permission, context.HttpContext.RequestAborted).ConfigureAwait(false) switch
        {
            CatalogueResourceAccess.Allowed => await next(context),
            _ => Results.NotFound(),
        };
    }
}

internal sealed class CatalogueEntityAccessFilter(
    ApplicationPermissionId permission,
    string idRouteValue,
    string? fixedEntityType = null) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var rawId = Convert.ToString(context.HttpContext.Request.RouteValues[idRouteValue],
            System.Globalization.CultureInfo.InvariantCulture);
        var entityType = fixedEntityType ?? Convert.ToString(
            context.HttpContext.Request.RouteValues["entityType"],
            System.Globalization.CultureInfo.InvariantCulture);
        if (!Guid.TryParse(rawId, out var entityId) || string.IsNullOrWhiteSpace(entityType))
        {
            return Results.NotFound();
        }

        var service = context.HttpContext.RequestServices
            .GetRequiredService<CatalogueResourceAuthorizationService>();
        var access = await service.EvaluateEntityAsync(
            context.HttpContext, entityType, entityId, permission, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (access == CatalogueResourceAccess.Allowed && await IngestionEditGuard.IsLockedAsync(context.HttpContext, permission, entityId))
        {
            return IngestionEditGuard.Conflict();
        }
        return access switch
        {
            CatalogueResourceAccess.Allowed => await next(context),
            _ => Results.NotFound(),
        };
    }
}

internal sealed class ProfileOperationAccessFilter(ApplicationPermissionId permission) : IEndpointFilter
{
    internal const string ActiveProfileItemKey = "tuvima.authorized-active-profile";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var resolver = context.HttpContext.RequestServices.GetRequiredService<IRequestAuthorityResolver>();
        var authority = await resolver.ResolveAsync(
            context.HttpContext, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (AuthorityValidity.ValidateHuman(authority) is not null ||
            authority.ActiveProfileId is not { } profileId)
        {
            return Results.Forbid();
        }

        if (authority.PrincipalKind == PrincipalKind.DelegatedUserClient)
        {
            var evaluator = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationEvaluator>();
            if (!(await evaluator.EvaluateAsync(
                    authority,
                    new AuthorizationRequirement(ApplicationPermission: permission, RequiresHumanContext: true),
                    null,
                    context.HttpContext.RequestAborted).ConfigureAwait(false)).IsAllowed)
            {
                return Results.Forbid();
            }
        }
        else if (authority.PrincipalKind != PrincipalKind.Human)
        {
            return Results.Forbid();
        }

        context.HttpContext.Items[ActiveProfileItemKey] = profileId;
        return await next(context);
    }
}

internal sealed class CatalogueAnyEntityAccessFilter(
    ApplicationPermissionId permission,
    string idRouteValue,
    bool artworkLink = false) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var rawId = Convert.ToString(context.HttpContext.Request.RouteValues[idRouteValue],
            System.Globalization.CultureInfo.InvariantCulture);
        if (!Guid.TryParse(rawId, out var entityId))
        {
            return Results.NotFound();
        }

        var service = context.HttpContext.RequestServices
            .GetRequiredService<CatalogueResourceAuthorizationService>();
        var access = artworkLink
            ? await service.EvaluateArtworkLinkAsync(context.HttpContext, entityId, permission, context.HttpContext.RequestAborted).ConfigureAwait(false)
            : await service.EvaluateAnyEntityAsync(context.HttpContext, entityId, permission, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (access == CatalogueResourceAccess.Allowed && await IngestionEditGuard.IsLockedAsync(context.HttpContext, permission, entityId))
        {
            return IngestionEditGuard.Conflict();
        }
        return access switch
        {
            CatalogueResourceAccess.Allowed => await next(context),
            _ => Results.NotFound(),
        };
    }
}

internal sealed class CatalogueQidAccessFilter(
    ApplicationPermissionId permission,
    string idRouteValue) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var qid = Convert.ToString(context.HttpContext.Request.RouteValues[idRouteValue],
            System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(qid))
        {
            return Results.NotFound();
        }

        var service = context.HttpContext.RequestServices
            .GetRequiredService<CatalogueResourceAuthorizationService>();
        return await service.EvaluateQidAsync(
            context.HttpContext, qid, permission, context.HttpContext.RequestAborted).ConfigureAwait(false) switch
        {
            CatalogueResourceAccess.Allowed => await next(context),
            _ => Results.NotFound(),
        };
    }
}

internal sealed class CatalogueCharacterPortraitAccessFilter(ApplicationPermissionId permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var rawId = Convert.ToString(context.HttpContext.Request.RouteValues["portraitId"],
            System.Globalization.CultureInfo.InvariantCulture);
        if (!Guid.TryParse(rawId, out var portraitId))
        {
            return Results.NotFound();
        }

        var service = context.HttpContext.RequestServices
            .GetRequiredService<CatalogueResourceAuthorizationService>();
        return await service.EvaluateCharacterPortraitAsync(
            context.HttpContext, portraitId, permission, context.HttpContext.RequestAborted).ConfigureAwait(false) switch
        {
            CatalogueResourceAccess.Allowed => await next(context),
            _ => Results.NotFound(),
        };
    }
}

internal sealed class CatalogueEntityAssetContainerAccessFilter(ApplicationPermissionId permission) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var rawId = Convert.ToString(context.HttpContext.Request.RouteValues["entityId"],
            System.Globalization.CultureInfo.InvariantCulture);
        if (!Guid.TryParse(rawId, out var entityId))
        {
            return Results.NotFound();
        }

        var service = context.HttpContext.RequestServices
            .GetRequiredService<CatalogueResourceAuthorizationService>();
        return await service.EvaluateEntityAssetContainerAsync(
            context.HttpContext, entityId, permission, context.HttpContext.RequestAborted).ConfigureAwait(false) switch
        {
            CatalogueResourceAccess.Allowed => await next(context),
            _ => Results.NotFound(),
        };
    }
}

internal static class IngestionEditGuard
{
    public static Task<bool> IsLockedAsync(HttpContext context, ApplicationPermissionId permission, Guid entityId) =>
        !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
        && (permission == ApplicationPermissionIds.MetadataWrite || permission == ApplicationPermissionIds.ReviewResolve
            || permission == ApplicationPermissionIds.MetadataMatch || permission == ApplicationPermissionIds.MetadataEnrichmentRun)
            ? IngestionAvailability.IsUpdatingAsync(context.RequestServices.GetRequiredService<IDatabaseConnection>(), entityId, context.RequestAborted)
            : Task.FromResult(false);

    public static IResult Conflict() => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Updating details",
        detail: "This item is still being ingested. Editing will become available when its required work finishes.");
}

public static class CatalogueResourceEndpointExtensions
{
    public static RouteHandlerBuilder RequireArtworkLinkAccess(
        this RouteHandlerBuilder builder, ApplicationPermissionId permission) =>
        builder.AddEndpointFilter(new CatalogueAnyEntityAccessFilter(permission, "linkId", artworkLink: true))
            .WithMetadata(new CatalogueAnyEntityAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireCatalogueAssetAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.AddEndpointFilter(new CatalogueAssetAccessFilter(permission))
            .WithMetadata(new CatalogueAssetAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireCatalogueArtworkAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.AddEndpointFilter(new CatalogueArtworkAccessFilter(permission))
            .WithMetadata(new CatalogueArtworkAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireCatalogueEntityAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission,
        string idRouteValue = "entityId") =>
        builder.AddEndpointFilter(new CatalogueEntityAccessFilter(permission, idRouteValue))
            .WithMetadata(new CatalogueEntityAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireAnyCatalogueEntityAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission,
        string idRouteValue = "entityId") =>
        builder.AddEndpointFilter(new CatalogueAnyEntityAccessFilter(permission, idRouteValue))
            .WithMetadata(new CatalogueAnyEntityAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireCatalogueQidAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission,
        string idRouteValue = "qid") =>
        builder.AddEndpointFilter(new CatalogueQidAccessFilter(permission, idRouteValue))
            .WithMetadata(new CatalogueQidAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireCatalogueCharacterPortraitAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.AddEndpointFilter(new CatalogueCharacterPortraitAccessFilter(permission))
            .WithMetadata(new CatalogueCharacterPortraitAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireCatalogueEntityAssetContainerAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.AddEndpointFilter(new CatalogueEntityAssetContainerAccessFilter(permission))
            .WithMetadata(new CatalogueEntityAssetContainerAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireCatalogueEntityAccess(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission,
        string entityType,
        string idRouteValue) =>
        builder.AddEndpointFilter(new CatalogueEntityAccessFilter(permission, idRouteValue, entityType))
            .WithMetadata(new CatalogueEntityAccessMetadata(permission.Value));

    public static RouteHandlerBuilder RequireProfileOperation(
        this RouteHandlerBuilder builder,
        ApplicationPermissionId permission) =>
        builder.RequireAuthorization(AuthPolicies.Authenticated)
            .AddEndpointFilter(new ProfileOperationAccessFilter(permission))
            .WithMetadata(new ProfileOperationAccessMetadata(permission.Value));
}
