using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.Security;
using MediaEngine.Contracts.Backups;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Moving people between Libraries (backup and restore spec D19, users spec D9 and D16). The file holds what is needed to sign in as each
/// person in it, so every route asks the person to confirm it is them first (403 <c>confirm_its_you</c> for a stale sign-in), and only the
/// server administrator signed in at the Dashboard exports (their own profile, anyone else, or a household) or imports. Everyone else,
/// household administrators included, is just a user and is refused with 403.
/// </summary>
public static class UserTransferEndpoints
{
    internal static IEndpointRouteBuilder MapUserTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var access = app.MapGroup("/access").WithTags("Access");

        // The signed-in server administrator's own account, never one named in the request.
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
            .RequireEffectiveAdministratorInPerson()
            .WithName("ExportMyData")
            .WithSummary("Downloads the signed-in server administrator's own data as one file: their profile, the sign-in that opens it and the profile's own state. Only the server administrator exports.")
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
            .WithSummary("Downloads one user's profile and sign-in as a file another Library can import. For the server administrator.")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        // A whole household: every profile, their sign-ins and their own state. The server administrator only; a household administrator
        // gets 403 like any other user.
        access.MapPost("/households/{householdId:guid}/export", async (Guid householdId, HttpContext http, IRequestAuthorityResolver resolver,
            [FromServices] IUserTransferService transfers, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(http, async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            var download = await transfers.ExportHouseholdAsync(await resolver.ResolveAsync(http, ct), householdId, ct);
            return Results.File(download.Content, "application/zip", download.FileName);
        }))
            .RequireEffectiveAdministratorInPerson()
            .WithName("ExportHousehold")
            .WithSummary("Downloads a whole household (every profile, their sign-ins and their own state) as a file another Library can import. For the server administrator only.")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
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
            .WithSummary("Checks an uploaded user file and says who it holds, which sign-ins clash, and the households to put them in. Writes nothing to the data store.")
            .Produces<UserImportPreviewDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithMetadata(
                new RequestSizeLimitAttribute(UserTransferService.MaxUploadBytes + 1024 * 1024),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = UserTransferService.MaxUploadBytes + 1024 * 1024 })
            .DisableAntiforgery();

        accounts.MapPost("/import/{operationId:guid}", async (Guid operationId, [FromBody] UserImportRequestDto? request, HttpContext http,
            IRequestAuthorityResolver resolver, [FromServices] IUserTransferService transfers, [FromServices] RecentSignInGuard recentSignIn,
            CancellationToken ct) => await ExecuteAsync(http, async () =>
        {
            if (await recentSignIn.RefuseHumanIfStaleAsync(http.User, ct).ConfigureAwait(false) is { } stale)
            {
                return stale;
            }

            return Results.Ok(await transfers.ImportAsync(await resolver.ResolveAsync(http, ct), operationId, request!, ct));
        }))
            .RequireEffectiveAdministratorInPerson()
            .WithName("ImportUser")
            .WithSummary("Imports a checked user file as new profiles in the chosen household. Never carries a role; Read, Watch, Listen and View only when kept; never library access.")
            .Produces<UserImportResultDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
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
