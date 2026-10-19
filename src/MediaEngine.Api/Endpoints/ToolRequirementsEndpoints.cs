using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

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

        group.MapPost("/{id}/install", async (
            string id,
            [FromServices] IToolInstallerService installer,
            IToolRequirementsService tools,
            CancellationToken ct) =>
        {
            var result = await installer.StartAsync(id, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                ToolInstallOutcome.Accepted => Results.Accepted(null, await tools.GetAsync(ct).ConfigureAwait(false)),
                ToolInstallOutcome.UnknownTool => ApiErrors.NotFound("That isn't a tool the Library can look for."),
                ToolInstallOutcome.AlreadyRunning => ApiErrors.Conflict("That is already being downloaded."),
                _ => ApiErrors.BadRequest(result.Message ?? "The Library can't install that for you here."),
            };
        })
        .WithName("InstallTool")
        .WithSummary("Starts downloading FFmpeg or Tesseract from its pinned address, checks it against its recorded checksum, and returns the tools list with the download shown as in progress.")
        .Produces<ToolRequirementsDto>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireEffectiveAdministrator();

        group.MapDelete("/{id}/install", async (
            string id,
            [FromServices] IToolInstallerService installer,
            IToolRequirementsService tools,
            CancellationToken ct) =>
        {
            if (!await IsKnownAsync(tools, id, ct).ConfigureAwait(false))
            {
                return ApiErrors.NotFound("That isn't a tool the Library can look for.");
            }

            await installer.CancelAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(await tools.GetAsync(ct).ConfigureAwait(false));
        })
        .WithName("CancelToolInstall")
        .WithSummary("Cancels a tool download in progress and removes its temporary files. Does nothing when none is running.")
        .Produces<ToolRequirementsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/{id}/run-installer", async (
            string id,
            [FromServices] IToolInstallerService installer,
            IToolRequirementsService tools,
            CancellationToken ct) =>
        {
            if (!await IsKnownAsync(tools, id, ct).ConfigureAwait(false))
            {
                return ApiErrors.NotFound("That isn't a tool the Library can look for.");
            }

            var result = await installer.RunInstallerAsync(id, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                ToolInstallOutcome.Accepted => Results.Accepted(null, await tools.GetAsync(ct).ConfigureAwait(false)),
                ToolInstallOutcome.AlreadyRunning => ApiErrors.Conflict("The installer is already open."),
                _ => ApiErrors.BadRequest(result.Message ?? "There's no installer ready to run."),
            };
        })
        .WithName("RunToolInstaller")
        .WithSummary("Starts Tesseract's downloaded setup program on the machine running the Engine; Windows asks for approval. Only when the Engine runs in a signed-in desktop session.")
        .Produces<ToolRequirementsDto>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireEffectiveAdministrator();

        group.MapPost("/{id}/decline", async (
            string id,
            IToolRequirementsService tools,
            [FromServices] IToolPromptPreferenceRepository prompts,
            CancellationToken ct) =>
        {
            if (!await IsKnownAsync(tools, id, ct).ConfigureAwait(false))
            {
                return ApiErrors.NotFound("That isn't a tool the Library can look for.");
            }

            await prompts.DeclineAsync(id, ct).ConfigureAwait(false);
            return Results.Ok(await tools.GetAsync(ct).ConfigureAwait(false));
        })
        .WithName("DeclineToolInstall")
        .WithSummary("Records that the tools banner must not offer to install this tool again. The Tools page still offers it.")
        .Produces<ToolRequirementsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        return app;
    }

    private static async Task<bool> IsKnownAsync(IToolRequirementsService tools, string id, CancellationToken ct) =>
        (await tools.GetAsync(ct).ConfigureAwait(false)).Tools.Any(tool => tool.Id == id);
}
