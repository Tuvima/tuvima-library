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

        // One entry per scan: the runs of different libraries that opened in the same scan are shown together (Shy 10 Nov).
        var runs = new List<ActivityRunDto>();
        foreach (var scan in page.Take(limit).GroupBy(summary => summary.Run.EntryId))
        {
            var covers = (await store.ListGroupsAsync(scan.Key, null, 0, CoversPerRun, ct).ConfigureAwait(false))
                .Select(group => new ActivityCoverDto(CoverUrl(group), group.MediaType, group.GroupTitle))
                .ToList();
            runs.Add(ToDto(scan.ToList(), names, covers));
        }

        var last = await store.GetLastCompletedAsync(query.LibraryIds, ct).ConfigureAwait(false);
        return new ActivityRunsDto(runs, page.Count > limit, last);
    }

    public async Task<ActivityRunGroupsDto?> ListGroupsAsync(Guid entryId, string? mediaType, int offset, int limit, CancellationToken ct = default)
    {
        if (await store.GetRunAsync(entryId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var take = Math.Clamp(limit <= 0 ? 24 : limit, 1, MaximumLimit);
        var groups = await store.ListGroupsAsync(entryId, string.IsNullOrWhiteSpace(mediaType) ? null : mediaType, offset, take + 1, ct)
            .ConfigureAwait(false);
        return new ActivityRunGroupsDto(
            entryId,
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
                    .Select(hit => new ActivityTitleMarkerDto(hit.EntryId, hit.Kind, hit.StartedAt, hit.ItemCount))
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

    private static ActivityRunDto ToDto(IReadOnlyList<ActivityRunSummary> members, IReadOnlyDictionary<string, string> names, IReadOnlyList<ActivityCoverDto> covers)
    {
        var first = members.OrderBy(member => member.Run.StartedAt).First().Run;
        var active = members.Any(member => member.Run.Status == ActivityRunStatuses.Active);
        var failed = members.Any(member => member.Run.Status == ActivityRunStatuses.Failed);
        var libraryNames = members
            .Select(member => names.TryGetValue(member.Run.LibraryId.ToLowerInvariant(), out var name) ? name : string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var counts = members
            .SelectMany(member => member.CountsByMediaType)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(group => (MediaType: group.Key, Count: group.Sum(entry => entry.Value)))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.MediaType, StringComparer.Ordinal)
            .Select(entry => new ActivityRunCountDto(entry.MediaType, entry.Count))
            .ToList();
        return new ActivityRunDto(
            first.EntryId,
            first.LibraryId,
            string.Join(", ", libraryNames),
            first.Kind,
            active ? ActivityRunStatuses.Active : failed ? ActivityRunStatuses.Failed : ActivityRunStatuses.Completed,
            first.StartedAt,
            active ? null : members.Max(member => member.Run.EndedAt),
            members.Sum(member => member.Run.ItemCount),
            counts,
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
