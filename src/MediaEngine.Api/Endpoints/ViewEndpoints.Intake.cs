using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.LocalAssets.Intake;
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
