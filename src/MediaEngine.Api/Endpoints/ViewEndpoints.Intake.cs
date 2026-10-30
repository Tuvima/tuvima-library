using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets.Intake;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Import and Rejected areas as an administrator sees them on a View kind's page (Photo and video filing spec D1, D6, D8, D9, D19):
/// what is waiting, what was refused and why, Scan now, removing a refused file early, the layout photos are filed by, and the kind's
/// Activity. Filing itself runs on its own; none of these routes moves a file into a library except Scan now, which runs the same filer.
/// </summary>
public static partial class ViewEndpoints
{
    private static void MapIntake(RouteGroupBuilder group)
    {
        group.MapGet("/admin/intake/layout", ([FromServices] ViewIntakeAdminService service) => Results.Ok(service.GetLayout()))
            .WithName("GetViewFilingLayout")
            .WithSummary("The folders photos and videos are filed into, with an example.")
            .Produces<ViewFilingLayoutDto>()
            .RequireEffectiveAdministrator();

        group.MapPut("/admin/intake/layout", (UpdateViewFilingLayoutRequest request, [FromServices] ViewIntakeAdminService service) =>
        {
            try
            {
                return Results.Ok(service.SetLayout(request.Pattern));
            }
            catch (ArgumentException exception)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("UpdateViewFilingLayout")
            .WithSummary("Change the folders photos and videos are filed into from now on. Files already filed stay where they are.")
            .Produces<ViewFilingLayoutDto>()
            .RequireEffectiveAdministrator();

        group.MapGet("/admin/missing-files", ([FromServices] ViewIntakeAdminService service) => Results.Ok(service.GetMissingFileSettings()))
            .WithName("GetViewMissingFileSettings")
            .WithSummary("How many days Library keeps what it knows about a file deleted outside Library (0 means never remove).")
            .Produces<ViewMissingFileSettingsDto>()
            .RequireEffectiveAdministrator();

        group.MapPut("/admin/missing-files", (UpdateViewMissingFileSettingsRequest request, [FromServices] ViewIntakeAdminService service) =>
        {
            try
            {
                return Results.Ok(service.SetMissingFileSettings(request.RetentionDays));
            }
            catch (ArgumentException exception)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("UpdateViewMissingFileSettings")
            .WithSummary("Change how long records of missing files are kept. Library never deletes a file because it went missing.")
            .Produces<ViewMissingFileSettingsDto>()
            .RequireEffectiveAdministrator();

        group.MapPost("/admin/intake/scan", async ([FromServices] ViewIntakeAdminService service, CancellationToken ct) =>
            Results.Ok(await service.ScanNowAsync(ct)))
            .WithName("ScanViewImportArea")
            .WithSummary("Files whatever has settled in the Import area right now.")
            .Produces<ViewIntakeScanResultDto>()
            .RequireEffectiveAdministrator();

        group.MapDelete("/admin/intake/rejected/{id:guid}", async (Guid id, [FromServices] ViewIntakeAdminService service, CancellationToken ct) =>
            await service.RemoveRejectedAsync(id, ct)
                ? Results.NoContent()
                : ApiErrors.NotFound("That refused file is no longer in the Rejected area."))
            .WithName("RemoveViewRejectedFile")
            .WithSummary("Removes one refused file from the Rejected area now, instead of waiting out its time.")
            .Produces(StatusCodes.Status204NoContent)
            .RequireEffectiveAdministrator();

        group.MapGet("/admin/tidy/{sourceId:guid}", async (Guid sourceId, IViewRequestProfileContext identity,
            [FromServices] ViewTidyService service, CancellationToken ct) =>
            await service.GetStatusAsync(sourceId, ct, (await identity.ResolveAuthorityAsync(ct)).ActiveProfileId) is { } status
                ? Results.Ok(status)
                : ApiErrors.NotFound("Only a managed photos or personal videos folder of yours can be tidied."))
            .WithName("GetViewTidyStatus")
            .WithSummary("What Tidy this folder would do to a managed folder, or what the current or last run did.")
            .Produces<ViewTidyStatusDto>()
            .RequireEffectiveAdministrator();

        group.MapPost("/admin/tidy/{sourceId:guid}/run", async (Guid sourceId, IViewRequestProfileContext identity,
            [FromServices] ViewTidyService service, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            try
            {
                return await service.StartAsync(sourceId, authority.ActiveProfileId, "now", ct, checkOwner: true) is { } status
                    ? Results.Ok(status)
                    : ApiErrors.NotFound("Only a managed photos or personal videos folder can be tidied.");
            }
            catch (InvalidOperationException exception)
            {
                return ApiErrors.Conflict(exception.Message);
            }
        }).WithName("RunViewTidy")
            .WithSummary("Starts Tidy this folder now. The folder's library is unavailable for new files until it finishes.")
            .Produces<ViewTidyStatusDto>()
            .RequireEffectiveAdministrator();

        group.MapPut("/admin/tidy/{sourceId:guid}/schedule", async (Guid sourceId, ViewTidyScheduleRequest request,
            IViewRequestProfileContext identity, [FromServices] ViewTidyService service, CancellationToken ct) =>
            await service.SetScheduledAsync(sourceId, request.Scheduled, (await identity.ResolveAuthorityAsync(ct)).ActiveProfileId, ct) is { } status
                ? Results.Ok(status)
                : ApiErrors.NotFound("Only a managed photos or personal videos folder can be tidied."))
            .WithName("ScheduleViewTidy")
            .WithSummary("Turns the nightly Tidy of a managed folder on or off.")
            .Produces<ViewTidyStatusDto>()
            .RequireEffectiveAdministrator();

        group.MapGet("/admin/duplicates", async (IViewRequestProfileContext identity, [FromServices] ViewDuplicatesService service, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            return authority.ActiveProfileId is { } profile
                ? Results.Ok(await service.GetReportAsync(profile, ct))
                : ApiErrors.Forbidden("Sign in as a person to see the duplicates report.");
        }).WithName("GetViewDuplicatesReport")
            .WithSummary("Exact copies already in the Shared Library or the administrator's own space, as the cleanup job last found them.")
            .Produces<ViewDuplicateReportDto>()
            .RequireEffectiveAdministrator();

        group.MapPost("/admin/duplicates/scan", async (IViewRequestProfileContext identity, [FromServices] ViewDuplicatesService service, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profile)
            {
                return ApiErrors.Forbidden("Sign in as a person to look for duplicates.");
            }

            await service.ScanAsync(ct);
            return Results.Ok(await service.GetReportAsync(profile, ct));
        }).WithName("ScanViewDuplicates")
            .WithSummary("Runs the duplicates cleanup now. It only updates the report; nothing is removed.")
            .Produces<ViewDuplicateReportDto>()
            .RequireEffectiveAdministrator();

        group.MapPost("/admin/duplicates/{id:guid}/keep", async (Guid id, ViewDuplicateKeepRequest request, IViewRequestProfileContext identity,
            [FromServices] ViewDuplicatesService service, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profile)
            {
                return ApiErrors.Forbidden("Sign in as a person to tidy duplicates.");
            }

            try
            {
                var result = await service.KeepOneAsync(profile, id, request.KeepItemId, ct);
                return result is null ? ApiErrors.NotFound("That group of copies is no longer there.") : Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("KeepOneViewDuplicate")
            .WithSummary("Keeps one copy from a group of exact duplicates and moves the others to Trash.")
            .Produces<ViewDuplicateKeepResultDto>()
            .RequireEffectiveAdministrator();

        group.MapPost("/admin/duplicates/{id:guid}/dismiss", async (Guid id, IViewRequestProfileContext identity,
            [FromServices] ViewDuplicatesService service, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            return authority.ActiveProfileId is { } profile && await service.DismissAsync(profile, id, ct)
                ? Results.NoContent()
                : ApiErrors.NotFound("That group of copies is no longer there.");
        }).WithName("DismissViewDuplicate")
            .WithSummary("Leaves a group of copies as it is; it comes back only if more copies turn up.")
            .Produces(StatusCodes.Status204NoContent)
            .RequireEffectiveAdministrator();

        group.MapGet("/admin/intake/{kind}", async (string kind, [FromServices] ViewIntakeAdminService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.GetSummaryAsync(kind, ct));
            }
            catch (ArgumentException exception)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("GetViewIntakeSummary")
            .WithSummary("What is waiting in, and refused from, the Import area for photos or personal videos.")
            .Produces<ViewIntakeSummaryDto>()
            .RequireEffectiveAdministrator();

        group.MapGet("/admin/intake/{kind}/activity", async (string kind, int? take, [FromServices] ViewIntakeAdminService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.GetActivityAsync(kind, take ?? 100, ct));
            }
            catch (ArgumentException exception)
            {
                return ApiErrors.BadRequest(exception.Message);
            }
        }).WithName("GetViewKindActivity")
            .WithSummary("What was added, skipped and refused for photos or personal videos, newest first.")
            .Produces<ViewActivityDto>()
            .RequireEffectiveAdministrator();
    }
}
