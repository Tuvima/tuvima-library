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
/// search, Where, type and group filters, paging and counts, the preview of a bulk change (what would change and which files would move),
/// and Delete. Saving a change is not here: set group, role, Belongs to and tags are a change of the editor's staged save
/// (<c>/library/edit-session</c>, kind <c>supporting_files</c>), so one Save is all or nothing across title fields and supporting files.
/// Delete asks once and happens at once, so it is its own action, and like every delete on disk it needs an administrator in person (no
/// paired app or service application acting for one). Access follows the title: the list needs the right to read it, the others the right
/// to edit it, and a title the caller may not see (a Vault title without an unlocked Vault session, a library hidden from a Kids profile)
/// answers "not found" before anything is read.
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

        files.MapPost("/titles/{titleId:guid}/preview", async (
            Guid titleId,
            SupportingFileBulkUpdateRequest request,
            SupportingFileBulkService bulk,
            CancellationToken ct) =>
        {
            var outcome = await bulk.PreviewAsync(titleId, request, ct);
            return outcome.Value is { } result ? Results.Ok(result) : Refusal(outcome.Problem, outcome.Message);
        })
        .WithName("PreviewSupportingFilesChange")
        .WithSummary("Checks a group, role, Belongs to or tags change on several supporting files and plans the file moves, writing nothing.")
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
        .RequireEffectiveAdministratorInPerson()
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "titleId");

        return app;
    }

    private static IResult Refusal(SupportingFileBulkProblem? problem, string? message) => problem switch
    {
        SupportingFileBulkProblem.TitleNotFound => ApiErrors.NotFound(message ?? SupportingFileBulkService.TitleNotFound),
        _ => ApiErrors.BadRequest(message ?? "That change could not be made."),
    };
}
