using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Api.Endpoints;

/// <summary>Administrator view of the tools and downloads the Library depends on.</summary>
public static class ToolRequirementsEndpoints
{
    public static IEndpointRouteBuilder MapToolRequirementsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/settings/tools").WithTags("Settings");

        group.MapGet("/", async (IToolRequirementsService tools, CancellationToken ct) =>
            Results.Ok(await tools.GetAsync(ct).ConfigureAwait(false)))
            .WithName("GetToolRequirements")
            .WithSummary("Returns the last tools check: what each tool affects, whether it is ready, and how to fix it.")
            .Produces<ToolRequirementsDto>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        group.MapPost("/check", async (IToolRequirementsService tools, CancellationToken ct) =>
            Results.Ok(await tools.CheckAsync(ct).ConfigureAwait(false)))
            .WithName("CheckToolRequirements")
            .WithSummary("Looks for the tools again, lets waiting work continue, and returns the fresh result.")
            .Produces<ToolRequirementsDto>(StatusCodes.Status200OK)
            .RequireEffectiveAdministrator();

        return app;
    }
}
