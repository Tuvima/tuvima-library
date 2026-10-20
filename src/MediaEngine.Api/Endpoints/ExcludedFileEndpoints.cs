using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Settings;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The files that stayed on disk when their title was deleted from the library (Settings → Libraries → Excluded files): the list,
/// and "Add back". Settings is an administrator surface, so there is no application permission.
/// </summary>
public static class ExcludedFileEndpoints
{
    public static IEndpointRouteBuilder MapExcludedFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/excluded-files")
            .WithTags("Library", "Settings");

        group.MapGet("/", async (
            [FromQuery(Name = "library_id")] string? libraryId,
            ExcludedFileService excluded,
            CancellationToken ct) =>
            Results.Ok(await excluded.ListAsync(libraryId, ct)))
        .WithName("ListExcludedFiles")
        .WithSummary("Files that stayed on disk when their title was deleted, newest first; one library's when library_id is given. File names and folder labels only, never disk paths.")
        .Produces<IReadOnlyList<ExcludedFileDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireEffectiveAdministrator();

        group.MapPost("/{id:guid}/add-back", async (
            Guid id,
            ExcludedFileService excluded,
            CancellationToken ct) =>
        {
            var result = await excluded.AddBackAsync(id, ct);
            return result is null
                ? ApiErrors.NotFound("This file is not in the excluded list.")
                : Results.Ok(result);
        })
        .WithName("AddBackExcludedFile")
        .WithSummary("Removes the exclusion and queues a scan of that one file. When the file is no longer on disk the exclusion is still removed and nothing is queued.")
        .Produces<ExcludedFileAddBackResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        return app;
    }
}
