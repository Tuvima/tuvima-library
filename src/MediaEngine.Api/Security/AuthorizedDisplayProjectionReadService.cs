using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
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
    IDatabaseConnection database,
    IConfigurationLoader? configuration = null,
    TimeProvider? clock = null) : IDisplayProjectionReadService
{
    // Built from the same data store connection, so every host that can authorize a catalogue request can also enforce the
    // limit and keep the Vault out of view.
    private ContentLimitPolicy? _contentLimits;

    private IUnmatchedAssetRepository? _unmatched;

    private IUnmatchedAssetRepository Unmatched => _unmatched ??= new UnmatchedAssetRepository(database);

    private ContentLimitPolicy ContentLimits => _contentLimits ??= new(
        new ProfileContentLimitRepository(database),
        new VaultAccessResolver(new VaultLibraryRepository(database), new NsfwAccessRepository(database), clock ?? TimeProvider.System),
        configuration);

    // An Unmatched file lives only under the Unmatched filter (Library setup spec D64): every shared list below leaves out a work whose
    // files in a library are all Unmatched, kept or not, and only the filter's own reads (the ...IncludingUnmatched ones) keep it. A parent
    // shared with matched files stays, because the leaf rows it is built from are still there.
    public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) =>
        await FilterWorksAsync(await inner.LoadWorksAsync(ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false), false, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorksIncludingUnmatchedAsync(CancellationToken ct) =>
        await FilterWorksAsync(await inner.LoadWorksAsync(ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false), true, ct).ConfigureAwait(false);

    public async Task<IReadOnlySet<Guid>> LoadVisibleWorkIdsAsync(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        return await AllowedWorkIdsAsync(scope, false, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<Guid>> LoadVisibleWorkIdsIncludingUnmatchedAsync(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        return await AllowedWorkIdsAsync(scope, true, ct).ConfigureAwait(false);
    }

    // The same rule LoadWorksAsync applies, over the light rows: an invalid scope sees nothing, otherwise a work is visible
    // when any of its (work, library) rows passes Allows.
    private async Task<IReadOnlySet<Guid>> AllowedWorkIdsAsync(CatalogueScope scope, bool includeUnmatched, CancellationToken ct)
    {
        if (!scope.IsValid)
        {
            return new HashSet<Guid>();
        }

        var visibleRows = await inner.LoadVisibleWorkRowsAsync(ct).ConfigureAwait(false);
        var vaultWorks = WorksInHiddenVault(visibleRows.Select(row => (row.WorkId, row.LibraryId)), scope);
        var allowed = visibleRows
            .Where(row => !vaultWorks.Contains(row.WorkId) && Allows(row.LibraryId, row.MediaType, row.ContentRating, scope))
            .ToList();
        allowed = await WithoutUnmatchedOnlyAsync(allowed, row => row.WorkId, row => row.LibraryId, includeUnmatched, ct).ConfigureAwait(false);
        return allowed.Select(row => row.WorkId).ToHashSet();
    }

    /// <summary>
    /// Drops the rows whose work has only Unmatched files in the row's library (kept or not), so an Unmatched file never reaches a lane,
    /// Home, a shelf, a collection, a people page or a normal search. Only the works in view are looked up, and nothing is read when
    /// nothing is Unmatched.
    /// </summary>
    private async Task<List<T>> WithoutUnmatchedOnlyAsync<T>(
        List<T> rows, Func<T, Guid> workId, Func<T, string?> libraryId, bool includeUnmatched, CancellationToken ct)
    {
        if (includeUnmatched || rows.Count == 0)
        {
            return rows;
        }

        var unmatchedOnly = (await Unmatched.ListWorkLibrariesAsync(rows.Select(workId).Distinct().ToList(), ct).ConfigureAwait(false))
            .Where(entry => entry.EveryFileUnmatched)
            .Select(entry => (entry.WorkId, entry.LibraryId))
            .ToHashSet();
        return unmatchedOnly.Count == 0
            ? rows
            : rows.Where(row => !unmatchedOnly.Contains((workId(row), (libraryId(row) ?? string.Empty).ToUpperInvariant()))).ToList();
    }

    public async Task<DisplayLibraryScope> LoadLibraryScopeAsync(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        return scope.IsValid ? new DisplayLibraryScope(scope.AllLibraries, scope.Libraries) : new DisplayLibraryScope(false, new HashSet<Guid>());
    }

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorkLibraryRowsAsync(CancellationToken ct) =>
        await AllowedWorkLibraryRowsAsync(await inner.LoadWorksAsync(ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false), false, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorkLibraryRowsIncludingUnmatchedAsync(CancellationToken ct) =>
        await AllowedWorkLibraryRowsAsync(await inner.LoadWorksAsync(ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false), true, ct).ConfigureAwait(false);

    public async Task<DisplayLibraryAccess> LoadLibraryAccessAsync(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        return scope.IsValid
            ? new DisplayLibraryAccess((libraryId, mediaType) =>
                !scope.Content.IsVaultLibrary(libraryId) && AllowsLibraryFile(libraryId, mediaType, scope))
            : DisplayLibraryAccess.Nothing;
    }

    // A work's own detail page keeps its Unmatched files (they play from there, D64); a person's or any other page leaves them out.
    public async Task<IReadOnlyList<DisplayWorkRow>> LoadDetailWorksAsync(Guid id, CancellationToken ct) =>
        await FilterWorksAsync(
            await inner.LoadDetailWorksAsync(id, ct).ConfigureAwait(false),
            await ResolveScopeAsync(ct).ConfigureAwait(false),
            IsWork(id),
            ct).ConfigureAwait(false);

    private bool IsWork(Guid id)
    {
        using var connection = database.CreateConnection();
        return connection.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM works WHERE id = @id);", new { id }) != 0;
    }

    internal async Task<IReadOnlyList<DisplayWorkRow>> LoadAuthorizedAssetsAsync(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (!scope.IsValid)
        {
            return [];
        }

        // The file itself must be visible (checked on the joined asset below), not just the work's own files:
        // an episode covered by a combined file has no file of its own but is still playable through the host file.
        // Readiness (matched, placed, artwork, curator, review) is the stored mark; the joined file row is checked here.
        var visibleWorkPredicate =
            WorkReadinessSql.ReadyMarkPredicate("w") + " AND COALESCE(w.is_catalog_only, 0) = 0 "
            + "AND ma.status = 'Normal' AND ma.is_orphaned = 0";
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        // A rating is only read for a profile that has a limit; everyone else pays nothing.
        var ratingSql = scope.Content.IsUnrestricted
            ? "NULL"
            : ContentRatingSql.Expression("w.id", "COALESCE(gw.id, pw.id, w.id)", "ma.id");
        using var connection = database.CreateConnection();
        var rows = (await connection.QueryAsync<DisplayWorkRow>(new CommandDefinition(
            $"""
            SELECT ma.library_id AS LibraryId,
                   w.id AS WorkId,
                   w.collection_id AS CollectionId,
                   w.media_type AS MediaType,
                   w.work_kind AS WorkKind,
                   ma.id AS AssetId,
                   {ratingSql} AS ContentRating,
                   COALESCE(ma.presented_at, CURRENT_TIMESTAMP) AS CreatedAt
            FROM works w
            JOIN work_owned_assets woa ON woa.work_id=w.id
            JOIN media_assets ma ON ma.id=woa.asset_id
            LEFT JOIN works pw ON pw.id=w.parent_work_id
            LEFT JOIN works gw ON gw.id=pw.parent_work_id
            WHERE w.work_kind <> 'parent'
              AND {visibleWorkPredicate}
              AND {visibleAssetPredicate}
            ORDER BY ma.presented_at DESC, ma.id, woa.is_covered;
            """,
            cancellationToken: ct)).ConfigureAwait(false)).AsList();
        return rows.Where(row => AllowsFile(row.LibraryId, row.MediaType, row.ContentRating, scope)).ToList();
    }

    internal async Task<IReadOnlyList<DisplayWorkRow>> FilterRecentWorksAsync(IReadOnlyList<DisplayWorkRow> rows, Guid? profileId, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (!scope.IsValid || profileId is null || profileId != scope.Authority.ActiveProfileId)
        {
            return [];
        }
        var vaultWorks = WorksInHiddenVault(rows.Select(row => (row.WorkId, row.LibraryId)), scope);
        var allowed = rows.Where(row => !vaultWorks.Contains(row.WorkId) && Allows(row.LibraryId, row.MediaType, row.ContentRating, scope)).ToList();
        return await WithoutUnmatchedOnlyAsync(allowed, row => row.WorkId, row => row.LibraryId, false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Narrows a light candidate list to what the profile's libraries and features allow. Content ratings are
    /// only known on full rows, so they are checked later; this never drops something the full check would keep.
    /// </summary>
    internal async Task<IReadOnlyList<RecentCandidate>> FilterRecentCandidatesAsync(IReadOnlyList<RecentCandidate> candidates, Guid? profileId, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (!scope.IsValid || profileId is null || profileId != scope.Authority.ActiveProfileId)
        {
            return [];
        }
        var allowed = candidates
            .Where(candidate => !scope.Content.IsVaultLibrary(candidate.LibraryId) && AllowsLibraryAndFeature(candidate.LibraryId, candidate.MediaType, scope))
            .ToList();
        return await WithoutUnmatchedOnlyAsync(allowed, candidate => candidate.WorkId, candidate => candidate.LibraryId, false, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) =>
        await FilterWorksAsync(await inner.LoadHomeWorksAsync(ct).ConfigureAwait(false), await ResolveScopeAsync(ct).ConfigureAwait(false), false, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (!scope.IsValid || profileId is null || profileId != scope.Authority.ActiveProfileId)
        {
            return [];
        }

        var allowed = (await inner.LoadJourneyAsync(profileId, lane, ct).ConfigureAwait(false))
            .Where(row => row.ProfileId == profileId && Allows(row.LibraryId, row.MediaType, row.ContentRating, scope))
            .ToList();
        // Continue and journey lanes hold identified titles only; the state read below stays whole so a card in the Unmatched filter keeps its progress.
        return await WithoutUnmatchedOnlyAsync(allowed, row => row.WorkId, row => row.LibraryId, false, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (!scope.IsValid || profileId is null || profileId != scope.Authority.ActiveProfileId)
        {
            return [];
        }
        return (await inner.LoadStatesAsync(profileId, lane, ct).ConfigureAwait(false))
            .Where(row => row.ProfileId == profileId && Allows(row.LibraryId, row.MediaType, row.ContentRating, scope)).ToList();
    }

    public async Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (profileId is null || profileId != scope.Authority.ActiveProfileId || !scope.IsValid)
        {
            return new HashSet<Guid>();
        }

        var favorites = await inner.LoadFavoriteWorkIdsAsync(profileId, ct).ConfigureAwait(false);
        if (scope.Content.Vault.VaultLibraryIds.Count == 0)
        {
            // A favourite whose files are all Unmatched is not part of the lanes either (D64): only those need the visible-work check.
            var unmatchedOnly = (await Unmatched.ListWorkLibrariesAsync(favorites, ct).ConfigureAwait(false))
                .Where(entry => entry.EveryFileUnmatched)
                .Select(entry => entry.WorkId)
                .ToHashSet();
            if (unmatchedOnly.Count == 0)
            {
                return favorites;
            }

            var shown = await AllowedWorkIdsAsync(scope, false, ct).ConfigureAwait(false);
            return favorites.Where(id => !unmatchedOnly.Contains(id) || shown.Contains(id)).ToHashSet();
        }

        // A favourite that is a Vault item is not part of the shared catalogue, locked or not (Videos spec D27, D45).
        var visible = await AllowedWorkIdsAsync(scope, false, ct).ConfigureAwait(false);
        return favorites.Where(visible.Contains).ToHashSet();
    }

    public async Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct).ConfigureAwait(false);
        if (profileId is null || profileId != scope.Authority.ActiveProfileId || !scope.IsValid)
        {
            return [];
        }

        var allowedWorkIds = await AllowedWorkIdsAsync(scope, false, ct).ConfigureAwait(false);
        if (scope.AllLibraries && scope.Content.NeedsNoFiltering && scope.Content.Vault.VaultLibraryIds.Count == 0
            && allowedWorkIds.Count == (await inner.LoadVisibleWorkIdsAsync(ct).ConfigureAwait(false)).Count)
        {
            // Nothing is narrowed: no limit, no Vault, and no work left out as Unmatched.
            return await inner.LoadHomeCollectionsAsync(profileId, ct).ConfigureAwait(false);
        }

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
            return new(authority, false, false, new HashSet<Guid>(), new HashSet<AccountFeatureId>(), ContentLimitFilter.Unrestricted);
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
                return new(authority, false, false, new HashSet<Guid>(), new HashSet<AccountFeatureId>(), ContentLimitFilter.Unrestricted);
            }
        }

        var content = await ContentLimits.ForAsync(authority).ConfigureAwait(false);
        if (authority.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            return new(authority, true, true, new HashSet<Guid>(), AccountFeatureId.All.ToHashSet(), content);
        }

        if (authority.PrincipalKind is not (PrincipalKind.Human or PrincipalKind.DelegatedUserClient))
        {
            return new(authority, false, false, new HashSet<Guid>(), new HashSet<AccountFeatureId>(), ContentLimitFilter.Unrestricted);
        }

        if (authority.IsEffectiveAdministrator)
        {
            return new(authority, true, true, new HashSet<Guid>(), AccountFeatureId.All.ToHashSet(), content);
        }

        var features = await accounts.GetFeatureGrantsAsync(authority.AccountId!.Value, ct).ConfigureAwait(false);
        var libraries = await accounts.GetLibraryGrantsAsync(authority.AccountId.Value, ct).ConfigureAwait(false);
        return new(authority, true, false, libraries, features, content);
    }

    private async Task<IReadOnlyList<DisplayWorkRow>> FilterWorksAsync(
        IReadOnlyList<DisplayWorkRow> rows,
        CatalogueScope scope,
        bool includeUnmatched,
        CancellationToken ct)
    {
        return (await AllowedWorkLibraryRowsAsync(rows, scope, includeUnmatched, ct).ConfigureAwait(false))
            .GroupBy(row => row.WorkId)
            .Select(group => group
                .OrderBy(row => row.CreatedAt)
                .ThenBy(row => row.AssetId)
                .First())
            .OrderByDescending(row => row.CreatedAt)
            .ToList();
    }

    // The one allow rule for works, applied per (work, library) row; FilterWorksAsync then keeps one row per work.
    private async Task<IReadOnlyList<DisplayWorkRow>> AllowedWorkLibraryRowsAsync(
        IReadOnlyList<DisplayWorkRow> rows, CatalogueScope scope, bool includeUnmatched, CancellationToken ct)
    {
        if (!scope.IsValid)
        {
            return [];
        }

        var vaultWorks = WorksInHiddenVault(rows.Select(row => (row.WorkId, row.LibraryId)), scope);
        var allowed = rows.Where(row => !vaultWorks.Contains(row.WorkId) && Allows(row.LibraryId, row.MediaType, row.ContentRating, scope)).ToList();
        return await WithoutUnmatchedOnlyAsync(allowed, row => row.WorkId, row => row.LibraryId, includeUnmatched, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The rule for every shared list (works, Home, search, collections, recent, journeys): a Vault item is never part of it, not even
    /// for an unlocked session, because the Vault area is its own place with its own routes (Videos spec D20, D27, D45).
    /// </summary>
    private static bool Allows(string? libraryId, string mediaType, string? contentRating, CatalogueScope scope) =>
        !scope.Content.IsVaultLibrary(libraryId) && AllowsFile(libraryId, mediaType, contentRating, scope);

    /// <summary>
    /// The rule for one file the request may open (the player's catalogue): a Vault file is allowed for an unlocked session with the
    /// Watch feature, so a person can play what they find in the Vault area.
    /// </summary>
    private static bool AllowsFile(string? libraryId, string mediaType, string? contentRating, CatalogueScope scope) =>
        scope.Content.Allows(contentRating) && AllowsLibraryFile(libraryId, mediaType, scope);

    /// <summary>The part of <see cref="AllowsFile"/> that depends on the library, not the file's rating: also what decides whether a library may be listed.</summary>
    private static bool AllowsLibraryFile(string? libraryId, string mediaType, CatalogueScope scope) =>
        !scope.Content.HidesLibrary(libraryId)
        && scope.Content.AllowsVideo(libraryId, mediaType)
        && AllowsLibraryAndFeature(libraryId, mediaType, scope);

    /// <summary>
    /// The works that have at least one file in a Vault library. Such a work is absent from every shared list as a whole, so
    /// a work that somehow spans a Vault library and a normal one still cannot show its Vault side's title there.
    /// </summary>
    private static HashSet<Guid> WorksInHiddenVault(IEnumerable<(Guid WorkId, string? LibraryId)> rows, CatalogueScope scope) =>
        scope.Content.Vault.VaultLibraryIds.Count > 0
            ? rows.Where(row => scope.Content.IsVaultLibrary(row.LibraryId)).Select(row => row.WorkId).ToHashSet()
            : [];

    private static bool AllowsLibraryAndFeature(string? libraryId, string mediaType, CatalogueScope scope)
    {
        if (scope.Content.IsVaultLibrary(libraryId))
        {
            // Vault access is the device session's Vault mode, not an account's library grants; the Watch feature still applies.
            return scope.Content.Vault.Unlocked && scope.Features.Contains(AccountFeatureId.Watch);
        }

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
        IReadOnlySet<AccountFeatureId> Features,
        ContentLimitFilter Content)
    {
        public static readonly CatalogueScope Denied = new(
            new RequestAuthority(PrincipalKind.Anonymous, false),
            false,
            false,
            new HashSet<Guid>(),
            new HashSet<AccountFeatureId>(), ContentLimitFilter.Unrestricted);
    }
}
