using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Paging;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Activity page's Engine actions: each person's own record of what they watched, listened to, read and added to
/// My list. The Engine decides whose activity the caller may see (Me, a household, a person or everyone); a paired
/// app sees its own person only. Only a server administrator exports or deletes other people's history.
/// </summary>
public static class PersonalActivityEndpoints
{
    public static IEndpointRouteBuilder MapPersonalActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/activity").WithTags("Personal activity");

        group.MapGet("/", async (
            [AsParameters] ActivityQueryParameters parameters,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
            Respond(await service.ListAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false), parameters.ToQuery(), ct).ConfigureAwait(false)))
        .WithName("GetPersonalActivity")
        .WithSummary("Returns the activity the caller may see, newest first: one item per entry, per title (group=title) or per music session.")
        .Produces<PersonalActivityPageDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/periods", async (
            [AsParameters] ActivityQueryParameters parameters,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
            Respond(await service.PeriodsAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false), parameters.ToQuery(), ct).ConfigureAwait(false)))
        .WithName("GetPersonalActivityPeriods")
        .WithSummary("Returns how many items fall in each year and month under the same scope and filters, for the date rail.")
        .Produces<PersonalActivityPeriodsDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/recent", async (
            int? limit,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
            Respond(await service.RecentAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false),
                PagedRequest.From(null, limit, defaultLimit: 20, maxLimit: 50).Limit, ct).ConfigureAwait(false)))
        .WithName("GetPersonalActivityRecent")
        .WithSummary("Returns the caller's own latest activity for the Overview strip, with music collapsed into sessions.")
        .Produces<PersonalActivityRecentDto>()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/summary", async (
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
            Respond(await service.SummaryAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false), ct).ConfigureAwait(false)))
        .WithName("GetPersonalActivitySummary")
        .WithSummary("Returns the caller's own lifetime totals: time played by kind, titles finished and in progress, and the first activity date.")
        .Produces<PersonalActivitySummaryDto>()
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/export.csv", async (
            [AsParameters] ActivityQueryParameters parameters,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
        {
            var result = await service.ExportAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false), parameters.ToQuery(), ct).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.Stream(
                    stream => result.Value!.WriteAsync(stream, ct),
                    "text/csv; charset=utf-8",
                    result.Value!.FileName)
                : Fail(result.Failure, result.Message);
        })
        .WithName("ExportPersonalActivityCsv")
        .WithSummary("Downloads exactly what the filters show as a CSV file. Exporting other people's activity is for a server administrator and is logged.")
        .Produces(StatusCodes.Status200OK, contentType: "text/csv")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

        group.MapGet("/delete-preview", async (
            DateTimeOffset? before,
            string? scope,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
            before is not { } cutoff
                ? ApiErrors.BadRequest("A before date is required.")
                : Respond(await service.DeletePreviewAsync(
                    await resolver.ResolveAsync(http, ct).ConfigureAwait(false), scope, cutoff, ct).ConfigureAwait(false)))
        .WithName("PreviewPersonalActivityDeletion")
        .WithSummary("Returns how many entries before the date would be deleted. Server administrators only.")
        .Produces<PersonalActivityDeletePreviewDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

        group.MapDelete("/", async (
            DateTimeOffset? before,
            string? scope,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
            before is not { } cutoff
                ? ApiErrors.BadRequest("A before date is required.")
                : Respond(await service.DeleteAsync(
                    await resolver.ResolveAsync(http, ct).ConfigureAwait(false), scope, cutoff, ct).ConfigureAwait(false)))
        .WithName("DeletePersonalActivity")
        .WithSummary("Deletes the entries before the date in the scope and is logged. Server administrators only; history is never deleted any other way.")
        .Produces<PersonalActivityDeleteResultDto>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireHumanSelfService();

        group.MapGet("/artwork/{key}", async (
            string key,
            string? scope,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            [FromServices] PersonalActivityReadService service,
            CancellationToken ct) =>
        {
            var result = await service.ArtworkAsync(
                await resolver.ResolveAsync(http, ct).ConfigureAwait(false), key, scope, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Fail(result.Failure, result.Message);
            }

            // The picture follows who may see it, so it is cached by the browser only.
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(result.Value!.Image, result.Value.ContentType);
        })
        .WithName("GetPersonalActivityArtwork")
        .WithSummary("Returns the small picture kept with an activity entry, only to someone who may see an entry that uses it.")
        .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        return app;
    }

    private static IResult Respond<T>(PersonalActivityResult<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Fail(result.Failure, result.Message);

    private static IResult Fail(PersonalActivityFailure failure, string? message) => failure switch
    {
        PersonalActivityFailure.BadRequest => ApiErrors.BadRequest(message ?? "The request is not valid."),
        PersonalActivityFailure.NotFound => ApiErrors.NotFound(message ?? "Not found."),
        _ => ApiErrors.Forbidden(message ?? "You can't see that activity."),
    };

    /// <summary>The filters shared by the list, the date rail and the CSV export.</summary>
    internal sealed record ActivityQueryParameters(
        string? Scope,
        string? Q,
        string[]? Kind,
        DateTimeOffset? From,
        DateTimeOffset? To,
        string? Outcome,
        string? Device,
        string? Group,
        string? Expand,
        string? Cursor,
        int? Limit)
    {
        public PersonalActivityQuery ToQuery() => new()
        {
            Scope = Scope,
            Q = Q,
            Kinds = Kind,
            From = From,
            To = To,
            Outcome = Outcome,
            Device = Device,
            Group = Group,
            Expand = Expand,
            Cursor = Cursor,
            Limit = PagedRequest.From(null, Limit, defaultLimit: 50, maxLimit: 200).Limit,
        };
    }
}
