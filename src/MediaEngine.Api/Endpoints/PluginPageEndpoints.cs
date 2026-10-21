using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins.Pages;
using MediaEngine.Contracts.Plugins;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The pages and menu entries plugins add (WP16). These are for people using the Library, not for managing plugins:
/// any signed-in person may ask, and each answer is cut down to the pages that person's audience allows.
/// </summary>
internal static class PluginPageEndpoints
{
    internal static RouteGroupBuilder MapPluginPageEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/plugin-pages")
            .WithTags("Plugin pages")
            .RequireHumanSelfService();

        group.MapGet("/menu", async (
            HttpContext http,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            [FromServices] PluginPageService pages,
            CancellationToken ct) =>
            Results.Ok(pages.Menu(await ViewerAsync(http, authorityResolver, ct).ConfigureAwait(false))))
            .WithName("GetPluginMenu")
            .Produces<PluginMenuResponse>(StatusCodes.Status200OK);

        group.MapGet("/{pluginId}/{pageKey}", async (
            string pluginId,
            string pageKey,
            HttpContext http,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            [FromServices] PluginPageService pages,
            CancellationToken ct) =>
        {
            var page = await pages.GetAsync(await ViewerAsync(http, authorityResolver, ct).ConfigureAwait(false), pluginId, pageKey, ct).ConfigureAwait(false);
            return page is null ? ApiErrors.NotFound("That page does not exist.") : Results.Ok(page);
        })
            .WithName("GetPluginPage")
            .Produces<PluginPageResponse>(StatusCodes.Status200OK);

        group.MapPost("/{pluginId}/{pageKey}/submit", async (
            string pluginId,
            string pageKey,
            [FromBody] PluginPageSubmitRequest request,
            HttpContext http,
            [FromServices] IRequestAuthorityResolver authorityResolver,
            [FromServices] PluginPageService pages,
            CancellationToken ct) =>
        {
            var result = await pages.SubmitAsync(
                await ViewerAsync(http, authorityResolver, ct).ConfigureAwait(false),
                pluginId,
                pageKey,
                request.Values,
                ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                PluginPageSubmitOutcome.Saved => Results.Ok(new PluginPageSubmitResponse { Saved = true, RecordKey = result.RecordKey ?? "" }),
                PluginPageSubmitOutcome.Invalid => ApiErrors.Unprocessable(result.Problem ?? "Check the form and try again."),
                PluginPageSubmitOutcome.Refused => ApiErrors.Conflict(result.Problem ?? "This plugin cannot keep any more entries right now."),
                _ => ApiErrors.NotFound("That page does not exist."),
            };
        })
            .WithName("SubmitPluginPageForm")
            .Produces<PluginPageSubmitResponse>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<PluginPageViewer> ViewerAsync(HttpContext http, IRequestAuthorityResolver authorityResolver, CancellationToken ct)
    {
        var authority = await authorityResolver.ResolveAsync(http, ct).ConfigureAwait(false);
        return new PluginPageViewer(authority.IsEffectiveAdministrator, authority.ActiveProfileIsRestricted);
    }
}
