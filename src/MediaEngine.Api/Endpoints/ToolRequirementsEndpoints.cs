using MediaEngine.Api.Http;
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

        group.MapPut("/{id}/location", async (
            string id,
            ToolLocationRequest request,
            ToolLocationService locations,
            CancellationToken ct) =>
        {
            var result = await locations.SetAsync(id, request, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                ToolLocationOutcome.Saved => Results.Ok(result.Tools),
                ToolLocationOutcome.UnknownTool => ApiErrors.NotFound("That isn't a tool the Library can look for."),
                _ => ApiErrors.BadRequest(result.Message ?? "That folder can't be used."),
            };
        })
        .WithName("SetToolLocation")
        .WithSummary("Saves the folder the person chose as a tool's home, checks the tools again, and returns the fresh result.")
        .Produces<ToolRequirementsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        return app;
    }
}
