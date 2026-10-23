using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Library;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The first check of a newly added folder (Library setup spec D35, D61): which folders are being checked or are paused, and Keep going for a
/// paused one. Removing the folder is the existing folder removal, which drops the check with it. Looking at and answering the check is an
/// administrator's job, like the rest of Libraries in Settings. A Vault library never has a check.
/// </summary>
public static class FolderFirstCheckEndpoints
{
    public static IEndpointRouteBuilder MapFolderFirstCheckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/first-checks")
            .WithTags("Library", "FirstCheck")
            .RequireEffectiveAdministrator();

        group.MapGet("/", async ([FromServices] FolderFirstCheckService service, CancellationToken ct) =>
            Results.Ok(new FolderFirstChecksDto
            {
                Items = (await service.ListAsync(ct)).Select(FolderFirstCheckService.ToItemDto).ToList(),
            }))
        .WithName("ListFolderFirstChecks")
        .WithSummary("The folders whose first check is running or paused, with the library, folder, path, state, tried and Unmatched counts and the folder's total.")
        .Produces<FolderFirstChecksDto>();

        group.MapPost("/{sourceId}/keep-going", async (
            string sourceId,
            [FromServices] FolderFirstCheckService service,
            CancellationToken ct) =>
            await service.KeepGoingAsync(sourceId, ct)
                ? Results.NoContent()
                : ApiErrors.Conflict("That folder is not being checked, so there is nothing to keep going."))
        .WithName("KeepGoingOnPausedFolder")
        .WithSummary("Keep going on a paused folder (or one still being checked): identification resumes, Unmatched files stay Unmatched and are looked up again later, and matched files' held moves and tags are released to the next run.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
