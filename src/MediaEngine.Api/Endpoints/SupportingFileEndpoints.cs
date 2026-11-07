using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Engine side of the supporting-files browser (universal editor v5 D15, D16, D30; screens E and F): one root title's files listed with
/// search, Where, type and group filters, paging and counts, and the bulk actions the editor sends when it saves (set group, set role,
/// Belongs to, tags, delete). Access follows the title: the list needs the right to read it, the bulk actions the right to edit it and an
/// administrator, and a title the caller may not see (a Vault title without an unlocked Vault session, a library hidden from a Kids
/// profile) answers "not found" before anything is read.
/// </summary>
public static class SupportingFileEndpoints
{
    public static IEndpointRouteBuilder MapSupportingFileEndpoints(this IEndpointRouteBuilder app)
    {
        var files = app.MapGroup("/api/v1/supporting-files").WithTags("Supporting files");

        files.MapGet("/titles/{titleId:guid}", async (
            Guid titleId,
            string? q,
            string? where,
            string? type,
            string? group,
            string? sort,
            bool? desc,
            int? page,
            int? pageSize,
            SupportingFileListService list,
            CancellationToken ct) =>
            Results.Ok(await list.ListAsync(
                titleId,
                new SupportingFileListQuery(q, where, type, group, sort, desc ?? false, page ?? 1, pageSize),
                ct)))
        .WithName("ListSupportingFiles")
        .WithSummary("Lists one title's supporting files: search, Where, type and group filters, sort, paging, and the counts for the browser's left list.")
        .Produces<SupportingFileListDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.LibraryRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "titleId");

        files.MapPost("/titles/{titleId:guid}/bulk-update", async (
            Guid titleId,
            SupportingFileBulkUpdateRequest request,
            SupportingFileBulkService bulk,
            CancellationToken ct) =>
        {
            var outcome = await bulk.UpdateAsync(titleId, request, ct);
            return outcome.Value is { } result ? Results.Ok(result) : Refusal(outcome.Problem, outcome.Message);
        })
        .WithName("BulkUpdateSupportingFiles")
        .WithSummary("Sets group, role, Belongs to or tags on several supporting files in one step; with preview true it only checks and plans the file moves.")
        .Produces<SupportingFileBulkUpdateResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator()
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "titleId");

        files.MapPost("/titles/{titleId:guid}/bulk-delete", async (
            Guid titleId,
            SupportingFileBulkDeleteRequest request,
            SupportingFileBulkService bulk,
            HttpContext http,
            IRequestAuthorityResolver authorities,
            IProfileRepository profiles,
            CancellationToken ct) =>
        {
            var authority = await authorities.ResolveAsync(http, ct);
            var profile = authority.ActiveProfileId is { } profileId ? await profiles.GetByIdAsync(profileId, ct) : null;
            var outcome = await bulk.DeleteAsync(titleId, request, authority.AccountId, profile?.DisplayName, ct);
            return outcome.Value is { } result ? Results.Ok(result) : Refusal(outcome.Problem, outcome.Message);
        })
        .WithName("BulkDeleteSupportingFiles")
        .WithSummary("Removes several supporting files from the Library; the files on disk follow the library's file handling.")
        .Produces<SupportingFileBulkDeleteResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator()
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "titleId");

        return app;
    }

    private static IResult Refusal(SupportingFileBulkProblem? problem, string? message) => problem switch
    {
        SupportingFileBulkProblem.TitleNotFound => ApiErrors.NotFound(message ?? SupportingFileBulkService.TitleNotFound),
        _ => ApiErrors.BadRequest(message ?? "That change could not be made."),
    };
}
