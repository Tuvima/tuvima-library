using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// What one library's page can do (Libraries spec D7, D8): scan this library only, and copy server files into its import destination.
/// Both are an administrator's job, like the rest of Libraries in Settings, and a Vault library answers as a library that does not exist.
/// </summary>
public static class LibraryFolderActionEndpoints
{
    public static IEndpointRouteBuilder MapLibraryFolderActionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/libraries/{libraryId}")
            .WithTags("Library", "Libraries")
            .RequireEffectiveAdministrator();

        group.MapPost("/scan", async (
            string libraryId,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            IIngestionEngine engine,
            CancellationToken ct) =>
        {
            if (!await UnmatchedEndpoints.IsListedLibraryAsync(libraryId, configuration, vault, ct))
            {
                return ApiErrors.NotFound($"Library '{libraryId}' was not found.");
            }

            var library = configuration.LoadLibraries().Libraries.First(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            // Only this library's own folders, and only those that are being scanned (a paused folder is left alone) and are there on disk.
            var targets = library.ScannableSources
                .Where(source => Directory.Exists(source.Path))
                .Select(source => new IngestionScanTarget(source.Path, source.IncludeSubdirectories))
                .ToList();
            if (targets.Count == 0)
            {
                return ApiErrors.BadRequest("This library has no folder that can be scanned right now.");
            }

            await engine.ScanDirectories(targets, ct);
            return Results.Accepted(value: new RescanAcceptedResponse("Scan started for this library.", targets.Count));
        })
        .WithName("ScanLibrary")
        .WithSummary("Scans this library's own folders (those with scanning on and present on disk) and no others.")
        .Produces<RescanAcceptedResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/folder-titles", async (
            string libraryId,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            ILibraryFolderTitleCountRepository titleCounts,
            CancellationToken ct) =>
        {
            if (!await UnmatchedEndpoints.IsListedLibraryAsync(libraryId, configuration, vault, ct))
            {
                return ApiErrors.NotFound($"Library '{libraryId}' was not found.");
            }

            var library = configuration.LoadLibraries().Libraries.First(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
            var sources = library.CataloguedSources.ToList();
            var byPath = await titleCounts.CountByFolderAsync(sources.Select(source => source.Path).ToList(), ct);
            return Results.Ok(new LibraryFolderTitlesDto
            {
                Counts = sources.ToDictionary(source => source.Id, source => byPath.GetValueOrDefault(source.Path), StringComparer.Ordinal),
            });
        })
        .WithName("GetLibraryFolderTitles")
        .WithSummary("How many titles the catalogue holds for each folder of this library, by the folder's source id.")
        .Produces<LibraryFolderTitlesDto>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/add-files", async (
            string libraryId,
            LibraryAddFilesRequest request,
            HttpContext http,
            IConfigurationLoader configuration,
            IVaultLibraryRepository vault,
            [FromServices] LibraryAddFilesService addFiles,
            IIngestionEngine engine,
            ILogger<LibraryAddFilesService> logger,
            CancellationToken ct) =>
        {
            if (!await UnmatchedEndpoints.IsListedLibraryAsync(libraryId, configuration, vault, ct))
            {
                return ApiErrors.NotFound($"Library '{libraryId}' was not found.");
            }

            var outcome = await addFiles.AddAsync(libraryId, request.Paths, await http.HiddenVaultFolderRootsAsync(ct), ct);
            if (outcome.Result is not { } result)
            {
                return Results.Problem(
                    detail: outcome.RefusalMessage,
                    statusCode: outcome.RefusalStatus,
                    title: "Files were not added.",
                    extensions: new Dictionary<string, object?> { ["code"] = outcome.RefusalCode });
            }

            if (result.Copied > 0 && outcome.DestinationPath is { } destination)
            {
                try
                {
                    await engine.ScanDirectory(destination, true, ct);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    // The files are copied; the next scan takes them in if this one could not start.
                    logger.LogWarning(exception, "Add files copied into library {LibraryId} but the scan did not start", libraryId);
                }
            }

            return Results.Ok(result);
        })
        .WithName("AddFilesToLibrary")
        .WithSummary(
            "Copies server files into this library's import destination. The originals are only read. Refused before anything is copied " +
            "when there is no import destination, a path is outside the approved storage locations, or there is not enough free space.")
        .Produces<LibraryAddFilesResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status507InsufficientStorage);

        return app;
    }
}
