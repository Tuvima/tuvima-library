using MediaEngine.Api.Http;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Bulk organising, the list export and the remembered Documents sort. Every route is closed to a profile that may
/// not use Documents. The bulk edit checks each document on its own and says plainly which ones it left alone; the
/// export only lists what the person's own query would show.
/// </summary>
public static partial class ViewEndpoints
{
    /// <summary>Header telling the Dashboard the export stopped at the row cap.</summary>
    public const string ExportTruncatedHeader = "X-Tuvima-Export-Truncated";

    private static void MapDocumentBulk(RouteGroupBuilder group)
    {
        group.MapPost("/documents/bulk", async ([FromBody] ViewDocumentBulkRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ViewDocumentBulkService service, CancellationToken ct) =>
        {
            try
            {
                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(
                    true, identity, preferences, resolver, authorization, ct);
                return access is null
                    ? refusal!
                    : Results.Ok(await service.RunAsync(access.Authority, access.Viewer, request, ct));
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException
                or ArgumentException)
            {
                return CatalogueFailure(exception);
            }
        }).WithName("BulkEditViewDocuments").Produces<ViewDocumentBulkResultDto>();

        group.MapGet("/documents/export", async (HttpContext http, string? scope, Guid? scopeProfileId,
            string? q, Guid? galleryId, string? lifecycle,
            Guid[]? tag, Guid? correspondent, Guid? docType, DateOnly? docFrom, DateOnly? docTo,
            string? filing, string[]? field, string? docSort,
            IViewRequestProfileContext identity, IViewProfileRepository preferences,
            IViewResourceAuthorizationService authorization, IViewQueryOrchestrator queries,
            IViewCatalogueRepository catalogue, IViewScopeResolver resolver, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (!authority.IsAuthenticated)
            {
                return Unauthenticated();
            }

            try
            {
                var allowedKinds = await KindsWithoutBlockedDocumentsAsync(authority, [LocalAssetMediaKinds.Document], preferences, ct);
                if (allowedKinds is null)
                {
                    return DocumentsNotAllowed();
                }

                var requested = authority.ActiveProfileId is { } profileId
                    ? await GetScopeAsync(profileId, scope, scopeProfileId, preferences, ct)
                    : ParseScope(scope, scopeProfileId);
                var documentFilter = BuildDocumentFilter(tag, correspondent, docType, docFrom, docTo, filing, field, docSort);
                var items = new List<LocalAssetDto>();
                string? cursor = null;
                bool more;
                do
                {
                    var result = await queries.QueryAsync(new ViewAssetQueryRequest(
                        requested, 500, cursor, q, allowedKinds, GalleryId: galleryId,
                        Lifecycle: ParseLifecycle(lifecycle), DocumentFilter: documentFilter), ct);
                    if (result.Page is null)
                    {
                        return Access(result.Outcome);
                    }

                    items.AddRange(result.Page.Items);
                    cursor = result.Page.NextCursor;
                    more = result.Page.HasMore && cursor is not null;
                }
                while (more && items.Count <= ViewDocumentCsv.MaximumRows);

                var truncated = items.Count > ViewDocumentCsv.MaximumRows;
                if (truncated)
                {
                    items.RemoveRange(ViewDocumentCsv.MaximumRows, items.Count - ViewDocumentCsv.MaximumRows);
                    http.Response.Headers[ExportTruncatedHeader] = "true";
                }

                var (access, refusal) = await ResolveCatalogueAccessAsync(
                    scope, scopeProfileId, true, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                // Only the custom fields this person can see get a column, exactly like the filter rail.
                var fields = catalogue.GetCatalogue(access.Viewer, [LocalAssetMediaKinds.Document], ct).Fields
                    .Select(entry => new ViewDocumentFieldDto(
                        entry.Id, entry.Name, entry.FieldKind ?? ViewDocumentFieldKinds.Text, entry.Choices, entry.UsageCount, false))
                    .ToList();
                // The list carries tags and organising details; the custom field values are read for these rows only.
                var fieldValues = catalogue.GetDocumentFieldValues(access.Viewer, items.Select(item => item.Id).ToList(), ct);
                var name = $"documents-{DateTime.UtcNow:yyyy-MM-dd}.csv";
                return Results.Stream(output => ViewDocumentCsv.WriteAsync(output, items, fields, fieldValues, ct),
                    "text/csv; charset=utf-8", name);
            }
            catch (ArgumentException exception) { return ApiErrors.BadRequest(exception.Message); }
            catch (InvalidOperationException exception) { return ApiErrors.Unprocessable(exception.Message); }
        }).WithName("ExportViewDocumentsCsv").Produces(StatusCodes.Status200OK, contentType: "text/csv");

        group.MapGet("/preferences/documents-sort", async (IViewRequestProfileContext identity,
            IViewProfileRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            if (!await MayUseDocumentsAsync(authority, repository, ct))
            {
                return DocumentsNotAllowed();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null, ViewResourceAction.Manage), ct);
            return access.IsAllowed
                ? Results.Ok(new ViewDocumentsSortDto((await repository.GetPreferencesAsync(profileId, ct)).DocumentsSort))
                : Access(access.Outcome);
        }).WithName("GetViewDocumentsSort").Produces<ViewDocumentsSortDto>();

        group.MapPut("/preferences/documents-sort", async ([FromBody] ViewDocumentsSortDto request,
            IViewRequestProfileContext identity, IViewProfileRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            if (!await MayUseDocumentsAsync(authority, repository, ct))
            {
                return DocumentsNotAllowed();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null, ViewResourceAction.Manage), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            var sort = string.IsNullOrWhiteSpace(request.Sort) ? null : request.Sort.Trim().ToLowerInvariant();
            if (sort is not null && !ViewDocumentSorts.All.Contains(sort))
            {
                return ApiErrors.BadRequest($"Documents can be sorted by: {string.Join(", ", ViewDocumentSorts.All)}.");
            }

            return await repository.SaveDocumentsSortAsync(profileId, sort, ct)
                ? Results.Ok(new ViewDocumentsSortDto(sort))
                : Missing();
        }).WithName("UpdateViewDocumentsSort").Produces<ViewDocumentsSortDto>();
    }
}
