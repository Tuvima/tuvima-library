using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Backups;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Moving one user between Libraries (backup and restore spec D19). The file holds what is needed to sign in as that person, so every
/// route asks the person to confirm it is them first (403 <c>confirm_its_you</c> for a stale sign-in), a person exports only themselves,
/// and only an administrator signed in at the Dashboard exports or imports anyone else.
/// </summary>
public static class UserTransferEndpoints
{
    internal static IEndpointRouteBuilder MapUserTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var access = app.MapGroup("/access").WithTags("Access");

        // The signed-in person's own account, never one named in the request.
        access.MapPost("/me/export", async (HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] IUserTransferService transfers, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(http, async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var download = await transfers.ExportOwnAsync(await resolver.ResolveAsync(http, ct), ct);
            return Results.File(download.Content, "application/zip", download.FileName);
        }))
            .RequireHumanSelfService()
            .WithName("ExportMyData")
            .WithSummary("Downloads the signed-in person's own data as one file: their sign-in, profiles and each profile's own state.")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var accounts = access.MapGroup("/accounts");
        accounts.MapPost("/{accountId:guid}/export", async (Guid accountId, HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] IUserTransferService transfers, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(http, async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var download = await transfers.ExportAsync(await resolver.ResolveAsync(http, ct), accountId, ct);
            return Results.File(download.Content, "application/zip", download.FileName);
        }))
            .RequireEffectiveAdministratorInPerson()
            .WithName("ExportAccount")
            .WithSummary("Downloads one user's data as a file another Library can import. For administrators.")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        accounts.MapPost("/import/inspect", async (HttpRequest request, HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] IUserTransferService transfers, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(http, async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            if (!request.HasFormContentType)
            {
                return ApiErrors.BadRequest("Upload the file as multipart form data.");
            }

            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return ApiErrors.BadRequest("Choose a user export file.");
            }

            await using var stream = file.OpenReadStream();
            return Results.Ok(await transfers.InspectAsync(await resolver.ResolveAsync(http, ct), stream, file.FileName, ct));
        }))
            .RequireEffectiveAdministratorInPerson()
            .WithName("InspectUserImport")
            .WithSummary("Checks an uploaded user export and says who it holds and whether it can be imported. Writes nothing to the data store.")
            .Produces<UserImportPreviewDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithMetadata(
                new RequestSizeLimitAttribute(UserTransferService.MaxUploadBytes + 1024 * 1024),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = UserTransferService.MaxUploadBytes + 1024 * 1024 })
            .DisableAntiforgery();

        accounts.MapPost("/import/{operationId:guid}", async (Guid operationId, HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] IUserTransferService transfers, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(http, async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            return Results.Ok(await transfers.ImportAsync(await resolver.ResolveAsync(http, ct), operationId, ct));
        }))
            .RequireEffectiveAdministratorInPerson()
            .WithName("ImportUser")
            .WithSummary("Imports a checked user export as a new user. They can sign in but see nothing until an administrator gives them access.")
            .Produces<UserImportResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }

    private static async Task<IResult> ExecuteAsync(HttpContext http, Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (ArgumentException ex) { return ApiErrors.BadRequest(ex.Message); }
        catch (InvalidDataException ex) { return ApiErrors.BadRequest(ex.Message); }
        catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
        catch (UserTransferRefusedException ex) { return ApiErrors.Conflict(ex.Message); }
        catch (UnauthorizedAccessException ex) { return ApiErrors.Forbidden(ex.Message); }
        catch (InvalidOperationException ex)
        {
            // A problem on this computer (a missing key, a table the rules do not know): the internal message is logged, never shown.
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UserTransferEndpoints))
                .LogError(ex, "A user export or import failed on this computer.");
            return ApiErrors.Problem(StatusCodes.Status500InternalServerError, "The user couldn't be moved.", UserTransferService.ComputerProblemMessage);
        }
    }
}
