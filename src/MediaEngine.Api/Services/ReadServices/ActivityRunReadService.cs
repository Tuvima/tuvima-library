using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads the activity runs for the Activity page (spec library-activity WP1): the History list, one opened batch as stacked tiles, the
/// instant title search, and the administrator's trim. Administrators only; the endpoint decides that. Library names come from the library
/// settings. Small covers use the same cached small-art address the other Engine lists use.
/// </summary>
public sealed class ActivityRunReadService(IActivityRunStore store, IConfigurationLoader configuration)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;
    private const int CoversPerRun = 5;

    public async Task<ActivityRunsDto> ListRunsAsync(ActivityRunQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit <= 0 ? DefaultLimit : query.Limit, 1, MaximumLimit);
        var page = await store.ListRunsAsync(query with { Limit = limit + 1 }, ct).ConfigureAwait(false);
        var names = LibraryNames();

        var runs = new List<ActivityRunDto>();
        foreach (var summary in page.Take(limit))
        {
            var covers = (await store.ListGroupsAsync(summary.Run.Id, null, 0, CoversPerRun, ct).ConfigureAwait(false))
                .Select(group => new ActivityCoverDto(CoverUrl(group), group.MediaType, group.GroupTitle))
                .ToList();
            runs.Add(ToDto(summary, names, covers));
        }

        var last = await store.GetLastCompletedAsync(query.LibraryIds, ct).ConfigureAwait(false);
        return new ActivityRunsDto(runs, page.Count > limit, last);
    }

    public async Task<ActivityRunGroupsDto?> ListGroupsAsync(Guid runId, string? mediaType, int offset, int limit, CancellationToken ct = default)
    {
        if (await store.GetRunAsync(runId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var take = Math.Clamp(limit <= 0 ? 24 : limit, 1, MaximumLimit);
        var groups = await store.ListGroupsAsync(runId, string.IsNullOrWhiteSpace(mediaType) ? null : mediaType, offset, take + 1, ct)
            .ConfigureAwait(false);
        return new ActivityRunGroupsDto(
            runId,
            groups.Take(take).Select(ToDto).ToList(),
            groups.Count > take);
    }

    public async Task<ActivityTitleSearchDto> SearchTitlesAsync(ActivityRunQuery query, CancellationToken ct = default)
    {
        var hits = await store.SearchTitlesAsync(query, ct).ConfigureAwait(false);
        var cards = hits
            .GroupBy(hit => hit.GroupKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.OrderByDescending(hit => hit.StartedAt).First();
                var markers = group
                    .OrderByDescending(hit => hit.StartedAt)
                    .Select(hit => new ActivityTitleMarkerDto(hit.RunId, hit.Kind, hit.StartedAt, hit.ItemCount))
                    .ToList();
                var asGroup = new ActivityRunGroup(
                    first.GroupKey, first.GroupTitle, first.MediaType, first.GroupWorkId, first.FirstWorkId, null,
                    group.Sum(hit => hit.ItemCount));
                return new ActivityTitleCardDto(
                    first.GroupKey, first.GroupTitle, first.MediaType, CoverUrl(asGroup), RouteOf(asGroup),
                    asGroup.ItemCount, markers);
            })
            .OrderByDescending(card => card.Markers[0].StartedAt)
            .ToList();
        return new ActivityTitleSearchDto(cards);
    }

    public async Task<ActivityPurgeResultDto> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default)
        => new(await store.PurgeAsync(olderThan, ct).ConfigureAwait(false));

    private Dictionary<string, string> LibraryNames()
        => configuration.LoadLibraries().Libraries
            .Where(library => !string.IsNullOrWhiteSpace(library.Id))
            .GroupBy(library => library.Id.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

    private static ActivityRunDto ToDto(ActivityRunSummary summary, IReadOnlyDictionary<string, string> names, IReadOnlyList<ActivityCoverDto> covers)
    {
        var run = summary.Run;
        return new ActivityRunDto(
            run.Id,
            run.LibraryId,
            names.TryGetValue(run.LibraryId.ToLowerInvariant(), out var name) ? name : string.Empty,
            run.Kind,
            run.Status,
            run.StartedAt,
            run.EndedAt,
            run.ItemCount,
            summary.CountsByMediaType
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new ActivityRunCountDto(entry.Key, entry.Value))
                .ToList(),
            covers);
    }

    private static ActivityGroupDto ToDto(ActivityRunGroup group)
        => new(group.GroupKey, group.GroupTitle, group.MediaType, group.ItemCount, CoverUrl(group), RouteOf(group));

    // A show or album tile shows the show's or album's own art; any other tile shows its title's art. A removed file has none.
    private static string? CoverUrl(ActivityRunGroup group)
        => (group.MediaType is "TV" or "Music" ? group.GroupWorkId ?? group.FirstWorkId : group.FirstWorkId) is { } id && id != Guid.Empty
            ? $"/stream/entity/work/{id:D}/cover?size=s"
            : null;

    // Only a tile that stands for one page gets a link: a show or album, or a single title.
    private static string? RouteOf(ActivityRunGroup group)
    {
        if (string.IsNullOrEmpty(group.MediaType))
        {
            return null;
        }

        if (group.MediaType is "TV" or "Music")
        {
            return group.GroupWorkId is { } showId && showId != Guid.Empty
                ? IngestionPresentationReadService.DetailRoute(group.MediaType, showId)
                : null;
        }

        return group.ItemCount == 1 && group.FirstWorkId is { } workId && workId != Guid.Empty
            ? IngestionPresentationReadService.DetailRoute(group.MediaType, workId)
            : null;
    }
}
