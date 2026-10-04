using Dapper;
using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Endpoints;

public static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ingestion")
                       .WithTags("Ingestion");

        group.MapGet("/notification-status", async (IngestionNotificationReadService readService, CancellationToken ct) =>
            Results.Ok(await readService.GetRecentAsync(ct)))
          .WithName("GetIngestionNotificationStatus")
          .Produces<List<IngestionOperationsBatchDto>>()
          .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        group.MapGet("/operations", async (
            IIngestionOperationsStatusService statusService,
            CancellationToken ct) =>
        {
            var snapshot = await statusService.GetSnapshotAsync(ct);
            return Results.Ok(snapshot);
        })
        .WithName("GetIngestionOperationsSnapshot")
        .WithSummary("Aggregated Ingestion status for scans, review, providers, folders, and recent batches.")
        .Produces<IngestionOperationsSnapshotDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        group.MapGet("/presentation", async (
            IIngestionPresentationReadService readService,
            int? currentLimit,
            int? recentDayLimit,
            int? recentItemsPerDay,
            CancellationToken ct) => Results.Ok(await readService.GetSnapshotAsync(
                currentLimit ?? 8,
                recentDayLimit ?? 3,
                recentItemsPerDay ?? 9,
                ct)))
        .WithName("GetIngestionPresentation")
        .WithSummary("Returns the bounded media-centric Ingestion page projection.")
        .Produces<IngestionPresentationSnapshotDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        group.MapGet("/media-groups", async (
            IIngestionPresentationReadService readService,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, 50, 100);
            return Results.Ok(await readService.GetCurrentMediaAsync(page.Offset, page.Limit, ct));
        })
        .WithName("GetCurrentIngestionMediaGroups")
        .WithSummary("Returns paged media groups currently entering the library.")
        .Produces<PagedResponse<IngestionMediaGroupDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        group.MapGet("/recent-additions", async (
            IIngestionPresentationReadService readService,
            string? search,
            string? lane,
            DateTimeOffset? start,
            DateTimeOffset? end,
            string? sort,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, 50, 100);
            return Results.Ok(await readService.GetRecentAdditionsAsync(search, lane, start, end, page.Offset, page.Limit, sort, ct));
        })
        .WithName("GetRecentIngestionAdditions")
        .WithSummary("Returns paged, event-scoped media additions across completed runs.")
        .Produces<PagedResponse<IngestionMediaGroupDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/media-groups/{groupId:guid}", async (
            Guid batchId,
            Guid groupId,
            IIngestionPresentationReadService readService,
            CancellationToken ct) =>
        {
            var item = await readService.GetMediaGroupAsync(batchId, groupId, ct);
            return item is null ? ApiErrors.NotFound("That ingestion media group was not found.") : Results.Ok(item);
        })
        .WithName("GetIngestionMediaGroup")
        .WithSummary("Returns one pinned ingestion media group.")
        .Produces<IngestionMediaGroupDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/media-groups/{groupId:guid}/children", async (
            Guid batchId,
            Guid groupId,
            IIngestionPresentationReadService readService,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, 50, 250);
            return Results.Ok(await readService.GetChildrenAsync(batchId, groupId, page.Offset, page.Limit, ct));
        })
        .WithName("GetIngestionMediaGroupChildren")
        .WithSummary("Returns lazy, paged tracks, episodes, issues, or parts for an ingestion group.")
        .Produces<PagedResponse<IngestionMediaChildDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapPost("/assets/{assetId:guid}/reread-metadata", async (
            Guid assetId,
            IIngestionEngine engine,
            CancellationToken ct) =>
        {
            var result = await engine.RereadAssetMetadataAsync(assetId, ct);
            var response = new FileMetadataRereadResponse
            {
                AssetId = result.AssetId,
                Status = result.Status,
                Refreshed = result.Refreshed,
                ContentHashChanged = result.ContentHashChanged,
                Message = result.Message,
            };
            return result.Status == "AssetMissing"
                ? ApiErrors.NotFound(result.Message)
                : Results.Ok(response);
        })
        .WithName("RereadAssetMetadata")
        .WithSummary("Re-read local tags and technical metadata without identity matching or file moves.")
        .Produces<FileMetadataRereadResponse>()
        .Produces(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionRetry);

        group.MapPost("/scan", async (
            ScanRequest? request,
            IIngestionEngine engine,
            IOptions<IngestionOptions> opts,
            CancellationToken ct) =>
        {
            var rootPath = request?.RootPath
                ?? opts.Value.EffectiveWatchDirectories.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return ApiErrors.BadRequest(
                    "No root_path provided and no library source path is configured.");
            }

            if (!Directory.Exists(rootPath))
            {
                return ApiErrors.BadRequest($"Directory does not exist: {rootPath}");
            }

            var operations = await engine.DryRunAsync(rootPath, ct);
            var response = new ScanResponse
            {
                Operations = operations
                    .Select(static operation => new PendingOperationDto
                    {
                        SourcePath = operation.SourcePath,
                        DestinationPath = operation.DestinationPath,
                        OperationKind = operation.OperationKind,
                        Reason = operation.Reason,
                    })
                    .ToList(),
            };

            return Results.Ok(response);
        })
        .WithName("TriggerScan")
        .WithSummary("Simulate a library scan and return pending operations without mutating files.")
        .Produces<ScanResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionRun);

        // ── POST /ingestion/library-scan ──────────────────────────────────────────

        group.MapPost("/library-scan", async (
            ILibraryScanner scanner,
            IOptions<IngestionOptions> opts,
            CancellationToken ct) =>
        {
            var root = opts.Value.LibraryRoot;

            if (string.IsNullOrWhiteSpace(root))
            {
                return ApiErrors.BadRequest(
                    "LibraryRoot is not configured. Set Ingestion:LibraryRoot in appsettings.json.");
            }

            if (!Directory.Exists(root))
            {
                return ApiErrors.BadRequest($"Library root does not exist: {root}");
            }

            var result = await scanner.ScanAsync(root, ct);

            // Scan .universe/ to recover fictional entities and relationships.
            var universeResult = await scanner.ScanUniversesAsync(root, ct);

            return Results.Ok(new LibraryScanResponse
            {
                CollectionsUpserted = result.CollectionsUpserted,
                EditionsUpserted = result.EditionsUpserted,
                PeopleRecovered = 0,
                UniversesUpserted = universeResult.UniversesUpserted,
                EntitiesUpserted = universeResult.EntitiesUpserted,
                RelationshipsUpserted = universeResult.RelationshipsUpserted,
                Errors = result.Errors + universeResult.Errors,
                ElapsedMs = (long)result.Elapsed.TotalMilliseconds,
            });
        })
        .WithName("TriggerLibraryScan")
        .WithSummary(
            "Scans media files in the Library Root, updates file paths for known assets, " +
            "and notes new files for a follow-up ingestion pass (Great Inhale v2).")
        .Produces<LibraryScanResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionRun);

        // ── GET /ingestion/watch-folder ─────────────────────────────────────────

        group.MapGet("/watch-folder", (
            IOptions<IngestionOptions> opts,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 100, maxLimit: 500);
            var watchDir = opts.Value.EffectiveWatchDirectories.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(watchDir))
            {
                return Results.Ok(new WatchFolderPageResponse
                {
                    Offset = page.Offset,
                    Limit = page.Limit,
                });
            }

            if (!Directory.Exists(watchDir))
            {
                return Results.Ok(new WatchFolderPageResponse
                {
                    WatchDirectory = watchDir,
                    Offset = page.Offset,
                    Limit = page.Limit,
                });
            }

            var searchOption = opts.Value.IncludeSubdirectories
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var files = GetNewestWatchFiles(watchDir, searchOption, page.Offset + page.Limit + 1, ct);
            var response = PagedResponse<WatchFolderFileDto>.FromPage(
                files.Skip(page.Offset).ToList(),
                page);

            return Results.Ok(new WatchFolderPageResponse
            {
                WatchDirectory = watchDir,
                Files = response.Items,
                Offset = response.Offset,
                Limit = response.Limit,
                HasMore = response.HasMore,
                NextCursor = response.NextCursor,
            });
        })
        .WithName("ListWatchFolder")
        .WithSummary("List files currently sitting in the Watch Folder.")
        .Produces<WatchFolderPageResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        // ── POST /ingestion/rescan ──────────────────────────────────────────────

        group.MapPost("/rescan", async (
            RescanRequest? request,
            IIngestionEngine engine,
            IOptions<IngestionOptions> opts,
            CancellationToken ct) =>
        {
            var includeSubdirectories = request?.IncludeSubdirectories ?? opts.Value.IncludeSubdirectories;
            var requestedRoot = request?.RootPath;

            if (!string.IsNullOrWhiteSpace(requestedRoot))
            {
                if (!Directory.Exists(requestedRoot))
                {
                    return ApiErrors.BadRequest($"Watch directory does not exist: {requestedRoot}");
                }

                await engine.ScanDirectory(requestedRoot, includeSubdirectories, ct);

                return Results.Accepted(value: new RescanAcceptedResponse(
                    "Rescan triggered. Files will be processed shortly.",
                    1));
            }

            var watchDirs = opts.Value.EffectiveWatchDirectories;
            if (watchDirs.Count == 0)
            {
                return ApiErrors.BadRequest(
                    "No library source paths are configured.");
            }

            var scanTargets = watchDirs
                .Where(Directory.Exists)
                .Select(watchDir => new IngestionScanTarget(watchDir, includeSubdirectories))
                .ToList();

            if (scanTargets.Count == 0)
            {
                return ApiErrors.BadRequest("No configured watch directories exist on disk.");
            }

            await engine.ScanDirectories(scanTargets, ct);

            return Results.Accepted(value: new RescanAcceptedResponse(
                "Rescan triggered. Files will be processed shortly.",
                scanTargets.Count));
        })
        .WithName("TriggerRescan")
        .WithSummary(
            "Re-scan the Watch Folder for new or unprocessed files. " +
            "Files are fed into the ingestion pipeline for processing.")
        .Produces<RescanAcceptedResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionRun);

        // ── POST /ingestion/reconcile ─────────────────────────────────────────

        group.MapPost("/reconcile", async (
            LibraryReconciliationService reconciler,
            CancellationToken ct) =>
        {
            var result = await reconciler.ReconcileAsync(ct);
            return Results.Ok(new ReconciliationResultResponse
            {
                TotalScanned = result.TotalScanned,
                MissingCount = result.MissingCount,
                ElapsedMs = result.ElapsedMs,
                DuplicateReadWorksMerged = result.DuplicateReadWorksMerged,
                AudiobookAuthorsAligned = result.AudiobookAuthorsAligned,
            });
        })
        .WithName("TriggerReconciliation")
        .WithSummary(
            "Scan all Normal-status assets and clean up any whose files " +
            "are missing from disk.")
        .Produces<ReconciliationResultResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionRun);

        // ── GET /ingestion/batches ────────────────────────────────────────────
        group.MapGet("/batches", async (
            IIngestionBatchResponseService batchResponses,
            int? limit,
            CancellationToken ct) =>
        {
            var responses = await batchResponses.GetRecentAsync(PagedRequest.From(null, limit, defaultLimit: 20).Limit, ct);
            return Results.Ok(responses);
        })
        .WithName("GetRecentBatches")
        .WithSummary("List recent ingestion batches, newest first.")
        .Produces<List<IngestionBatchResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        // ── GET /ingestion/batches/attention-count ────────────────────────────
        group.MapGet("/batches/attention-count", async (
            IIngestionBatchRepository batchRepo) =>
        {
            var count = await batchRepo.GetNeedsAttentionCountAsync();
            return Results.Ok(new BatchAttentionCountResponse(count));
        })
        .WithName("GetBatchAttentionCount")
        .WithSummary("Count of items across all batches that need curator attention.")
        .Produces<BatchAttentionCountResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        // ── GET /ingestion/batches/{id} ───────────────────────────────────────
        group.MapGet("/batches/{id:guid}/items", async (
            Guid id,
            IIngestionBatchReadService batchReadService,
            int? offset,
            int? limit,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 100, maxLimit: 500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var items = await batchReadService.GetItemsAsync(id, page.Offset, page.Limit + 1, ct);
            var response = PagedResponse<IngestionBatchItemResponse>.FromPage(items, page);
            var logger = loggerFactory.CreateLogger("MediaEngine.Api.IngestionBatches");
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 1000)
            {
                logger.LogWarning(
                    "Large-list read {Operation} took {ElapsedMs} ms with offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    "ingestion.batch.items",
                    sw.ElapsedMilliseconds,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }
            else
            {
                logger.LogDebug(
                    "Large-list read {Operation} took {ElapsedMs} ms with offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    "ingestion.batch.items",
                    sw.ElapsedMilliseconds,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }

            return Results.Ok(response);
        })
        .WithName("GetBatchItems")
        .WithSummary("List item-level ingestion progress for a batch.")
        .Produces<List<IngestionBatchItemResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);
        group.MapGet("/batches/{id:guid}", async (
            Guid id,
            IIngestionBatchResponseService batchResponses,
            CancellationToken ct) =>
        {
            var response = await batchResponses.GetByIdAsync(id, ct);
            return response is null ? ApiErrors.NotFound($"Batch '{id}' not found.") : Results.Ok(response);
        })
        .WithName("GetBatchById")
        .WithSummary("Get details of a specific ingestion batch.")
        .Produces<IngestionBatchResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        // ── POST /ingestion/upload ────────────────────────────────────────────────

        group.MapPost("/upload", async (
            HttpRequest request,
            IConfigurationLoader configLoader,
            IIngestionEngine engine,
            IOptions<IngestionOptions> opts,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            var mediaType = form["mediaType"].ToString();
            var destinationLibraryId = form["destinationLibraryId"].ToString();

            if (file is null || string.IsNullOrWhiteSpace(destinationLibraryId))
            {
                return ApiErrors.BadRequest("File and destinationLibraryId are required.");
            }

            if (file.Length <= 0)
            {
                return ApiErrors.BadRequest("Upload file must not be empty.");
            }

            var libraries = configLoader.LoadLibraries();
            var mediaTypes = configLoader.LoadMediaTypes();
            var library = libraries.Libraries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, destinationLibraryId.Trim(), StringComparison.OrdinalIgnoreCase));

            if (library is null)
            {
                return ApiErrors.BadRequest($"Destination library '{destinationLibraryId}' is not configured.");
            }

            if (library.Kind != LibraryKinds.Catalogued)
            {
                return ApiErrors.BadRequest("Personal media uploads use the profile-owned View upload endpoint.");
            }

            if (!library.AcceptedIntakeModes.Contains(LibraryIntakeModes.BrowserUpload, StringComparer.OrdinalIgnoreCase))
            {
                return ApiErrors.BadRequest($"Library '{library.Name}' does not accept browser uploads.");
            }

            var destination = library.PrimaryDestination;
            if (destination is null || !destination.AllowsFileMutation)
            {
                return ApiErrors.BadRequest($"Library '{library.Name}' has no managed, writable primary destination.");
            }

            if (string.IsNullOrWhiteSpace(mediaType)
                || !LibraryAcceptsMediaType(library, mediaType, mediaTypes.Types))
            {
                return ApiErrors.BadRequest(
                    $"A configured mediaType is required for catalogued library '{library.Name}'.");
            }

            var plan = UploadSafety.CreateDestinationPlan(
                destination.Path,
                mediaType,
                file.FileName,
                file.Length,
                mediaTypes.Types,
                opts.Value,
                allowPersonalFiles: false);

            if (plan.Error is not null)
            {
                return plan.Error;
            }

            Directory.CreateDirectory(plan.TargetDirectory);

            if (!UploadSafety.HasRequiredFreeSpace(plan.TargetDirectory, file.Length, opts.Value.UploadFreeSpaceBufferBytes))
            {
                return Results.Problem(
                    title: "Insufficient disk space",
                    detail: "The destination drive does not have enough free space for this upload and the configured safety buffer.",
                    statusCode: StatusCodes.Status507InsufficientStorage);
            }

            var tempPath = Path.Combine(
                plan.TargetDirectory,
                $".{Path.GetFileNameWithoutExtension(plan.SafeFileName)}.{Guid.NewGuid():N}.uploading");

            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    useAsync: true))
                {
                    await file.CopyToAsync(stream, ct);
                }

                await UploadSafety.FinalizeUploadAsync(
                    tempPath,
                    plan.TargetPath,
                    async (uploadedPath, indexCt) =>
                    {
                        await engine.EnqueueIntakeAsync(new IntakeFileRequest
                        {
                            Path = uploadedPath,
                            SourceKind = IntakeSourceKinds.BrowserUpload,
                            SourceId = destination.Id,
                            DestinationLibraryId = library.Id,
                        }, indexCt);
                    },
                    ct);
            }
            catch
            {
                TryDeleteTempUpload(tempPath);
                TryDeleteTempUpload(plan.TargetPath);
                throw;
            }

            return Results.Ok(new UploadMediaResponse(
                plan.TargetPath,
                plan.CanonicalMediaType,
                library.Id));
        })
        .WithName("UploadMedia")
        .WithSummary("Uploads to an explicit destination library and queues direct intake without rediscovery.")
        .DisableAntiforgery()
        .Produces<UploadMediaResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionRun);

        return app;
    }

    private static bool LibraryAcceptsMediaType(
        LibraryFolderConfig library,
        string mediaType,
        IReadOnlyList<MediaTypeDefinition> definitions)
    {
        var definition = definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, mediaType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, mediaType, StringComparison.OrdinalIgnoreCase));
        return definition is not null && library.MediaTypes.Any(configured =>
            string.Equals(configured, definition.Key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, definition.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<WatchFolderFileDto> GetNewestWatchFiles(
        string watchDir,
        SearchOption searchOption,
        int limit,
        CancellationToken ct)
    {
        var bounded = new List<WatchFolderFileDto>(Math.Max(1, limit));
        foreach (var fullPath in Directory.EnumerateFiles(watchDir, "*", searchOption))
        {
            ct.ThrowIfCancellationRequested();

            var info = new FileInfo(fullPath);
            bounded.Add(new WatchFolderFileDto
            {
                FileName = info.Name,
                RelativePath = Path.GetRelativePath(watchDir, fullPath),
                FileSizeBytes = info.Length,
                LastModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            });

            if (bounded.Count <= limit)
            {
                continue;
            }

            bounded.Sort(static (left, right) => right.LastModified.CompareTo(left.LastModified));
            bounded.RemoveRange(limit, bounded.Count - limit);
        }

        bounded.Sort(static (left, right) => right.LastModified.CompareTo(left.LastModified));
        return bounded;
    }

    private static void TryDeleteTempUpload(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup. The temp suffix prevents partial files being treated as complete uploads.
        }
    }
}

