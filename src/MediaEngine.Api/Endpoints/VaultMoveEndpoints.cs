using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Vault;
using MediaEngine.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Move to Vault and Move out (Vault sections D31). Preview shows what a folder's move would do and changes nothing; start asks for the
/// administrator's own profile PIN again, saves the job and returns at once, and the worker carries it out. Every action is for a server
/// administrator whose Vault mode is on; anyone else gets exactly the answer an unknown address gets, before a body is read. No answer
/// names a title, a file or a path.
/// </summary>
internal static class VaultMoveEndpoints
{
    internal static void MapVaultMoves(this RouteGroupBuilder group)
    {
        var moves = group.MapGroup("/vault-moves").WithTags("Vault");

        moves.MapPost("/preview", async (HttpContext http, [FromServices] IRequestAuthorityResolver authorities, [FromServices] VaultAccessResolver access, [FromServices] VaultMoveService service) =>
        {
            if (await VaultLibraryEndpoints.UnlockedAdministratorAsync(http, authorities, access) is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            if (await ReadAsync<VaultMovePreviewRequest>(http) is not { } request)
            {
                return ApiErrors.BadRequest("The request could not be read.");
            }

            return Results.Ok(await service.PreviewAsync(request.Direction, request.SourceId, http.RequestAborted));
        }).WithName("PreviewVaultMove").Produces<VaultMovePreviewDto>().ProducesProblem(StatusCodes.Status400BadRequest)
          .RequireAuthorization(AuthPolicies.Authenticated);

        moves.MapPost("/", async (HttpContext http, [FromServices] IRequestAuthorityResolver authorities, [FromServices] VaultAccessResolver access, [FromServices] VaultMoveService service) =>
        {
            if (await VaultLibraryEndpoints.UnlockedAdministratorAsync(http, authorities, access) is not { } administratorProfileId)
            {
                return ApiErrors.NotFound("Not found.");
            }

            if (await ReadAsync<StartVaultMoveRequest>(http) is not { } request)
            {
                return ApiErrors.BadRequest("The request could not be read.");
            }

            try
            {
                var outcome = await service.StartAsync(administratorProfileId, request, http.RequestAborted);
                if (outcome.Status != VaultMoveStartStatus.Started)
                {
                    return Results.Ok(new StartVaultMoveResultDto { Started = false, Preview = outcome.Preview });
                }

                return Results.Ok(new StartVaultMoveResultDto { Started = true, Job = await service.GetAsync(outcome.JobId!.Value, http.RequestAborted) });
            }
            catch (Exception ex) when (ex is VaultAdminPinNotSetException or ProfilePinRequiredException or ProfilePinLockedException)
            {
                return VaultEndpoints.PinFailure(ex);
            }
        }).WithName("StartVaultMove").Produces<StartVaultMoveResultDto>().ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status428PreconditionRequired).ProducesProblem(StatusCodes.Status429TooManyRequests)
          .RequireAuthorization(AuthPolicies.Authenticated);

        moves.MapGet("/", async (HttpContext http, [FromServices] IRequestAuthorityResolver authorities, [FromServices] VaultAccessResolver access, [FromServices] VaultMoveService service) =>
            await VaultLibraryEndpoints.UnlockedAdministratorAsync(http, authorities, access) is null
                ? ApiErrors.NotFound("Not found.")
                : Results.Ok(await service.ListAsync(50, http.RequestAborted)))
            .WithName("ListVaultMoves").Produces<IReadOnlyList<VaultMoveJobDto>>().RequireAuthorization(AuthPolicies.Authenticated);

        moves.MapGet("/{id:guid}", async (Guid id, HttpContext http, [FromServices] IRequestAuthorityResolver authorities, [FromServices] VaultAccessResolver access, [FromServices] VaultMoveService service) =>
        {
            if (await VaultLibraryEndpoints.UnlockedAdministratorAsync(http, authorities, access) is null)
            {
                return ApiErrors.NotFound("Not found.");
            }

            return await service.GetAsync(id, http.RequestAborted) is { } job ? Results.Ok(job) : ApiErrors.NotFound("Not found.");
        }).WithName("GetVaultMove").Produces<VaultMoveJobDto>().RequireAuthorization(AuthPolicies.Authenticated);
    }

    // The body is read after the caller is known, so a malformed body cannot tell a caller who is not an administrator that the action exists.
    private static async Task<T?> ReadAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(http.RequestAborted);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
