using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// The one rule for the "New" tag on tiles (Library activity D14): an item is New for 14 days after a later import added it,
/// and nothing is New after a library's first import. The Engine decides it here so every Dashboard and device agrees.
/// </summary>
public sealed class NewTagRule(IActivityRunStore runs, TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public async Task<DisplayPageDto> MarkAsync(DisplayPageDto page, CancellationToken ct = default)
    {
        var newIds = await FindNewAsync(page.Shelves.SelectMany(shelf => shelf.Items).Concat(page.Catalog), ct);
        return page with
        {
            Shelves = [.. page.Shelves.Select(shelf => shelf with { Items = Mark(shelf.Items, newIds) })],
            Catalog = Mark(page.Catalog, newIds),
        };
    }

    public async Task<DisplayShelfPageDto> MarkAsync(DisplayShelfPageDto page, CancellationToken ct = default)
    {
        var newIds = await FindNewAsync(page.Shelf.Items, ct);
        return page with { Shelf = page.Shelf with { Items = Mark(page.Shelf.Items, newIds) } };
    }

    public async Task<DisplayGridPageDto> MarkAsync(DisplayGridPageDto page, CancellationToken ct = default)
    {
        var newIds = await FindNewAsync(page.Items, ct);
        return page with { Items = Mark(page.Items, newIds) };
    }

    private async Task<IReadOnlySet<Guid>> FindNewAsync(IEnumerable<DisplayCardDto> cards, CancellationToken ct)
    {
        var ids = cards.SelectMany(WorkIdsOf).Distinct().ToList();
        return await runs.GetNewWorkIdsAsync(ids, clock.GetUtcNow() - Window, ct);
    }

    private static IReadOnlyList<DisplayCardDto> Mark(IReadOnlyList<DisplayCardDto> cards, IReadOnlySet<Guid> newIds) =>
        [.. cards.Select(card => card with { IsNew = WorkIdsOf(card).Any(newIds.Contains) })];

    private static IEnumerable<Guid> WorkIdsOf(DisplayCardDto card)
    {
        if (card.WorkId is { } workId)
        {
            yield return workId;
        }

        if (card.EpisodeContext is { } episode)
        {
            yield return episode.EpisodeWorkId;
        }
    }
}
