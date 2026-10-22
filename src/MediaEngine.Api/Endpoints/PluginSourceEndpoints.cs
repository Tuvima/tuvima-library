using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The metadata sources as plugins (Developer tools spec WP20): the list for the Plugins page, what would have no source if one
/// were switched off, and a side-by-side of two. All read-only, from the provider files; nothing here changes how items are
/// identified or enriched.
/// </summary>
public static class PluginSourceEndpoints
{
    internal static IEndpointRouteBuilder MapPluginSourceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/plugin-sources").WithTags("Plugins");

        group.MapGet("", (ProviderCapabilityCatalogue catalogue) => Results.Ok(catalogue.Overview()))
            .WithName("ListPluginSources")
            .WithSummary("Every metadata source with its capability sheet (what it supplies for which kinds of media), whether it is on, where it sends data, and whether the Engine must restart to match the list.")
            .Produces<PluginSourcesResponse>(StatusCodes.Status200OK)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigRead);

        group.MapGet("/compare", (string from, string to, ProviderCapabilityCatalogue catalogue) =>
            catalogue.Compare(from, to) is { } comparison
                ? Results.Ok(comparison)
                : ApiErrors.NotFound("One of those sources was not found."))
            .WithName("ComparePluginSources")
            .WithSummary("Two metadata sources side by side: what both supply and what only one does. Used before swapping one source for another.")
            .Produces<PluginSourceComparisonDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigRead);

        group.MapGet("/{name}/switch-off-impact", (string name, ProviderCapabilityCatalogue catalogue) =>
            catalogue.SwitchOffImpact(name) is { } impact
                ? Results.Ok(impact)
                : ApiErrors.NotFound($"Source '{name}' was not found."))
            .WithName("GetPluginSourceSwitchOffImpact")
            .WithSummary("What would have no source if this metadata source were switched off or removed, and what other sources would still cover. Existing matches are always kept.")
            .Produces<PluginSourceImpactDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAdministratorOrApplication(ApplicationPermissionIds.ProvidersConfigRead);

        return routes;
    }
}
