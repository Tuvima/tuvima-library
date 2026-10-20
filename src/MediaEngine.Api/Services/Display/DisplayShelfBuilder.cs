using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayShelfBuilder
{
    private readonly DisplayCardBuilder _cards;
    private readonly DisplayLaneGroupPolicy _groupPolicy;

    public DisplayShelfBuilder(DisplayCardBuilder cards, DisplayLaneGroupPolicy? groupPolicy = null)
    {
        _cards = cards;
        _groupPolicy = groupPolicy ?? new DisplayLaneGroupPolicy();
    }

    public IReadOnlyList<DisplayShelfDto> BuildReadShelves(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyList<DisplayJourneyRow> journey,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> progressByWork,
        int shelfLimit)
    {
        var take = Math.Max(1, shelfLimit);
        var shelves = new List<DisplayShelfDto>();
        AddShelf(shelves, "continue-reading", "Continue reading", "Books and comics already in motion",
            journey.Take(take).Select(item => _cards.FromJourney(item, "read")).ToList(), DisplayGridCatalog.RouteFor("read", "continue-reading"));

        AddShelf(shelves, "recently-added", "Recently added to read", "Fresh pages and issues ready to pick up",
            works.Take(take).Select(work => _cards.FromWork(work, "read", progressByWork.GetValueOrDefault(work.WorkId))).ToList(), DisplayGridCatalog.RouteFor("read", "recently-added"));

        var authorShelves = works
            .Where(work => !string.IsNullOrWhiteSpace(work.Author))
            .GroupBy(work => work.Author!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count())
            .Take(3)
            .Select(group => new DisplayShelfDto(
                Key: $"author-{DisplayMediaRules.StableKey(group.Key)}",
                Title: group.Key,
                Subtitle: $"{group.Count()} titles",
                Items: group.Take(20).Select(work => _cards.FromWork(work, "read", progressByWork.GetValueOrDefault(work.WorkId))).ToList(),
                SeeAllRoute: DisplayGridCatalog.RouteFor("read", $"author-{DisplayMediaRules.StableKey(group.Key)}")));

        shelves.AddRange(authorShelves);
        return shelves;
    }

    public IReadOnlyList<DisplayShelfDto> BuildWatchShelves(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyList<DisplayJourneyRow> journey,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> progressByWork,
        int shelfLimit)
    {
        var take = Math.Max(1, shelfLimit);
        var shelves = new List<DisplayShelfDto>();
        var groupShelf = _groupPolicy.GetShelf("watch");
        var tvShowCards = _cards.BuildTvShowCards(works, progressByWork);
        AddShelf(shelves, "continue-watching", "Continue watching", "Movies and shows already in progress",
            journey.Take(take).Select(item => _cards.FromJourney(item, "watch")).ToList(), DisplayGridCatalog.RouteFor("watch", "continue-watching"));

        AddShelf(shelves, "tv-shows", "TV Shows", "Shows built from the episodes you own",
            tvShowCards.Take(take).ToList(), DisplayGridCatalog.RouteFor("watch", "tv-shows"));

        if (groupShelf.Enabled)
        {
            AddShelf(shelves, groupShelf.Key, groupShelf.Title, groupShelf.Subtitle,
                _cards.BuildMovieSeriesCards(works, groupShelf.MinimumSeriesItems, progressByWork).Take(take).ToList(), groupShelf.SeeAllRoute);
        }

        AddShelf(shelves, "movies", "Movies in your library", "Feature films ready to play",
            works.Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Movie").Take(take).Select(work => _cards.FromWork(work, "watch", progressByWork.GetValueOrDefault(work.WorkId))).ToList(), DisplayGridCatalog.RouteFor("watch", "movies"));

        var genreShelves = works
            .SelectMany(work => DisplayMediaRules.SplitValues(work.Genre).Select(genre => (Genre: genre, Work: work)))
            .GroupBy(item => item.Genre, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 3)
            .OrderByDescending(group => group.Count())
            .Take(3)
            .Select(group => new DisplayShelfDto(
                Key: $"genre-{DisplayMediaRules.StableKey(group.Key)}",
                Title: group.Key,
                Subtitle: $"{group.Count()} titles",
                Items: group.Select(item => DisplayMediaRules.NormalizeDisplayKind(item.Work.MediaType) == "TV"
                        ? tvShowCards.FirstOrDefault(card => string.Equals(card.Title, item.Work.ShowName, StringComparison.OrdinalIgnoreCase))
                        : _cards.FromWork(item.Work, "watch", progressByWork.GetValueOrDefault(item.Work.WorkId)))
                    .Where(card => card is not null)
                    .Cast<DisplayCardDto>()
                    .DistinctBy(card => card.Id)
                    .Take(20)
                    .ToList(),
                SeeAllRoute: DisplayGridCatalog.RouteFor("watch", $"genre-{DisplayMediaRules.StableKey(group.Key)}")));

        shelves.AddRange(genreShelves);
        return shelves;
    }

    public static void AddShelf(List<DisplayShelfDto> shelves, string key, string title, string subtitle, IReadOnlyList<DisplayCardDto> cards, string? route)
    {
        if (cards.Count == 0)
        {
            return;
        }

        shelves.Add(new DisplayShelfDto(key, title, subtitle, cards, route));
    }
}
