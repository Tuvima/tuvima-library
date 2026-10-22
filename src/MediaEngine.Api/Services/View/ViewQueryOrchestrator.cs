using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.View;

public sealed record ViewAssetQueryRequest(
    ViewScopeRequest Scope,
    int Limit = 120,
    string? Cursor = null,
    string? Search = null,
    IReadOnlyList<string>? MediaKinds = null,
    bool FavoritesOnly = false,
    bool IncludeHidden = false,
    bool HiddenOnly = false,
    Guid? GalleryId = null,
    LocalAssetLifecycleFilter Lifecycle = LocalAssetLifecycleFilter.Active,
    bool AllowStaleSelectionFallback = false,
    DateTimeOffset? AnchorBefore = null,
    bool WithoutLocation = false,
    string? PersonKey = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    bool SortByAddedAt = false,
    DateTimeOffset? AddedBefore = null,
    string? AddedAfterKey = null,
    LocalAssetDocumentFilter? DocumentFilter = null,
    IReadOnlyCollection<Guid>? AddedByProfileIds = null);

/// <summary>
/// Authorized persistence plan. Backends receive only library IDs approved by
/// the resolver and must apply the whole set in the same query.
/// </summary>
public sealed record ViewAssetQueryPlan(
    ResolvedViewScope Scope,
    int Limit,
    string? Cursor,
    string? Search,
    IReadOnlyList<string>? MediaKinds,
    bool FavoritesOnly,
    bool IncludeHidden,
    bool HiddenOnly,
    Guid? GalleryId,
    LocalAssetLifecycleFilter Lifecycle,
    CollectionRuleDefinition? SmartRule,
    bool TimelineEligibleOnly,
    bool IncludeSharedLibraryAssets,
    DateTimeOffset? AnchorBefore = null,
    bool WithoutLocation = false,
    string? PersonKey = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    bool SortByAddedAt = false,
    DateTimeOffset? AddedBefore = null,
    string? AddedAfterKey = null,
    LocalAssetDocumentFilter? DocumentFilter = null,
    IReadOnlyCollection<string>? ExcludedContentKinds = null,
    IReadOnlyCollection<Guid>? AddedByProfileIds = null);

public sealed record ViewAddedByResult(
    ViewAccessOutcome Outcome,
    IReadOnlyList<ViewAddedByDto>? Profiles = null);

public sealed record ViewQueryResult(
    ViewAccessOutcome Outcome,
    ViewAssetTimelinePageDto? Page = null,
    ResolvedViewScope? Scope = null);

public sealed record ViewTimelineIndexResult(
    ViewAccessOutcome Outcome,
    ViewTimelineIndexDto? Index = null,
    ResolvedViewScope? Scope = null);

