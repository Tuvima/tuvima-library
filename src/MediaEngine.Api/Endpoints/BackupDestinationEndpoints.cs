using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Contracts.Backups;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Backup destinations (Developer tools spec WP25): the schedule and "keep the last N", the backup passphrase, where backups are
/// copied, a backup run on demand, and restoring from a destination. The passphrase is only ever used within one request.
/// </summary>
public static class BackupDestinationEndpoints
{
    internal static IEndpointRouteBuilder MapBackupDestinationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/backup").WithTags("System");

        group.MapGet("/settings", async ([FromServices] BackupSettingsManager manager, CancellationToken ct) =>
            Results.Ok(await manager.GetAsync(ct).ConfigureAwait(false)))
        .WithName("GetBackupSettings")
        .WithSummary("The backup schedule, how many backups are kept, whether a passphrase is set, and each destination with its state and last checked copy.")
        .Produces<BackupSettingsResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRead);

        group.MapPut("/settings", async (UpdateBackupSettingsRequest request, [FromServices] BackupSettingsManager manager, CancellationToken ct) =>
        {
            var problem = await manager.UpdateAsync(request, ct).ConfigureAwait(false);
            return problem is null
                ? Results.Ok(await manager.GetAsync(ct).ConfigureAwait(false))
                : ApiErrors.BadRequest(problem);
        })
        .WithName("UpdateBackupSettings")
        .WithSummary("Changes the backup schedule, retention or which destinations receive each backup. Parts left out keep their value.")
        .Produces<BackupSettingsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        group.MapPost("/destinations", async (SaveBackupDestinationRequest request, [FromServices] BackupSettingsManager manager, CancellationToken ct) =>
        {
            var problem = await manager.SaveDestinationAsync(request, ct).ConfigureAwait(false);
            return problem is null
                ? Results.Ok(await manager.GetAsync(ct).ConfigureAwait(false))
                : ApiErrors.BadRequest(problem);
        })
        .WithName("SaveBackupDestination")
        .WithSummary("Adds a backup destination (another folder, a network share, S3-compatible storage or WebDAV), or changes one when an id is given. A web destination needs the host to be confirmed; its login is stored encrypted and never returned.")
        .Produces<BackupSettingsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministratorInPerson();

        group.MapDelete("/destinations/{destinationId}", async (string destinationId, [FromServices] BackupSettingsManager manager, CancellationToken ct) =>
            await manager.RemoveDestinationAsync(destinationId, ct).ConfigureAwait(false)
                ? Results.Ok(await manager.GetAsync(ct).ConfigureAwait(false))
                : ApiErrors.NotFound("That destination was not found."))
        .WithName("RemoveBackupDestination")
        .WithSummary("Stops using a destination that was added and forgets its login. Copies already there are left alone.")
        .Produces<BackupSettingsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministratorInPerson();

        group.MapPost("/passphrase", async (SetBackupPassphraseRequest request, [FromServices] BackupSettingsManager manager, CancellationToken ct) =>
        {
            var problem = await manager.SetPassphraseAsync(request, ct).ConfigureAwait(false);
            return problem is null
                ? Results.Ok(await manager.GetAsync(ct).ConfigureAwait(false))
                : ApiErrors.BadRequest(problem);
        })
        .WithName("SetBackupPassphrase")
        .WithSummary("Sets or changes the backup passphrase. The passphrase is never stored; losing it means encrypted backups cannot be restored. Changing it needs the current one.")
        .Produces<BackupSettingsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministratorInPerson();

        group.MapPost("/run", async ([FromServices] BackupOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await orchestrator.RunAsync(ct).ConfigureAwait(false));
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }
        })
        .WithName("RunBackupNow")
        .WithSummary("Makes a backup now and copies it to every switched-on destination, checking each copy.")
        .Produces<BackupRunResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRun);

        group.MapPost("/destinations/{destinationId}/test", async (string destinationId, [FromServices] BackupOrchestrator orchestrator, CancellationToken ct) =>
            orchestrator.Find(destinationId) is { } destination
                ? Results.Ok(await orchestrator.TestAsync(destination, ct).ConfigureAwait(false))
                : ApiErrors.NotFound("That destination was not found."))
        .WithName("TestBackupDestination")
        .WithSummary("Checks that a destination can be reached and written to.")
        .Produces<BackupDestinationTestResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRun);

        group.MapGet("/destinations/{destinationId}/backups", async (string destinationId, [FromServices] BackupOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (orchestrator.Find(destinationId) is not { } destination)
            {
                return ApiErrors.NotFound("That destination was not found.");
            }

            try
            {
                var held = await destination.ListAsync(ct).ConfigureAwait(false);
                return Results.Ok(new DestinationBackupsResponse
                {
                    Items = [.. held
                        .Where(item => item.Name.StartsWith("tuvima-backup-", StringComparison.Ordinal)
                            && (!destination.LeavesMachine || item.Name.EndsWith(BackupEncryption.FileExtension, StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(item => item.ModifiedAt)
                        .Select(item => new DestinationBackupDto
                        {
                            Name = item.Name,
                            SizeBytes = item.SizeBytes,
                            ModifiedAt = item.ModifiedAt,
                            Encrypted = item.Name.EndsWith(BackupEncryption.FileExtension, StringComparison.OrdinalIgnoreCase),
                        })],
                });
            }
            catch (BackupDestinationException ex)
            {
                return ApiErrors.Problem(StatusCodes.Status502BadGateway, "Destination unavailable", ex.Message);
            }
        })
        .WithName("ListBackupDestinationBackups")
        .WithSummary("The backups a destination holds, newest first, marking which are encrypted.")
        .Produces<DestinationBackupsResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRead);

        group.MapPost("/restore-from-destination", async (RestoreFromDestinationRequest request, [FromServices] BackupOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (orchestrator.Find(request.DestinationId) is not { } destination)
            {
                return ApiErrors.NotFound("That destination was not found.");
            }

            try
            {
                return Results.Ok(await orchestrator.RestoreFromDestinationAsync(destination, request.Name, request.Passphrase, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is BackupPassphraseException or InvalidDataException or InvalidOperationException or BackupDestinationException or FileNotFoundException or ArgumentException)
            {
                return ApiErrors.BadRequest(ex is FileNotFoundException ? "That backup was not found at the destination." : ex.Message);
            }
        })
        .WithName("RestoreBackupFromDestination")
        .WithSummary("Fetches a backup from a destination, opens it with the passphrase when it is encrypted, and stages it for restore at the next start. A wrong passphrase is refused with a clear message.")
        .Produces<ScheduleRestoreResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        return routes;
    }
}
