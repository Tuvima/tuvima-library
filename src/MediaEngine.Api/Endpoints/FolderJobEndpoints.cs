using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Settings;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Converting a folder to Managed: the honest preview, starting the organising job, pausing, resuming and cancelling it,
/// and reading or exporting the log of every move. The job itself is one durable operation on the folder-jobs queue.
/// </summary>
public static class FolderJobEndpoints
{
    public static IEndpointRouteBuilder MapFolderJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/libraries/folders")
            .WithTags("Settings", "Libraries", "Folder jobs")
            .RequireAdmin();

        group.MapPost("/conversion/preview", async (
            [FromBody] FolderConversionPreviewRequest request,
            FolderJobService jobs,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await jobs.PreviewAsync(request, ct));
            }
            catch (FolderJobException exception)
            {
                return Problem(exception);
            }
        })
        .WithName("PreviewFolderConversion")
        .WithSummary("Counts a folder's media files, or previews what converting it to Managed would do. Changes nothing.")
        .Produces<FolderConversionPreviewResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/convert", async (
            [FromBody] FolderConvertRequest request,
            FolderJobService jobs,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await jobs.StartConversionAsync(request.SourceId, ct));
            }
            catch (FolderJobException exception)
            {
                return Problem(exception);
            }
        })
        .WithName("ConvertFolderToManaged")
        .WithSummary("Makes a folder Managed and queues the job that renames and reorganises the files already in it.")
        .Produces<FolderJobStatusDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/jobs", async (FolderJobService jobs, CancellationToken ct) =>
            Results.Ok(await jobs.ListAsync(ct)))
        .WithName("ListFolderJobs")
        .WithSummary("Lists the open folder jobs and each folder's most recent finished job from the last week.")
        .Produces<IReadOnlyList<FolderJobStatusDto>>();

        group.MapGet("/jobs/{operationId:guid}", async (Guid operationId, FolderJobService jobs, CancellationToken ct) =>
            await jobs.GetAsync(operationId, ct) is { } status
                ? Results.Ok(status)
                : ApiErrors.NotFound("That folder job was not found."))
        .WithName("GetFolderJob")
        .WithSummary("Returns one folder job.")
        .Produces<FolderJobStatusDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/jobs/{operationId:guid}/control", async (
            Guid operationId,
            [FromBody] FolderJobControlRequest request,
            FolderJobService jobs,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await jobs.ControlAsync(operationId, request.Action, ct));
            }
            catch (FolderJobException exception)
            {
                return Problem(exception);
            }
        })
        .WithName("ControlFolderJob")
        .WithSummary("Pauses, resumes or cancels a folder job. Cancelling keeps the work done so far.")
        .Produces<FolderJobStatusDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/jobs/{operationId:guid}/log", async (
            Guid operationId,
            int? offset,
            int? limit,
            FolderJobService jobs,
            CancellationToken ct) =>
        {
            try
            {
                var paged = PagedRequest.From(offset, limit, defaultLimit: 200, maxLimit: 1000);
                return Results.Ok(await jobs.GetLogAsync(operationId, paged.Offset, paged.Limit, ct));
            }
            catch (FolderJobException exception)
            {
                return Problem(exception);
            }
        })
        .WithName("GetFolderJobLog")
        .WithSummary("Reads a page of the log of every file a folder job looked at, old path to new path.")
        .Produces<FolderJobLogDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/jobs/{operationId:guid}/log/export", async (
            Guid operationId,
            HttpContext context,
            FolderJobService jobs,
            CancellationToken ct) =>
        {
            try
            {
                await jobs.GetAsync(operationId, ct);
                context.Response.ContentType = "text/csv; charset=utf-8";
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"folder-job-{operationId:N}.csv\"";
                await jobs.WriteLogCsvAsync(operationId, context.Response.Body, ct);
                return Results.Empty;
            }
            catch (FolderJobException exception)
            {
                return Problem(exception);
            }
        })
        .WithName("ExportFolderJobLog")
        .WithSummary("Downloads the whole log of a folder job as a CSV file.")
        .Produces(StatusCodes.Status200OK, contentType: "text/csv")
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static IResult Problem(FolderJobException exception) =>
        exception.NotFound ? ApiErrors.NotFound(exception.Message)
        : exception.Conflict ? ApiErrors.Conflict(exception.Message)
        : ApiErrors.BadRequest(exception.Message);
}
