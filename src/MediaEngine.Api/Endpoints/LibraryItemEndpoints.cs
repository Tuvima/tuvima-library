using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Services;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Unified curator-facing view of ingested media items. Endpoint handlers own HTTP
/// mapping while persistence is delegated to typed repositories and curation services.
/// </summary>
public static class LibraryItemEndpoints
{
    public static IEndpointRouteBuilder MapLibraryItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/items")
            .WithTags("LibraryItems");

        group.MapGet("/{entityId}/detail", async (
            Guid entityId,
            ILibraryItemRepository repo,
            CancellationToken ct) =>
        {
            var detail = await repo.GetDetailAsync(entityId, ct);
            return detail is null
                ? ApiErrors.NotFound($"Library item '{entityId}' not found.")
                : Results.Ok(detail.ToContract());
        })
        .WithName("GetLibraryItemDetail")
        .WithSummary("Full detail for a single library item.")
        .Produces<LibraryItemDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.ReviewRead);

        group.MapPost("/batch/delete", async (
            BatchLibraryItemRequest request,
            ILibraryItemCurationRepository store,
            WorkHierarchyMaintenanceService hierarchyMaintenance,
            ISystemActivityRepository activityRepo,
            ILogger<LibraryItemEndpointLog> logger,
            CancellationToken ct) =>
        {
            var entityIds = DistinctEntityIds(request);
            if (entityIds.Length == 0)
            {
                return ApiErrors.BadRequest("No entity IDs provided.");
            }

            var targets = await store.GetRemovalTargetsAsync(entityIds, ct);
            var processed = 0;
            var filesDeleted = 0;
            foreach (var entityId in entityIds)
            {
                if (!targets.TryGetValue(entityId, out var target) || target.FilePaths.Count == 0)
                {
                    continue;
                }

                try
                {
                    filesDeleted += await DeleteTargetAsync(
                        target, store, hierarchyMaintenance, activityRepo, logger, ct);
                    processed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Batch delete failed for work {WorkId}", entityId);
                }
            }

            return Results.Ok(new BatchLibraryItemResponse
            {
                ProcessedCount = processed,
                TotalRequested = entityIds.Length,
                Message = $"Deleted {processed} of {entityIds.Length} items ({filesDeleted} files removed).",
            });
        })
        .WithName("BatchDeleteLibraryCatalogItems")
        .WithSummary("Permanently delete multiple library items and their files in batch.")
        .Produces<BatchLibraryItemResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        group.MapGet("/{entityId:guid}/history", async (
            Guid entityId,
            ILibraryItemCurationRepository store,
            CancellationToken ct) =>
            Results.Ok((await store.GetHistoryAsync(entityId, ct)).Select(item => item.ToContract()).ToList()))
        .WithName("GetLibraryCatalogItemHistory")
        .WithSummary("Get processing history timeline for a library item.")
        .Produces<IReadOnlyList<LibraryItemHistoryDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.ReviewRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.ReviewRead);

        return app;
    }

    private static Guid[] DistinctEntityIds(BatchLibraryItemRequest request) =>
        request.EntityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

    private static async Task<int> DeleteTargetAsync(
        LibraryItemRemovalTarget target,
        ILibraryItemCurationRepository store,
        WorkHierarchyMaintenanceService hierarchyMaintenance,
        ISystemActivityRepository activityRepo,
        ILogger logger,
        CancellationToken ct)
    {
        var filesDeleted = 0;
        foreach (var filePath in target.FilePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    filesDeleted++;
                }

                TryPruneEmptyDirectory(Path.GetDirectoryName(filePath), logger);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete media file {FilePath}; database cleanup will continue", filePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Access denied while deleting media file {FilePath}; database cleanup will continue", filePath);
            }
        }

        foreach (var path in target.ManagedAssetPaths)
        {
            TryDeleteManagedAssetFile(path, logger);
        }

        await store.DeleteWorkRecordsAsync(target, ct);

        try
        {
            await hierarchyMaintenance.CleanupEmptyParentsAsync(target.ParentWorkId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Work {WorkId} was deleted, but empty parent cleanup failed", target.WorkId);
        }

        await LogSupplementaryActivityAsync(activityRepo, new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.MediaRemoved,
            CollectionName = target.Title,
            EntityId = target.WorkId,
            EntityType = "Work",
            Detail = $"Batch removed '{target.Title ?? "unknown"}' - {filesDeleted} file(s) deleted.",
        }, logger, ct);

        return filesDeleted;
    }

    private static async Task LogSupplementaryActivityAsync(
        ISystemActivityRepository activityRepo,
        SystemActivityEntry entry,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await activityRepo.LogAsync(entry, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Primary library item operation succeeded, but activity logging failed");
        }
    }

    private static void TryPruneEmptyDirectory(string? directory, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not prune empty media directory {Directory}", directory);
        }
    }

    private static void TryDeleteManagedAssetFile(string path, ILogger logger)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!IsManagedAssetPath(fullPath))
            {
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            PruneEmptyManagedAssetParents(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(ex, "Managed artwork cleanup failed for {Path}", path);
        }
    }

    private static bool IsManagedAssetPath(string fullPath) =>
        fullPath.Replace('\\', '/').Contains("/.data/assets/", StringComparison.OrdinalIgnoreCase);

    private static void PruneEmptyManagedAssetParents(string fullPath)
    {
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrWhiteSpace(current)
               && IsManagedAssetPath(current)
               && !string.Equals(Path.GetFileName(current), "assets", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
            {
                return;
            }

            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }
}

public sealed class LibraryItemEndpointLog;
