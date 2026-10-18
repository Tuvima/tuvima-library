using System.Reflection;
using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Operations;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;
using SystemStatusResponse = MediaEngine.Contracts.System.SystemStatusResponse;

namespace MediaEngine.Api.Endpoints;

public static class SystemEndpoints
{
    // Version sourced from the assembly at startup — no hard-coded string to forget to bump.
    private static readonly string AppVersion =
        typeof(SystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0]           // strip build metadata (e.g. git hash)
        ?? "1.0.0";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // No auth required — allows external apps to verify the URL is reachable.
        app.MapGet("/system/status", ([FromServices] IConfigurationLoader configLoader) =>
        {
            var core = configLoader.LoadCore();
            return Results.Ok(new SystemStatusResponse
            {
                Status = "ok",
                Version = AppVersion,
                Language = core?.Language.Metadata ?? "en",
            });
        })
        .WithTags("System")
        .WithName("GetSystemStatus")
        .WithSummary("Returns service health and version. Used by external apps to test connectivity.")
        .Produces<SystemStatusResponse>(StatusCodes.Status200OK)
        .AllowAnonymous();

        app.MapGet("/system/readiness", async (
            StartupReadinessService readiness,
            CancellationToken ct) => Results.Ok(await readiness.GetAsync(ct).ConfigureAwait(false)))
        .WithTags("System")
        .WithName("GetStartupReadiness")
        .WithSummary("Reports database, configuration, storage, model, provider, and worker readiness.")
        .Produces<StartupReadinessResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.SystemStatusRead);

        app.MapGet("/system/activity-status", async (
            IMediaOperationRepository operations,
            CancellationToken ct) =>
        {
            var queue = await operations.GetQueueAsync(null, 200, ct);
            var active = queue
                .Where(operation => operation.Status.Equals(MediaOperationStatus.Leased, StringComparison.OrdinalIgnoreCase)
                                    || operation.Status.Equals(MediaOperationStatus.Running, StringComparison.OrdinalIgnoreCase))
                .OrderBy(operation => operation.Priority)
                .ThenByDescending(operation => operation.UpdatedAt)
                .Take(50)
                .Select(ToSystemActivityOperation)
                .ToList();

            return Results.Ok(active);
        })
        .WithTags("System")
        .WithName("GetSystemActivityStatus")
        .WithSummary("Returns sanitized active Engine operations for the Dashboard activity indicator.")
        .Produces<List<SystemActivityOperationDto>>(StatusCodes.Status200OK)
        .RequireHumanOrApplicationPermission(ApplicationPermissionIds.SystemActivityRead);

        app.MapGet("/system/watcher-status", (IFileWatcher watcher) =>
            Results.Ok(new FileWatcherStatusResponse(
                watcher.IsRunning,
                watcher.WatchedPaths.Count,
                watcher.WatchedPaths,
                watcher.EventCount,
                watcher.LastEventAt,
                watcher.ErrorCount,
                watcher.LastErrorAt,
                watcher.LastErrorKind,
                watcher.LastErrorMessage)))
        .WithTags("System")
        .WithName("GetWatcherStatus")
        .WithSummary("Returns file watcher diagnostic status.")
        .Produces<FileWatcherStatusResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        app.MapPost("/maintenance/sweep-orphan-assets", (
            AssetStoreCleanupService cleanupService,
            CancellationToken ct) =>
        {
            var result = cleanupService.SweepOrphanAssets(ct);
            return Results.Ok(new AssetStoreSweepResponse(result.Cleaned, result.Message));
        })
        .WithTags("System")
        .WithName("SweepOrphanAssets")
        .WithSummary("Scans .data/assets for managed files not referenced by the database and removes them.")
        .Produces<AssetStoreSweepResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.StorageConfigWrite);

        app.MapGet("/system/backups", (DatabaseBackupService backups) =>
            Results.Ok(backups.List()))
        .WithTags("System")
        .WithName("ListBackups")
        .WithSummary("Lists server-side database and configuration backup archives.")
        .Produces<IReadOnlyList<BackupArchiveDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRead);

        app.MapGet("/system/data-store-version", (DataStoreVersionRepository versions) =>
        {
            var current = versions.GetCurrent();
            return Results.Ok(current is null
                ? new DataStoreVersionDto(0, string.Empty, null)
                : new DataStoreVersionDto(current.Number, current.Name, current.AppliedAt));
        })
        .WithTags("System")
        .WithName("GetDataStoreVersion")
        .WithSummary("Reports the latest upgrade step applied to the data store and when it ran; step 0 means none is recorded yet.")
        .Produces<DataStoreVersionDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRead);

        app.MapPost("/system/backups", async (
            DatabaseBackupService backups,
            CancellationToken ct) =>
        {
            var path = await backups.CreateAsync(ct).ConfigureAwait(false);
            return Results.Ok(new BackupArchiveDto(
                Path.GetFileName(path),
                File.GetCreationTimeUtc(path),
                new FileInfo(path).Length));
        })
        .WithTags("System")
        .WithName("CreateBackup")
        .WithSummary("Creates a consistent SQLite and non-secret configuration backup.")
        .Produces<BackupArchiveDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRun);

        app.MapGet("/system/backups/{fileName}", (string fileName, DatabaseBackupService backups) =>
        {
            try
            {
                var path = backups.ResolveArchive(fileName);
                return (IResult)Results.File(path, "application/zip", Path.GetFileName(path));
            }
            catch (ArgumentException ex)
            {
                return (IResult)ApiErrors.BadRequest(ex.Message);
            }
            catch (FileNotFoundException)
            {
                return (IResult)ApiErrors.NotFound("Backup archive was not found.");
            }
        })
        .WithTags("System")
        .WithName("DownloadBackup")
        .WithSummary("Downloads a previously created backup archive.")
        .Produces(StatusCodes.Status200OK, contentType: "application/zip")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRead);

        app.MapPost("/system/backups/validate", (ScheduleRestoreRequest request, DatabaseBackupService backups) =>
        {
            try
            {
                return (IResult)Results.Ok(backups.ValidateRestore(request.FileName));
            }
            catch (ArgumentException ex)
            {
                return (IResult)ApiErrors.BadRequest(ex.Message);
            }
            catch (FileNotFoundException)
            {
                return (IResult)ApiErrors.NotFound("Backup archive was not found.");
            }
            catch (InvalidDataException ex)
            {
                return (IResult)ApiErrors.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return (IResult)ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithTags("System")
        .WithName("ValidateBackupRestore")
        .WithSummary("Runs a non-destructive restore drill against a backup archive.")
        .Produces<RestoreValidationResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRead);

        app.MapPost("/system/backups/restore", (ScheduleRestoreRequest request, DatabaseBackupService backups) =>
        {
            try
            {
                return (IResult)Results.Ok(backups.ScheduleRestore(request.FileName));
            }
            catch (ArgumentException ex)
            {
                return (IResult)ApiErrors.BadRequest(ex.Message);
            }
            catch (FileNotFoundException)
            {
                return (IResult)ApiErrors.NotFound("Backup archive was not found.");
            }
            catch (InvalidDataException ex)
            {
                return (IResult)ApiErrors.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return (IResult)ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithTags("System")
        .WithName("ScheduleBackupRestore")
        .WithSummary("Validates and stages a backup for application on the next Engine restart.")
        .Produces<ScheduleRestoreResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRestore);

        return app;
    }

    private static SystemActivityOperationDto ToSystemActivityOperation(MediaOperation operation) => new()
    {
        Id = operation.Id,
        OperationType = operation.OperationType,
        OperationKind = operation.OperationKind,
        Status = operation.Status,
        Stage = operation.Stage,
        ProgressPercent = operation.ProgressPercent,
        ItemsTotal = operation.ItemsTotal,
        ItemsCompleted = operation.ItemsCompleted,
        UpdatedAt = operation.UpdatedAt,
    };
}
