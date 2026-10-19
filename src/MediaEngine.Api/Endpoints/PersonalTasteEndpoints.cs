using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Taste;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Taste;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Taste page's Engine actions: what the Library has learned about each person's taste, with their own removals and
/// additions applied. The Engine decides whose taste the caller may see (Me, a household, a person or everyone); other
/// people's taste is read-only, and only a signed-in person changes their own.
/// </summary>
public static class PersonalTasteEndpoints
{
    public static IEndpointRouteBuilder MapPersonalTasteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/taste").WithTags("Personal taste");

        group.MapGet("/", async (
            string? scope,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalTasteService service,
            CancellationToken ct) =>
        {
            var result = await service.GetAsync(await resolver.ResolveAsync(http, ct).ConfigureAwait(false), scope, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(result.Value) : Fail(result.Failure, result.Message);
        })
        .WithName("GetPersonalTaste")
        .WithSummary("Returns the taste of each person the scope covers (me, profile:{id}, household:{id} or everyone) with their removals and additions applied. Viewing other people's taste is logged.")
        .Produces<TasteViewDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapPost("/items", async (
            AddTasteItemRequest request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalTasteService service,
            CancellationToken ct) =>
        {
            var result = await service.AddAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false), request.Group, request.Value, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.NoContent() : Fail(result.Failure, result.Message);
        })
        .WithName("AddPersonalTasteItem")
        .WithSummary("Adds a genre, mood or era to the signed-in person's own taste. Nobody can change anyone else's taste.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireHumanSelfService();

        group.MapDelete("/items/{group}/{**value}", async (
            string group,
            string value,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalTasteService service,
            CancellationToken ct) =>
        {
            var result = await service.RemoveAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false), group, value, ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.NoContent() : Fail(result.Failure, result.Message);
        })
        .WithName("RemovePersonalTasteItem")
        .WithSummary("Hides a genre, mood or era from the signed-in person's own taste. The learned taste is not changed. Nobody can change anyone else's taste.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireHumanSelfService();

        return app;
    }

    private static IResult Fail(PersonalTasteFailure failure, string? message) => failure switch
    {
        PersonalTasteFailure.BadRequest => ApiErrors.BadRequest(message ?? "The request is not valid."),
        PersonalTasteFailure.NotFound => ApiErrors.NotFound(message ?? "Not found."),
        _ => ApiErrors.Forbidden(message ?? "You can't do that."),
    };
}
