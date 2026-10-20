using System.Globalization;
using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Read-only Collections and Universe routes for paired native apps (<c>/api/v1/collections</c>,
/// <c>/api/v1/universe</c>). They serve the same catalogue the Dashboard Collections page shows, for the profile
/// the device token acts as: the content limit and the profile's library and feature access are applied before any
/// item is listed or counted, so an item the profile may not see never appears in a page or in a total.
/// A collection or universe that is unknown, belongs to someone else, or has nothing this profile may see is answered
/// with the same empty 404. There are no write routes and no lore or graph routes here.
/// </summary>
public static class CollectionClientEndpoints
{
    private const string Tag = "Collections (client)";

    // One message for every reason a collection or universe is not available, so the answers cannot be told apart.
    private const string NotFoundDetail = "That collection or universe is not available.";

    private const int MaxOffset = 100_000;

    public static IEndpointRouteBuilder MapCollectionClientEndpoints(this IEndpointRouteBuilder app)
    {
        var collections = app.MapGroup("/api/v1/collections")
            .WithTags(Tag);

        collections.MapGet("/", async (
            string? cursor,
            int? limit,
            HttpContext http,
            IProfileRepository profiles,
            CollectionCatalogReadService catalog,
            IDisplayProjectionReadService display,
            UniverseClientReadService universes,
            CancellationToken ct) =>
        {
            if (!TryParseCursor(cursor, out var offset))
            {
                return ApiErrors.BadRequest("The cursor is not valid.");
            }

            var page = PagedRequest.From(offset, limit, defaultLimit: 24);
            var profile = await ResolveActiveProfileAsync(http, profiles, ct);
            var allowedWorkIds = await LoadAllowedWorkIdsAsync(display, ct);
            var visible = (await catalog.GetCatalogAsync(profile, ct, allowedWorkIds))
                .Where(collection => collection.ItemCount > 0)
                .ToList();
            var window = new List<CollectionManagementCatalogDto>();
            foreach (var entry in visible.Skip(page.Offset).Take(page.Limit + 1))
            {
                window.Add(await ToNativeAsync(entry, universes, profile, ct));
            }

            return Results.Ok(PagedResponse<CollectionManagementCatalogDto>.FromPage(window, page, visible.Count));
        })
            .WithName("GetClientCollections")
            .WithSummary("Returns the paged Collections catalogue visible to the paired profile.")
            .Produces<PagedResponse<CollectionManagementCatalogDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        collections.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext http,
            IProfileRepository profiles,
            CollectionCatalogReadService catalog,
            IDisplayProjectionReadService display,
            UniverseClientReadService universes,
            CancellationToken ct) =>
        {
            var profile = await ResolveActiveProfileAsync(http, profiles, ct);
            var allowedWorkIds = await LoadAllowedWorkIdsAsync(display, ct);
            var summary = await catalog.GetSummaryAsync(id, profile, ct, allowedWorkIds);
            return summary is null || summary.ItemCount == 0
                ? ApiErrors.NotFound(NotFoundDetail)
                : Results.Ok(await ToNativeAsync(summary, universes, profile, ct));
        })
            .WithName("GetClientCollection")
            .WithSummary("Returns the summary of one collection visible to the paired profile.")
            .Produces<CollectionManagementCatalogDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        collections.MapGet("/{id:guid}/items", async (
            Guid id,
            string? cursor,
            int? limit,
            HttpContext http,
            IProfileRepository profiles,
            CollectionCatalogReadService catalog,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (!TryParseCursor(cursor, out var offset))
            {
                return ApiErrors.BadRequest("The cursor is not valid.");
            }

            var page = PagedRequest.From(offset, limit, defaultLimit: 24);
            var profile = await ResolveActiveProfileAsync(http, profiles, ct);
            var allowedWorkIds = await LoadAllowedWorkIdsAsync(display, ct);
            // The read lists shows, comic series and albums as their root work and authorises those roots itself.
            var result = await catalog.GetItemPageAsync(
                id, profile, page.Offset, page.Limit, allowedWorkIds, ct);
            // A collection the catalogue would not list, or with nothing this profile may see, is answered like an
            // unknown one at every offset; only a cursor past the end of a collection that has items gives an empty page.
            if (!result.Found || result.Forbidden || result.TotalCount == 0)
            {
                return ApiErrors.NotFound(NotFoundDetail);
            }

            return Results.Ok(PagedResponse<CollectionItemDto>.FromPage(result.Items, page, result.TotalCount));
        })
            .WithName("GetClientCollectionItems")
            .WithSummary("Returns one page of the items of a collection visible to the paired profile.")
            .Produces<PagedResponse<CollectionItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        var universe = app.MapGroup("/api/v1/universe")
            .WithTags(Tag);

        universe.MapGet("/{qid}", async (
            string qid,
            HttpContext http,
            IProfileRepository profiles,
            UniverseClientReadService universes,
            CancellationToken ct) =>
        {
            var profile = await ResolveActiveProfileAsync(http, profiles, ct);
            var detail = await universes.LoadAsync(qid, profile, ct);
            return detail is null ? ApiErrors.NotFound(NotFoundDetail) : Results.Ok(detail);
        })
            .WithName("GetClientUniverse")
            .WithSummary("Returns one Universe with its member shelves, limited to what the paired profile may see.")
            .Produces<UniverseClientDetailDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireClientScope(ClientApiScopes.LibraryRead);

        return app;
    }

    // A Universe entry exposes its QID (and the "universe" group type) only when /api/v1/universe/{qid} would serve it to
    // this same profile, so a QID an app is handed always opens.
    private static async Task<CollectionManagementCatalogDto> ToNativeAsync(
        CollectionManagementCatalogDto entry,
        UniverseClientReadService universes,
        Profile? profile,
        CancellationToken ct)
    {
        var qid = UniverseClientReadService.NormalizeQid(entry.UniverseQid);
        if (qid is not null && await universes.LoadAsync(qid, profile, ct) is null)
        {
            qid = null;
        }

        return ManagedCollectionMapper.ForNativeClient(entry, qid);
    }

    // The cursor is the offset the previous page handed back (never beyond MaxOffset). Anything else is a client error.
    private static bool TryParseCursor(string? cursor, out int offset)
    {
        offset = 0;
        return string.IsNullOrEmpty(cursor)
            || (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset <= MaxOffset);
    }

    private static async Task<IReadOnlySet<Guid>> LoadAllowedWorkIdsAsync(
        IDisplayProjectionReadService display,
        CancellationToken ct) =>
        await display.LoadVisibleWorkIdsAsync(ct);

    // The profile always comes from the device token, never from a request value.
    internal static async Task<Profile?> ResolveActiveProfileAsync(
        HttpContext http,
        IProfileRepository profiles,
        CancellationToken ct)
    {
        var authority = await http.RequestServices
            .GetRequiredService<IRequestAuthorityResolver>()
            .ResolveAsync(http, ct);
        return authority.ActiveProfileId is { } profileId
            ? await profiles.GetByIdAsync(profileId, ct)
            : null;
    }
}
