using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// Applies the current live account, grant, application, feature, library, and
/// profile intersection to cached display projections before composition.
/// </summary>
internal sealed class AuthorizedDisplayProjectionReadService(
    IRawDisplayProjectionReadService inner,
    IHttpContextAccessor http,
    IRequestAuthorityResolver authorities,
    IAccountRepository accounts,
    IAuthorizationEvaluator evaluator,
    IDatabaseConnection database) : IDisplayProjectionReadService
{
    public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) =>
        FilterWorks(await inner.LoadWorksAsync(ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadDetailWorksAsync(Guid id, CancellationToken ct) =>
        FilterWorks(await inner.LoadDetailWorksAsync(id, ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false));

    internal async Task<IReadOnlyList<DisplayWorkRow>> LoadAuthorizedAssetsAsync(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (!scope.IsValid)
        {
            return [];
        }

        var visibleWorkPredicate = HomeVisibilitySql.VisibleWorkPredicate(
            "w.id", "w.curator_state", "w.is_catalog_only");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        using var connection = database.CreateConnection();
        var rows = (await connection.QueryAsync<DisplayWorkRow>(new CommandDefinition(
            $"""
            SELECT ma.library_id AS LibraryId,
                   w.id AS WorkId,
                   w.collection_id AS CollectionId,
                   w.media_type AS MediaType,
                   w.work_kind AS WorkKind,
                   ma.id AS AssetId,
                   COALESCE(ma.presented_at, CURRENT_TIMESTAMP) AS CreatedAt
            FROM works w
            JOIN editions e ON e.work_id=w.id
            JOIN media_assets ma ON ma.edition_id=e.id
            WHERE w.work_kind <> 'parent'
              AND {visibleWorkPredicate}
              AND {visibleAssetPredicate}
            ORDER BY ma.presented_at DESC, ma.id;
            """,
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        return rows.Where(row => Allows(row.LibraryId, row.MediaType, scope)).ToList();
    }

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) =>
        FilterWorks(await inner.LoadHomeWorksAsync(ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(string? lane, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (scope.Authority.ActiveProfileId is not { } profileId)
        {
            return [];
        }

        return (await inner.LoadJourneyAsync(lane, ct).ConfigureAwait(false))
            .Where(row => row.ProfileId == profileId && Allows(row.LibraryId, row.MediaType, scope))
            .ToList();
    }

    public async Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (profileId is null || profileId != scope.Authority.ActiveProfileId || !scope.IsValid)
        {
            return new HashSet<Guid>();
        }

        return await inner.LoadFavoriteWorkIdsAsync(profileId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (profileId is null || profileId != scope.Authority.ActiveProfileId || !scope.IsValid)
        {
            return [];
        }

        if (scope.AllLibraries)
        {
            return await inner.LoadHomeCollectionsAsync(profileId, ct).ConfigureAwait(false);
        }

        var allowedWorkIds = (await inner.LoadWorksAsync(ct).ConfigureAwait(false))
            .Where(work => Allows(work.LibraryId, work.MediaType, scope))
            .Select(work => work.WorkId)
            .ToHashSet();
        if (allowedWorkIds.Count == 0)
        {
            return [];
        }

        return await inner.LoadHomeCollectionsAsync(profileId, allowedWorkIds, ct).ConfigureAwait(false);
    }

    private async ValueTask<CatalogueScope> ResolveScopeAsync(CancellationToken ct)
    {
        if (http.HttpContext is not { } context)
        {
            return CatalogueScope.Denied;
        }

        var authority = await authorities.ResolveAsync(context, ct).ConfigureAwait(false);
        if (AuthorityValidity.Validate(authority) is not null)
        {
            return new(authority, false, false, new HashSet<Guid>(), new HashSet<AccountFeatureId>());
        }

        if (authority.PrincipalKind is PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication)
        {
            var permission = await evaluator.EvaluateAsync(
                authority,
                new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.LibraryRead),
                null,
                ct).ConfigureAwait(false);
            if (!permission.IsAllowed)
            {
                return new(authority, false, false, new HashSet<Guid>(), new HashSet<AccountFeatureId>());
            }
        }

        if (authority.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            return new(authority, true, true, new HashSet<Guid>(), AccountFeatureId.All.ToHashSet());
        }

        if (authority.PrincipalKind is not (PrincipalKind.Human or PrincipalKind.DelegatedUserClient))
        {
            return new(authority, false, false, new HashSet<Guid>(), new HashSet<AccountFeatureId>());
        }

        if (authority.IsEffectiveAdministrator)
        {
            return new(authority, true, true, new HashSet<Guid>(), AccountFeatureId.All.ToHashSet());
        }

        var features = await accounts.GetFeatureGrantsAsync(authority.AccountId!.Value, ct).ConfigureAwait(false);
        var libraries = await accounts.GetLibraryGrantsAsync(authority.AccountId.Value, ct).ConfigureAwait(false);
        return new(authority, true, false, libraries, features);
    }

    private static IReadOnlyList<DisplayWorkRow> FilterWorks(
        IReadOnlyList<DisplayWorkRow> rows,
        CatalogueScope scope) =>
        scope.IsValid
            ? rows.Where(row => Allows(row.LibraryId, row.MediaType, scope))
                .GroupBy(row => row.WorkId)
                .Select(group => group
                    .OrderBy(row => row.CreatedAt)
                    .ThenBy(row => row.AssetId)
                    .First())
                .OrderByDescending(row => row.CreatedAt)
                .ToList()
            : [];

    private static bool Allows(string? libraryId, string mediaType, CatalogueScope scope)
    {
        if (!scope.Features.Contains(FeatureFor(mediaType)))
        {
            return false;
        }

        if (!Guid.TryParse(libraryId, out var parsedLibraryId) || parsedLibraryId == Guid.Empty)
        {
            return false;
        }

        return scope.AllLibraries || scope.Libraries.Contains(parsedLibraryId);
    }

    private static AccountFeatureId FeatureFor(string mediaType) =>
        DisplayMediaRules.IsReadKind(mediaType) ? AccountFeatureId.Read :
        DisplayMediaRules.IsWatchKind(mediaType) ? AccountFeatureId.Watch :
        DisplayMediaRules.IsListenKind(mediaType) ? AccountFeatureId.Listen :
        // Unknown catalogue kinds do not acquire access from an unrelated feature.
        default;

    private sealed record CatalogueScope(
        RequestAuthority Authority,
        bool IsValid,
        bool AllLibraries,
        IReadOnlySet<Guid> Libraries,
        IReadOnlySet<AccountFeatureId> Features)
    {
        public static readonly CatalogueScope Denied = new(
            new RequestAuthority(PrincipalKind.Anonymous, false),
            false,
            false,
            new HashSet<Guid>(),
            new HashSet<AccountFeatureId>());
    }
}
