using System.Globalization;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Activity page's History (spec library-activity D3, D9 to D20): the runs the Engine recorded, one opened run as stacked tiles, an
/// instant title search, and the administrator's trim. Administrators only; for everyone else the routes do not exist. A Vault library is
/// never recorded, so nothing here can leak a Vault title or cover (D19).
/// </summary>
internal static class ActivityRunEndpoints
{
    internal static void MapActivityRuns(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/activity").WithTags("Libraries");

        group.MapGet("/runs", async (
            string? library,
            string? kind,
            string? from,
            string? to,
            string? q,
            string? sort,
            int? offset,
            int? limit,
            [FromServices] ActivityRunReadService runs,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(offset, limit, defaultLimit: ActivityRunReadService.DefaultLimit);
            if (Query(library, kind, from, to, q, sort, paged) is not { } query)
            {
                return ApiErrors.BadRequest("A date or kind in the History filters was not understood.");
            }

            return Results.Ok(await runs.ListRunsAsync(query, ct).ConfigureAwait(false));
        }).WithName("ListActivityRuns").WithSummary("Returns the History of imports, refreshes, fixes and removals, one entry per batch.")
          .Produces<ActivityRunsDto>().RequireAdmin();

        group.MapGet("/runs/{entryId:guid}/groups", async (
            Guid entryId,
            string? media_type,
            int? offset,
            int? limit,
            [FromServices] ActivityRunReadService runs,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(offset, limit, defaultLimit: 24);
            var groups = await runs.ListGroupsAsync(entryId, media_type, paged.Offset, paged.Limit, ct).ConfigureAwait(false);
            return groups is null ? ApiErrors.NotFound("Not found.") : Results.Ok(groups);
        }).WithName("ListActivityRunGroups").WithSummary("Returns one batch's items stacked into tiles, a page at a time.")
          .Produces<ActivityRunGroupsDto>().RequireAdmin();

        group.MapGet("/titles", async (
            string? q,
            string? library,
            string? kind,
            string? from,
            string? to,
            [FromServices] ActivityRunReadService runs,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.Ok(new ActivityTitleSearchDto([]));
            }

            if (Query(library, kind, from, to, q, null, PagedRequest.From(0, 200, maxLimit: 200)) is not { } query)
            {
                return ApiErrors.BadRequest("A date or kind in the History filters was not understood.");
            }

            return Results.Ok(await runs.SearchTitlesAsync(query, ct).ConfigureAwait(false));
        }).WithName("SearchActivityTitles").WithSummary("Finds a title across every batch; each title appears once with its own dated markers.")
          .Produces<ActivityTitleSearchDto>().RequireAdmin();

        group.MapPost("/purge", async (
            [FromBody] ActivityPurgeRequest request,
            [FromServices] ActivityRunReadService runs,
            CancellationToken ct) =>
            request is null || request.OlderThan == default
                ? ApiErrors.BadRequest("Choose the date before which History should be removed.")
                : Results.Ok(await runs.PurgeAsync(request.OlderThan, ct).ConfigureAwait(false)))
          .WithName("PurgeActivityRuns").WithSummary("Removes History entries that started before the chosen date (active runs are kept).")
          .Produces<ActivityPurgeResultDto>().RequireAdmin();
    }

    private static ActivityRunQuery? Query(
        string? library, string? kind, string? from, string? to, string? q, string? sort, PagedRequest paged)
    {
        var kinds = Split(kind);
        if (kinds.Any(value => !ActivityRunKinds.IsKnown(value)))
        {
            return null;
        }

        if (!TryDate(from, out var start) || !TryDate(to, out var end))
        {
            return null;
        }

        return new ActivityRunQuery(
            Split(library),
            kinds,
            start,
            end,
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            !string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase),
            paged.Offset,
            paged.Limit);
    }

    private static string[] Split(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryDate(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