public static class UploadSafety
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> PersonalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".avif", ".tif", ".tiff",
        ".dng", ".arw", ".cr2", ".nef", ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi", ".wmv",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".odt",
        ".ods", ".odp", ".csv", ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".opus", ".aac", ".aiff",
    };

    public static async Task FinalizeUploadAsync(
        string tempPath,
        string targetPath,
        Func<string, CancellationToken, Task> index,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(index);

        var moved = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, targetPath);
            moved = true;
            await index(targetPath, ct);
        }
        catch
        {
            TryDeleteUpload(tempPath);
            if (moved)
            {
                TryDeleteUpload(targetPath);
            }

            throw;
        }
    }

    private static void TryDeleteUpload(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; callers receive the original indexing failure.
        }
    }

    public static UploadPlan CreatePlan(
        string watchRoot,
        string mediaType,
        string fileName,
        long fileLength,
        IReadOnlyList<MediaEngine.Domain.Configuration.MediaTypeDefinition> mediaTypes,
        IngestionOptions options)
    {
        if (string.IsNullOrWhiteSpace(watchRoot))
        {
            return UploadPlan.Fail(ApiErrors.BadRequest("No watch folder configured."));
        }

        if (fileLength <= 0)
        {
            return UploadPlan.Fail(ApiErrors.BadRequest("Upload file must not be empty."));
        }

        if (fileLength > options.MaxUploadSizeBytes)
        {
            return UploadPlan.Fail(Results.Problem(
                title: "Upload too large",
                detail: $"The upload is {fileLength} bytes, which exceeds the configured limit of {options.MaxUploadSizeBytes} bytes.",
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        var definition = ResolveMediaType(mediaType, mediaTypes);
        if (definition is null)
        {
            return UploadPlan.Fail(ApiErrors.BadRequest($"Unsupported media type: {mediaType}"));
        }

        if (!IsSafeFileName(fileName, out var safeFileName))
        {
            return UploadPlan.Fail(ApiErrors.BadRequest("Invalid filename."));
        }

        var extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(extension)
            || !definition.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return UploadPlan.Fail(ApiErrors.BadRequest(
                $"Files with extension '{extension}' are not allowed for {definition.DisplayName}."));
        }

        var targetDir = Path.GetFullPath(Path.Combine(watchRoot, definition.DisplayName));
        var watchRootFull = Path.GetFullPath(watchRoot);
        var rootPrefix = watchRootFull.EndsWith(Path.DirectorySeparatorChar)
            ? watchRootFull
            : watchRootFull + Path.DirectorySeparatorChar;
        if (!targetDir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return UploadPlan.Fail(ApiErrors.BadRequest("Invalid media type destination."));
        }

        var targetPath = ResolveCollisionPath(targetDir, safeFileName);
        return new UploadPlan(
            true,
            null,
            targetDir,
            targetPath,
            safeFileName,
            definition.DisplayName);
    }

    public static UploadPlan CreateDestinationPlan(
        string destinationRoot,
        string? mediaType,
        string fileName,
        long fileLength,
        IReadOnlyList<MediaEngine.Domain.Configuration.MediaTypeDefinition> mediaTypes,
        IngestionOptions options,
        bool allowPersonalFiles)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return UploadPlan.Fail(ApiErrors.BadRequest("No destination folder configured."));
        }

        if (fileLength <= 0)
        {
            return UploadPlan.Fail(ApiErrors.BadRequest("Upload file must not be empty."));
        }

        if (fileLength > options.MaxUploadSizeBytes)
        {
            return UploadPlan.Fail(Results.Problem(
                title: "Upload too large",
                detail: $"The upload is {fileLength} bytes, which exceeds the configured limit of {options.MaxUploadSizeBytes} bytes.",
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        if (!IsSafeFileName(fileName, out var safeFileName))
        {
            return UploadPlan.Fail(ApiErrors.BadRequest("Invalid filename."));
        }

        var extension = Path.GetExtension(safeFileName);
        var definition = ResolveMediaType(mediaType ?? string.Empty, mediaTypes);
        if (allowPersonalFiles)
        {
            var configuredExtension = mediaTypes.Any(type =>
                type.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(extension)
                || (!configuredExtension && !PersonalExtensions.Contains(extension)))
            {
                return UploadPlan.Fail(ApiErrors.BadRequest(
                    $"Files with extension '{extension}' are not supported for personal libraries."));
            }
        }
        else if (definition is null
                 || string.IsNullOrWhiteSpace(extension)
                 || !definition.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return UploadPlan.Fail(ApiErrors.BadRequest(
                definition is null
                    ? $"Unsupported media type: {mediaType}"
                    : $"Files with extension '{extension}' are not allowed for {definition.DisplayName}."));
        }

        var targetDirectory = Path.GetFullPath(destinationRoot);
        var targetPath = ResolveCollisionPath(targetDirectory, safeFileName);
        return new UploadPlan(
            true,
            null,
            targetDirectory,
            targetPath,
            safeFileName,
            definition?.DisplayName ?? "Personal");
    }

    public static bool HasRequiredFreeSpace(string targetDirectory, long fileLength, long freeSpaceBufferBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(targetDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace >= fileLength + Math.Max(0, freeSpaceBufferBytes);
    }

    private static MediaEngine.Domain.Configuration.MediaTypeDefinition? ResolveMediaType(
        string mediaType,
        IReadOnlyList<MediaEngine.Domain.Configuration.MediaTypeDefinition> mediaTypes)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        return mediaTypes.FirstOrDefault(t =>
            string.Equals(t.DisplayName, mediaType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Key, mediaType, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeFileName(string fileName, out string safeFileName)
    {
        safeFileName = Path.GetFileName(fileName);
        return !string.IsNullOrWhiteSpace(fileName)
               && string.Equals(fileName, safeFileName, StringComparison.Ordinal)
               && !safeFileName.Any(c => InvalidFileNameChars.Contains(c));
    }

    private static string ResolveCollisionPath(string targetDir, string safeFileName)
    {
        var targetPath = Path.Combine(targetDir, safeFileName);
        var counter = 1;
        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var ext = Path.GetExtension(safeFileName);
        while (File.Exists(targetPath))
        {
            targetPath = Path.Combine(targetDir, $"{baseName} ({counter}){ext}");
            counter++;
        }

        return targetPath;
    }

}

internal static class IngestionBatchEndpointMapper
{
    internal static bool ShouldShowInRecentBatches(IngestionBatch batch)
    {
        var hasOutcome = batch.FilesIdentified > 0
            || batch.FilesReview > 0
            || batch.FilesNoMatch > 0
            || batch.FilesFailed > 0;

        return hasOutcome || !string.Equals(batch.Status, "completed", StringComparison.OrdinalIgnoreCase);
    }

}

public sealed record UploadPlan(
    bool IsValid,
    IResult? Error,
    string TargetDirectory,
    string TargetPath,
    string SafeFileName,
    string CanonicalMediaType)
{
    public static UploadPlan Fail(IResult error) => new(false, error, string.Empty, string.Empty, string.Empty, string.Empty);
}