/// <summary>
/// Authorized query boundary. Endpoint code cannot supply a profile or physical
/// library ID; both come from trusted context and scope resolution.
/// </summary>
public sealed class ViewQueryOrchestrator(
    IViewRequestProfileContext profileContext,
    IViewResourceAuthorizationService authorization,
    IViewAssetQueryBackend backend,
    IViewSmartGalleryQueryService? smartGalleries = null,
    IViewProfileRepository? profiles = null) : IViewQueryOrchestrator
{
    /// <summary>
    /// The View kinds the signed-in profile may not use. Every listing, search and count goes through this
    /// orchestrator, so a kind that is switched off is left out here once, for all of them.
    /// </summary>
    private async Task<IReadOnlyList<string>> BlockedKindsAsync(CancellationToken ct)
    {
        if (profiles is null
            || (await profileContext.ResolveAuthorityAsync(ct).ConfigureAwait(false)).ActiveProfileId is not { } profileId)
        {
            return [];
        }

        return (await profiles.GetPolicyAsync(profileId, ct).ConfigureAwait(false)).BlockedContentKinds;
    }

    /// <summary>
    /// The Documents page lists every document the person may see, including those in folders kept out of the Photos
    /// timeline, so a documents-only listing is not narrowed to timeline-eligible sources.
    /// </summary>
    private static bool AsksOnlyForDocuments(IReadOnlyList<string>? kinds) =>
        kinds is { Count: > 0 }
        && kinds.All(kind => string.Equals(kind?.Trim(), LocalAssetMediaKinds.Document, StringComparison.OrdinalIgnoreCase));

    public async Task<ViewQueryResult> QueryAsync(
        ViewAssetQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "View query limit must be between 1 and 500.");
        }

        if (request.SortByAddedAt && request.Cursor is not null)
        {
            throw new ArgumentException("Added-at queries use an explicit composite boundary.", nameof(request));
        }

        var decision = await authorization.AuthorizeAsync(
            await profileContext.ResolveAuthorityAsync(ct).ConfigureAwait(false),
            new ViewResourceRequest(
                request.Scope,
                request.GalleryId.HasValue ? ViewResourceKind.Gallery : ViewResourceKind.Search,
                request.GalleryId,
                AllowStaleSelectionFallback: request.AllowStaleSelectionFallback),
            ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.Scope is null)
        {
            return new ViewQueryResult(decision.Outcome);
        }

        CollectionRuleDefinition? smartRule = null;
        if (request.GalleryId is { } galleryId)
        {
            if (smartGalleries is null)
            {
                throw new InvalidOperationException("Smart Gallery query services are unavailable.");
            }

            smartRule = await smartGalleries.ResolveRuleAsync(galleryId, ct).ConfigureAwait(false);
        }

        var plan = new ViewAssetQueryPlan(
            decision.Scope,
            request.Limit,
            request.Cursor,
            request.Search,
            request.MediaKinds,
            request.FavoritesOnly,
            request.IncludeHidden && decision.Scope.Kind != ViewScopeKind.Profile && !decision.Scope.IsOtherHousehold,
            request.HiddenOnly && decision.Scope.Kind != ViewScopeKind.Profile && !decision.Scope.IsOtherHousehold,
            smartRule is null ? request.GalleryId : null,
            request.Lifecycle,
            smartRule,
            !request.SortByAddedAt && request.GalleryId is null && string.IsNullOrWhiteSpace(request.Search) && !request.WithoutLocation && request.PersonKey is null
                && !AsksOnlyForDocuments(request.MediaKinds),
            decision.Scope.Kind == ViewScopeKind.Shared,
            request.AnchorBefore, request.WithoutLocation, request.PersonKey, request.From, request.To, request.SortByAddedAt, request.AddedBefore, request.AddedAfterKey,
            request.DocumentFilter,
            await BlockedKindsAsync(ct).ConfigureAwait(false),
            request.AddedByProfileIds);
        var page = await backend.QueryAsync(plan, ct).ConfigureAwait(false);
        return new ViewQueryResult(ViewAccessOutcome.Allowed, page, decision.Scope);
    }

    public async Task<ViewTimelineIndexResult> IndexAsync(
        ViewAssetQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = await authorization.AuthorizeAsync(
            await profileContext.ResolveAuthorityAsync(ct).ConfigureAwait(false),
            new ViewResourceRequest(
                request.Scope,
                request.GalleryId.HasValue ? ViewResourceKind.Gallery : ViewResourceKind.Search,
                request.GalleryId,
                AllowStaleSelectionFallback: request.AllowStaleSelectionFallback),
            ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.Scope is null)
        {
            return new ViewTimelineIndexResult(decision.Outcome);
        }

        CollectionRuleDefinition? smartRule = null;
        if (request.GalleryId is { } galleryId)
        {
            if (smartGalleries is null)
            {
                throw new InvalidOperationException("Smart Gallery query services are unavailable.");
            }
            smartRule = await smartGalleries.ResolveRuleAsync(galleryId, ct).ConfigureAwait(false);
        }

        var plan = new ViewAssetQueryPlan(
            decision.Scope,
            request.Limit,
            null,
            request.Search,
            request.MediaKinds,
            request.FavoritesOnly,
            request.IncludeHidden && decision.Scope.Kind != ViewScopeKind.Profile && !decision.Scope.IsOtherHousehold,
            request.HiddenOnly && decision.Scope.Kind != ViewScopeKind.Profile && !decision.Scope.IsOtherHousehold,
            smartRule is null ? request.GalleryId : null,
            request.Lifecycle,
            smartRule,
            request.GalleryId is null && string.IsNullOrWhiteSpace(request.Search),
            decision.Scope.Kind == ViewScopeKind.Shared,
            null,
            ExcludedContentKinds: await BlockedKindsAsync(ct).ConfigureAwait(false),
            AddedByProfileIds: request.AddedByProfileIds);
        var index = await backend.IndexAsync(plan, ct).ConfigureAwait(false);
        return new ViewTimelineIndexResult(ViewAccessOutcome.Allowed, index, decision.Scope);
    }

    public async Task<ViewAddedByResult> AddedByAsync(
        ViewAssetQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = await authorization.AuthorizeAsync(
            await profileContext.ResolveAuthorityAsync(ct).ConfigureAwait(false),
            new ViewResourceRequest(
                request.Scope,
                ViewResourceKind.Search,
                null,
                AllowStaleSelectionFallback: request.AllowStaleSelectionFallback),
            ct).ConfigureAwait(false);
        if (!decision.IsAllowed || decision.Scope is null)
        {
            return new ViewAddedByResult(decision.Outcome);
        }

        var plan = new ViewAssetQueryPlan(
            decision.Scope,
            request.Limit,
            null,
            null,
            request.MediaKinds,
            false,
            false,
            false,
            null,
            LocalAssetLifecycleFilter.Active,
            null,
            !AsksOnlyForDocuments(request.MediaKinds),
            decision.Scope.Kind == ViewScopeKind.Shared,
            ExcludedContentKinds: await BlockedKindsAsync(ct).ConfigureAwait(false));
        var people = await backend.AddedByAsync(plan, ct).ConfigureAwait(false);
        return new ViewAddedByResult(ViewAccessOutcome.Allowed, people);
    }
}
