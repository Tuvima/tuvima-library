using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Import;
using MediaEngine.Contracts.Import;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The shared Import (Collections and lists spec D17): one place that recognises a file and answers in that kind's own words.
/// Reachable from New collection, the player's bookmark pack import and Settings > Overview > Import. Files are read in memory and
/// never leave this machine.
/// </summary>
public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/import").WithTags("Import");

        group.MapPost("/recognise", async (
            ImportRecogniseRequest body,
            [FromServices] ImportRecogniser recogniser,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (!ListUpload.TryDecode(body.FileName, body.ContentBase64, body.Text, out var bytes, out var error))
            {
                return ApiErrors.BadRequest(error!);
            }

            var allowed = await display.LoadVisibleWorkIdsAsync(ct);
            var preview = await recogniser.RecogniseAsync(
                new ImportSource(body.FileName ?? string.Empty, bytes!), new ImportContext(null, allowed), ct);
            return Results.Ok(preview);
        })
        .WithName("RecogniseImport")
        .WithSummary("Recognises a file and returns the preview for its kind. Nothing is saved.")
        .Produces<ImportPreviewDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        // Only kinds that can be applied from the dialog on their own come through here (today: plugin settings). A list becomes
        // a collection through New collection, and a bookmark pack through the title's player.
        group.MapPost("/apply", async (
            ImportApplyRequest body,
            [FromServices] ImportRecogniser recogniser,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (!ListUpload.TryDecode(body.FileName, body.ContentBase64, body.Text, out var bytes, out var error))
            {
                return ApiErrors.BadRequest(error!);
            }

            var allowed = await display.LoadVisibleWorkIdsAsync(ct);
            var result = await recogniser.ApplyAsync(
                body.Kind, new ImportSource(body.FileName ?? string.Empty, bytes!), new ImportContext(null, allowed), ct);
            return Results.Ok(result);
        })
        .WithName("ApplyImport")
        .WithSummary("Applies a recognised file as the kind that was previewed, where that kind can be applied on its own.")
        .Produces<ImportApplyResultDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        return app;
    }
}
