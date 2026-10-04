using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Services.Display;

public sealed partial class DisplayComposerService
{
    private readonly IDisplayProjectionReadService _readService;
    private readonly DisplayCardBuilder _cards;
    private readonly DisplayShelfBuilder _shelves;
    private readonly MusicPlayStatsRepository? _musicStats;

    public DisplayComposerService(
        IDisplayProjectionReadService readService,
        DisplayCardBuilder cards,
        DisplayShelfBuilder shelves,
        MusicPlayStatsRepository? musicStats = null)
    {
        _readService = readService;
        _cards = cards;
        _shelves = shelves;
        _musicStats = musicStats;
    }

    public async Task<DisplayPageDto> BuildHomeAsync(bool includeCatalog = true, Guid? profileId = null, CancellationToken ct = default, int shelfLimit = 18)
    {
        var worksTask = _readService.LoadHomeWorksAsync(ct);
        var journeyTask = _readService.LoadJourneyAsync(profileId, null, ct);
        var homeCollectionsTask = _readService.LoadHomeCollectionsAsync(profileId, ct);
        await Task.WhenAll(worksTask, journeyTask, homeCollectionsTask);

        var works = (await worksTask).ToList();
        var journey = (await journeyTask).ToList();
        var homeCollections = await homeCollectionsTask;
        var progressByWork = LatestProgressByWork(await _readService.LoadStatesAsync(profileId, null, ct));
        var tvShowCards = _cards.BuildTvShowCards(works, progressByWork);

        var musicAlbumCards = BuildMusicAlbumCards(works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
            .ToList());
        var continueCards = journey
            .Where(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType) != "Music")
            .Where(item => item.ProgressPct is > 0 and < 99.5)
            .Select(item => _cards.FromJourney(item, "home"))
            .Concat(BuildMusicJourneyAlbumCards(works, journey))
            .OrderByDescending(card => card.SortTimestamp)
            .Take(Math.Max(1, shelfLimit))
            .ToList();

        var readyRoots = works.GroupBy(work => work.RootWorkId)
            .Where(group => group.All(work => work.IsIdentityReady))
            .Select(group => group.Key.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var freshCandidates = works
            .Where(work => work.IsIdentityReady)
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) is not ("TV" or "Music"))
            .Select(work => _cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId)))
            .Concat(tvShowCards.Where(card => readyRoots.Contains((card.WorkId ?? card.Id).ToString("D"))).Select(card => card with { TileTextMode = "coverOnly" }))
            .Concat(musicAlbumCards.Where(card => readyRoots.Contains((card.WorkId ?? card.Id).ToString("D"))))
            .OrderByDescending(card => card.SortTimestamp)
            .ToList();

        var readCards = works
            .Where(work => DisplayMediaRules.IsReadKind(work.MediaType))
            .OrderByDescending(work => progressByWork.TryGetValue(work.WorkId, out var p) ? p.LastAccessed : work.CreatedAt)
            .Take(Math.Max(1, shelfLimit))
            .Select(work => _cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId)))
            .ToList();

        var watchCards = works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Movie")
            .Select(work => _cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId)))
            .Concat(tvShowCards.Select(card => card with { TileTextMode = "coverOnly" }))
            .OrderByDescending(card => card.SortTimestamp)
            .Take(Math.Max(1, shelfLimit))
            .ToList();

        var listenCards = musicAlbumCards
            .Concat(works
                .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Audiobook")
                .Select(work => _cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId))))
            .OrderByDescending(card => card.SortTimestamp)
            .Take(Math.Max(1, shelfLimit))
            .ToList();

        var collectionCards = homeCollections
            .Take(Math.Max(1, shelfLimit))
            .Select(DisplayCardBuilder.FromHomeCollection)
            .ToList();

        var shelves = new List<DisplayShelfDto>();
        DisplayShelfBuilder.AddShelf(shelves, "continue", "Continue Across Media", "Pick up where you left off", continueCards, null);
        DisplayShelfBuilder.AddShelf(shelves, "watch-next", "Watch", "Movies and shows ready to play", watchCards, "/watch");
        DisplayShelfBuilder.AddShelf(shelves, "read-next", "Read", "Books and comics ready to open", readCards, "/read");
        DisplayShelfBuilder.AddShelf(shelves, "listen-next", "Listen", "Music and audiobooks ready to resume", listenCards, "/listen");
        DisplayShelfBuilder.AddShelf(shelves, "home-collections", "Collections & Lists", "Curated lists and broader rollups from your library", collectionCards, "/collections");

        var spotlights = BuildHomeSpotlights(works, progressByWork, tvShowCards,
            continueCards.Concat(watchCards).Concat(readCards).Concat(listenCards).Concat(freshCandidates).ToList());

        return new DisplayPageDto(
            Key: "home",
            Title: "Home",
            Subtitle: "A cross-media view of your local library",
            Hero: spotlights.FirstOrDefault(),
            Shelves: shelves,
            Catalog: includeCatalog
                ? works.Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) is not ("TV" or "Music"))
                    .Select(work => _cards.FromWork(work, "home", progressByWork.GetValueOrDefault(work.WorkId)))
                    .Concat(tvShowCards.Select(card => card with { TileTextMode = "coverOnly" }))
                    .Concat(musicAlbumCards)
                    .ToList()
                : []) { Spotlights = spotlights };
    }

    public async Task<DisplayPageDto> BuildBrowseAsync(
        string? lane,
        string? mediaType,
        string? grouping,
        string? search,
        int offset,
        int limit,
        bool includeCatalog = true,
        Guid? profileId = null,
        CancellationToken ct = default,
        string? genres = null,
        string? creator = null,
        string? status = null,
        string? year = null,
        string? sort = null)
    {
        var normalizedLane = DisplayMediaRules.NormalizeLane(lane);
        if (normalizedLane is not null &&
            string.IsNullOrWhiteSpace(mediaType) &&
            string.IsNullOrWhiteSpace(search) &&
            (string.IsNullOrWhiteSpace(grouping) || string.Equals(grouping, "all", StringComparison.OrdinalIgnoreCase)) &&
            offset <= 0)
        {
            return await BuildLaneAsync(normalizedLane, includeCatalog, profileId, ct);
        }

        if (string.Equals(normalizedLane, "listen", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(DisplayMediaRules.NormalizeMediaType(mediaType ?? string.Empty), "Music", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(grouping, "home", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(search) &&
            offset <= 0)
        {
            return await BuildMusicHomeAsync(includeCatalog, profileId, ct);
        }

        var worksTask = _readService.LoadWorksAsync(ct);
        var journeyTask = _readService.LoadJourneyAsync(profileId, null, ct);
        var favoriteWorkIdsTask = _readService.LoadFavoriteWorkIdsAsync(profileId, ct);
        await Task.WhenAll(worksTask, journeyTask, favoriteWorkIdsTask);

        var favoriteWorkIds = await favoriteWorkIdsTask;
        var works = (await worksTask).ToList();
        var journey = (await journeyTask).ToList();
        var progressByWork = LatestProgressByWork(await _readService.LoadStatesAsync(profileId, null, ct));

        var filtered = works.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            filtered = filtered.Where(work => string.Equals(DisplayMediaRules.NormalizeMediaType(work.MediaType), DisplayMediaRules.NormalizeMediaType(mediaType), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            filtered = normalizedLane switch
            {
                "watch" => filtered.Where(work => DisplayMediaRules.IsWatchKind(work.MediaType)),
                "read" => filtered.Where(work => DisplayMediaRules.IsReadKind(work.MediaType)),
                "listen" => filtered.Where(work => DisplayMediaRules.IsListenKind(work.MediaType)),
                _ => filtered,
            };
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(work =>
                Contains(work.Title, search) ||
                Contains(work.ShowName, search) ||
                Contains(work.Author, search) ||
                Contains(work.Series, search) ||
                Contains(work.Genre, search) ||
                Contains(work.Album, search) ||
                Contains(work.Artist, search) ||
                Contains(work.Network, search) ||
                Contains(work.SearchNetwork, search) ||
                Contains(work.SearchPublisher, search) ||
                Contains(work.CountryOfOrigin, search) ||
                Contains(work.Franchise, search));
        }

        var facetSource = filtered.ToList();
        var facets = BuildBrowseFacets(facetSource);
        var requestedGenres = DisplayMediaRules.SplitValues(genres);
        if (requestedGenres.Count > 0)
        {
            filtered = filtered.Where(work =>
            {
                var workGenres = DisplayMediaRules.SplitValues(work.Genre);
                return requestedGenres.Any(requested => workGenres.Contains(requested, StringComparer.OrdinalIgnoreCase));
            });
        }

        var requestedCreators = DisplayMediaRules.SplitValues(creator);
        if (requestedCreators.Count > 0)
        {
            filtered = filtered.Where(work =>
                requestedCreators.Any(requested =>
                    ContributorValues(work).Contains(requested, StringComparer.OrdinalIgnoreCase)));
        }

        var requestedYears = DisplayMediaRules.SplitValues(year);
        if (requestedYears.Count > 0)
        {
            filtered = filtered.Where(work => requestedYears.Any(requested =>
                string.Equals(work.Year, requested, StringComparison.OrdinalIgnoreCase)));
        }

        if (string.Equals(status, "in-progress", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(work => progressByWork.TryGetValue(work.WorkId, out var progress)
                                               && progress.ProgressPct is > 0 and < 100);
        }
        else if (string.Equals(status, "not-started", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(work => !progressByWork.TryGetValue(work.WorkId, out var progress)
                                               || progress.ProgressPct <= 0);
        }
        else if (string.Equals(status, "favorite", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(work => favoriteWorkIds.Contains(work.WorkId));
        }
        else if (string.Equals(status, "finished", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(work => progressByWork.TryGetValue(work.WorkId, out var progress)
                                               && progress.ProgressPct >= 99.5);
        }

        if (string.Equals(normalizedLane, "read", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(mediaType))
        {
            filtered = CollapseReadVariantsByQid(filtered);
        }

        var filteredWorks = SortBrowseWorks(filtered, sort).ToList();
        var isTimeline = string.Equals(grouping, "timeline", StringComparison.OrdinalIgnoreCase);
        if (isTimeline)
        {
            int CanonicalYear(DisplayWorkRow work) => int.TryParse(work.Year, out var value) && value > 0 ? value : 0;
            filteredWorks = filteredWorks.OrderBy(work => CanonicalYear(work) == 0)
                .ThenBy(work => string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase) ? CanonicalYear(work) : -CanonicalYear(work))
                .ThenBy(work => work.WorkId).ToList();
        }
        var totalCount = filteredWorks.Count;

        if (isTimeline && DisplayMediaRules.NormalizeMediaType(mediaType ?? string.Empty) is "Music" or "TV")
        {
            var aggregated = DisplayMediaRules.NormalizeMediaType(mediaType!) == "Music"
                ? BuildMusicAlbumCards(filteredWorks) : _cards.BuildTvShowCards(filteredWorks, progressByWork);
            var ordered = aggregated.OrderBy(card => card.SortYear <= 0)
                .ThenBy(card => sort == "oldest" ? card.SortYear : -card.SortYear).ThenBy(card => card.Id).ToList();
            var page = ordered.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit <= 0 ? 48 : limit, 1, 200)).ToList();
            return new DisplayPageDto("browse-timeline", "Timeline", null, null, [], includeCatalog ? page : [])
            {
                TotalCount = ordered.Count, Facets = facets,
                Timeline = ordered.Select((card, index) => new { Year = Math.Max(0, card.SortYear), Index = index })
                    .GroupBy(item => item.Year).Select(group => new DisplayTimelinePeriodDto(group.Key, group.Count(), group.Min(item => item.Index))).ToList()
            };
        }

        if (ShouldReturnTvShowGroups(normalizedLane, mediaType, grouping))
        {
            var showCards = _cards.BuildTvShowCards(filteredWorks, progressByWork);
            if (showCards.Count > 0)
            {
                var pagedShowCards = showCards
                    .Skip(Math.Max(0, offset))
                    .Take(Math.Clamp(limit <= 0 ? 48 : limit, 1, 200))
                    .ToList();

                return new DisplayPageDto(
                    Key: "browse-watch-shows",
                    Title: "TV Shows",
                    Subtitle: "Shows grouped by title",
                    Hero: pagedShowCards.FirstOrDefault() is { } showHero ? DisplayCardBuilder.ToHero(showHero, "TV Shows") : null,
                    Shelves: includeCatalog ? [] : [new DisplayShelfDto("results", "TV Shows", null, pagedShowCards, null)],
                    Catalog: includeCatalog ? pagedShowCards : [])
                {
                    TotalCount = showCards.Count,
                    Facets = facets,
                };
            }
        }

        var context = normalizedLane ?? "browse";
        var pagedWorks = filteredWorks
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit <= 0 ? 48 : limit, 1, 200))
            .ToList();
        var musicStats = _musicStats is not null
                         && profileId.HasValue
                         && pagedWorks.Any(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
            ? await _musicStats.GetStatsAsync(profileId.Value, pagedWorks.Select(work => work.WorkId), ct)
            : new Dictionary<Guid, MusicPlayStat>();
        var cards = pagedWorks
            .Select(work => MarkFavorite(
                _cards.FromWork(work, context, progressByWork.GetValueOrDefault(work.WorkId)),
                favoriteWorkIds))
            .Select(card => ApplyMusicStats(card, musicStats))
            .ToList();

        return new DisplayPageDto(
            Key: $"browse-{normalizedLane ?? "all"}-{grouping ?? "all"}",
            Title: TitleForLane(lane),
            Subtitle: null,
            Hero: cards.FirstOrDefault() is { } hero ? DisplayCardBuilder.ToHero(hero, "From your library") : null,
            Shelves: includeCatalog ? [] : [new DisplayShelfDto("results", "Results", null, cards, null)],
            Catalog: includeCatalog ? cards : [])
        {
            TotalCount = totalCount,
            Facets = facets,
            Timeline = isTimeline ? filteredWorks.Select((work, index) => new { Year = int.TryParse(work.Year, out var parsed) && parsed > 0 ? parsed : 0, Index = index })
                .GroupBy(item => item.Year).Select(group => new DisplayTimelinePeriodDto(group.Key, group.Count(), group.Min(item => item.Index))).ToList() : null,
        };
    }

    public async Task<DisplayPageDto> BuildContinueAsync(
        string? lane,
        int limit,
        bool includeCatalog = true,
        CancellationToken ct = default,
        string? mediaType = null,
        Guid? profileId = null)
    {
        var normalizedLane = DisplayMediaRules.NormalizeLane(lane);
        var journey = await _readService.LoadJourneyAsync(profileId, normalizedLane, ct);
        var works = await _readService.LoadWorksAsync(ct);
        var cards = journey
            .Where(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType) != "Music")
            .Where(item => item.ProgressPct is > 0 and < 99.5)
            .Where(item => string.IsNullOrWhiteSpace(mediaType)
                           || string.Equals(
                               DisplayMediaRules.NormalizeMediaType(item.MediaType),
                               DisplayMediaRules.NormalizeMediaType(mediaType),
                               StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.LastAccessed)
            .Take(Math.Clamp(limit <= 0 ? 24 : limit, 1, 100))
            .Select(item => _cards.FromJourney(item, normalizedLane ?? "continue"))
            .Concat(string.IsNullOrWhiteSpace(mediaType) || DisplayMediaRules.NormalizeDisplayKind(mediaType) == "Music"
                ? BuildMusicJourneyAlbumCards(works, journey) : [])
            .OrderByDescending(card => card.SortTimestamp)
            .Take(Math.Clamp(limit <= 0 ? 24 : limit, 1, 100))
            .ToList();

        return new DisplayPageDto(
            Key: $"continue-{normalizedLane ?? "all"}",
            Title: "Continue",
            Subtitle: null,
            Hero: cards.FirstOrDefault() is { } hero ? DisplayCardBuilder.ToHero(hero, "Continue") : null,
            Shelves: [new DisplayShelfDto("continue", "Continue", null, cards, null)],
            Catalog: includeCatalog ? cards : []);
    }

    public Task<DisplayPageDto> BuildSearchAsync(string? query, int limit, bool includeCatalog = true, CancellationToken ct = default) =>
        BuildBrowseAsync(null, null, "all", query, 0, limit <= 0 ? 48 : limit, includeCatalog, null, ct);

    public async Task<DisplayShelfPageDto?> BuildShelfPageAsync(
        string shelfKey,
        string? lane,
        string? mediaType,
        string? grouping,
        string? search,
        string? cursor,
        int? offset,
        int limit,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(shelfKey))
        {
            return null;
        }

        var start = ResolveShelfOffset(cursor, offset);
        var take = Math.Clamp(limit <= 0 ? 24 : limit, 1, 100);
        var sourceLimit = start + take + 1;
        var page = string.IsNullOrWhiteSpace(lane)
                   && string.IsNullOrWhiteSpace(mediaType)
                   && string.IsNullOrWhiteSpace(grouping)
                   && string.IsNullOrWhiteSpace(search)
            ? await BuildHomeAsync(includeCatalog: false, profileId: profileId, ct: ct, shelfLimit: sourceLimit)
            : await BuildShelfSourcePageAsync(lane, mediaType, grouping, search, sourceLimit, profileId, ct);

        var shelf = page.Shelves.FirstOrDefault(item => string.Equals(item.Key, shelfKey, StringComparison.OrdinalIgnoreCase));
        if (shelf is null)
        {
            return null;
        }

        var items = shelf.Items;
        var pagedItems = items.Skip(start).Take(take).ToList();
        var nextOffset = start + pagedItems.Count;
        var hasMore = nextOffset < items.Count;
        var nextCursor = hasMore ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        return new DisplayShelfPageDto(
            shelf with { Items = pagedItems },
            nextCursor,
            start,
            take,
            hasMore);
    }

    public async Task<DisplayPageDto?> BuildGroupAsync(
        Guid groupId,
        bool includeCatalog = true,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        var works = CollapseReadVariantsByQid((await _readService.LoadWorksAsync(ct))
            .Where(work => work.CollectionId == groupId)
        )
            .OrderBy(work => DisplayMediaRules.ParseDouble(work.SeriesPosition) ?? double.MaxValue)
            .ThenBy(work => work.SortTitle ?? work.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (works.Count == 0)
        {
            return null;
        }

        var cards = works.Select(work => _cards.FromWork(work, "collection", null)).ToList();
        var title = works.FirstOrDefault(work => !string.IsNullOrWhiteSpace(work.Series))?.Series ?? "Collection";
        return new DisplayPageDto(
            Key: $"group-{groupId:N}",
            Title: title,
            Subtitle: $"{works.Count} items",
            Hero: cards.FirstOrDefault() is { } hero ? DisplayCardBuilder.ToHero(hero, "Collection") : null,
            Shelves: [new DisplayShelfDto("items", title, $"{works.Count} items", cards, null)],
            Catalog: includeCatalog ? cards : []);
    }

    private Task<DisplayPageDto> BuildShelfSourcePageAsync(
        string? lane,
        string? mediaType,
        string? grouping,
        string? search,
        int shelfLimit,
        Guid? profileId,
        CancellationToken ct)
    {
        var normalizedLane = DisplayMediaRules.NormalizeLane(lane);
        if (string.Equals(normalizedLane, "listen", StringComparison.OrdinalIgnoreCase)
            && string.Equals(DisplayMediaRules.NormalizeMediaType(mediaType ?? string.Empty), "Music", StringComparison.OrdinalIgnoreCase)
            && string.Equals(grouping, "home", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(search))
        {
            return BuildMusicHomeAsync(includeCatalog: false, profileId, ct, shelfLimit);
        }

        if (normalizedLane is not null
            && string.IsNullOrWhiteSpace(mediaType)
            && string.IsNullOrWhiteSpace(search)
            && (string.IsNullOrWhiteSpace(grouping) || string.Equals(grouping, "all", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildLaneAsync(normalizedLane, includeCatalog: false, profileId, ct, shelfLimit);
        }

        return BuildBrowseAsync(lane, mediaType, grouping, search, 0, Math.Max(1, shelfLimit), includeCatalog: false, profileId, ct);
    }

    private async Task<DisplayPageDto> BuildLaneAsync(string lane, bool includeCatalog, Guid? profileId, CancellationToken ct, int shelfLimit = 18)
    {
        var worksTask = _readService.LoadWorksAsync(ct);
        var journeyTask = _readService.LoadJourneyAsync(profileId, lane, ct);
        await Task.WhenAll(worksTask, journeyTask);

        var works = (await worksTask).ToList();
        var journey = (await journeyTask).ToList();
        var progressByWork = LatestProgressByWork(await _readService.LoadStatesAsync(profileId, null, ct));

        var laneSource = works
            .Where(work => lane switch
            {
                "watch" => DisplayMediaRules.IsWatchKind(work.MediaType),
                "read" => DisplayMediaRules.IsReadKind(work.MediaType),
                "listen" => DisplayMediaRules.IsListenKind(work.MediaType),
                _ => true,
            });

        if (string.Equals(lane, "read", StringComparison.OrdinalIgnoreCase))
        {
            laneSource = CollapseReadVariantsByQid(laneSource);
        }

        var laneWorks = laneSource
            .OrderByDescending(work => work.CreatedAt)
            .ThenByDescending(work => DisplayMediaRules.ParseDouble(work.Year) ?? 0)
            .ToList();

        var laneJourney = journey
            .OrderByDescending(item => item.LastAccessed)
            .ToList();

        var catalog = laneWorks
            .Select(work => _cards.FromWork(work, lane, progressByWork.GetValueOrDefault(work.WorkId)))
            .ToList();

        var tvShowCards = string.Equals(lane, "watch", StringComparison.OrdinalIgnoreCase)
            ? _cards.BuildTvShowCards(laneWorks, progressByWork)
            : [];

        var shelves = lane switch
        {
            "watch" => _shelves.BuildWatchShelves(laneWorks, laneJourney, progressByWork, shelfLimit),
            "read" => _shelves.BuildReadShelves(laneWorks, laneJourney, progressByWork, shelfLimit),
            "listen" => BuildAlbumFirstListenShelves(laneWorks, laneJourney, progressByWork, shelfLimit),
            _ => [new DisplayShelfDto("results", "Results", null, catalog, null)],
        };

        var heroCard = string.Equals(lane, "listen", StringComparison.OrdinalIgnoreCase)
            ? shelves.SelectMany(shelf => shelf.Items).FirstOrDefault()
            : laneJourney.FirstOrDefault() is { } journeyHero
            ? PromoteTvJourneyToShowHero(journeyHero, _cards.FromJourney(journeyHero, lane), tvShowCards)
            : string.Equals(lane, "watch", StringComparison.OrdinalIgnoreCase)
                ? shelves.SelectMany(shelf => shelf.Items).FirstOrDefault()
                : catalog.FirstOrDefault();

        return new DisplayPageDto(
            Key: lane,
            Title: TitleForLane(lane),
            Subtitle: SubtitleForLane(lane),
            Hero: heroCard is null ? null : DisplayCardBuilder.ToHero(heroCard, EyebrowForLane(lane, laneJourney.Count > 0)),
            Shelves: shelves,
            Catalog: includeCatalog ? catalog : []);
    }

    private static DisplayCardDto PromoteTvJourneyToShowHero(
        DisplayJourneyRow journey,
        DisplayCardDto journeyCard,
        IReadOnlyList<DisplayCardDto> tvShowCards)
    {
        if (DisplayMediaRules.NormalizeDisplayKind(journey.MediaType) != "TV"
            || journey.RootWorkId == Guid.Empty)
        {
            return journeyCard;
        }

        var showCard = tvShowCards.FirstOrDefault(card => card.Id == journey.RootWorkId);
        if (showCard is null)
        {
            return journeyCard;
        }

        // Lane heroes represent the show, even when their primary action resumes a
        // specific owned episode. This keeps episode stills on episode/Continue cards.
        return showCard with
        {
            Progress = journeyCard.Progress,
            Subject = DisplaySubjectKind.TvShow,
            ContinuationState = journeyCard.ContinuationState,
            Actions = [.. journeyCard.Actions, .. showCard.Actions],
            SortTimestamp = journeyCard.SortTimestamp,
        };
    }

    private async Task<DisplayPageDto> BuildMusicHomeAsync(bool includeCatalog, Guid? profileId, CancellationToken ct, int shelfLimit = 18)
    {
        var worksTask = _readService.LoadWorksAsync(ct);
        var journeyTask = _readService.LoadJourneyAsync(profileId, "listen", ct);
        var favoriteWorkIdsTask = _readService.LoadFavoriteWorkIdsAsync(profileId, ct);
        await Task.WhenAll(worksTask, journeyTask, favoriteWorkIdsTask);

        var works = (await worksTask)
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
            .OrderByDescending(work => work.CreatedAt)
            .ThenBy(work => work.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(work => work.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(work => DisplayMediaRules.ParseDouble(work.TrackNumber) ?? double.MaxValue)
            .ThenBy(work => work.SortTitle ?? work.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var journey = (await journeyTask)
            .Where(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType) == "Music")
            .OrderByDescending(item => item.LastAccessed)
            .ToList();
        var favoriteWorkIds = await favoriteWorkIdsTask;
        var progressByWork = LatestProgressByWork(await _readService.LoadStatesAsync(profileId, null, ct));
        var catalog = works
            .Select(work => MarkFavorite(_cards.FromWork(work, "listen", progressByWork.GetValueOrDefault(work.WorkId)), favoriteWorkIds))
            .ToList();

        var shelves = new List<DisplayShelfDto>();
        DisplayShelfBuilder.AddShelf(shelves, "recently-played", "Recently Played", "Pick up the albums from your latest listening sessions",
            BuildMusicJourneyAlbumCards(works, journey).Take(Math.Max(1, shelfLimit)).ToList(), "/listen/music");
        DisplayShelfBuilder.AddShelf(shelves, "favorite-songs", "Favorite Songs", "Tracks you marked as favorites",
            catalog.Where(card => card.WorkId.HasValue && favoriteWorkIds.Contains(card.WorkId.Value)).Take(Math.Max(1, shelfLimit)).ToList(), "/listen/music/playlists/system/favorite-songs");
        DisplayShelfBuilder.AddShelf(shelves, "new-tracks-added", "New tracks added", "Recently added tracks grouped by album",
            BuildNewTrackAlbumCards(works, shelfLimit).ToList(), "/listen/music?sort=newest");
        DisplayShelfBuilder.AddShelf(shelves, "albums", "Albums", "Album-first browsing with cover art at the center",
            BuildMusicAlbumCards(works).Take(Math.Max(1, shelfLimit)).ToList(), "/listen/music");
        DisplayShelfBuilder.AddShelf(shelves, "artists", "Artists", "Artist-led listening built from your library",
            BuildMusicArtistCards(works).Take(Math.Max(1, shelfLimit)).ToList(), "/listen/music?browse=artists");

        var heroCard = BuildMusicJourneyAlbumCards(works, journey).FirstOrDefault()
            ?? BuildMusicAlbumCards(works).FirstOrDefault()
            ?? catalog.FirstOrDefault();

        return new DisplayPageDto(
            Key: "listen-music",
            Title: "Music",
            Subtitle: "Albums, artists, songs, and playlists from your local library",
            Hero: heroCard is null ? null : DisplayCardBuilder.ToHero(heroCard, journey.Count > 0 ? "Recently Played" : "Featured album"),
            Shelves: shelves,
            Catalog: includeCatalog ? catalog : []);
    }

    private IReadOnlyList<DisplayCardDto> BuildMusicAlbumCards(IReadOnlyList<DisplayWorkRow> works) =>
        works
            .Where(work => !string.IsNullOrWhiteSpace(work.Album))
            .GroupBy(work => AlbumGroupKey(work), StringComparer.OrdinalIgnoreCase)
            .Select(group => ToMusicAlbumCard(group.ToList()))
            .Where(card => card is not null)
            .Cast<DisplayCardDto>()
            .OrderByDescending(card => card.SortTimestamp)
            .ToList();

    private IReadOnlyList<DisplayCardDto> BuildMusicJourneyAlbumCards(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyList<DisplayJourneyRow> journey)
    {
        var worksByAlbum = works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
            .Where(work => !string.IsNullOrWhiteSpace(work.Album))
            .GroupBy(AlbumGroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DisplayWorkRow>)group.ToList(), StringComparer.OrdinalIgnoreCase);

        return journey
            .Where(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType) == "Music")
            .Where(item => !string.IsNullOrWhiteSpace(item.Album))
            .GroupBy(AlbumGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                if (!worksByAlbum.TryGetValue(group.Key, out var albumWorks)
                    || ToMusicAlbumCard(albumWorks) is not { } albumCard)
                {
                    return null;
                }

                var latestTrack = group.OrderByDescending(item => item.LastAccessed).First();
                var journeyCard = _cards.FromJourney(latestTrack, "listen");
                var actions = journeyCard.Actions
                    .Concat(albumCard.Actions)
                    .DistinctBy(action => $"{action.Type}|{action.WebUrl}|{action.WorkId}|{action.CollectionId}", StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return albumCard with
                {
                    Progress = null,
                    Subject = DisplaySubjectKind.Album,
                    ContinuationState = DisplayContinuationState.Unstarted,
                    Actions = actions,
                    SortTimestamp = latestTrack.LastAccessed,
                };
            })
            .Where(card => card is not null)
            .Cast<DisplayCardDto>()
            .OrderByDescending(card => card.SortTimestamp)
            .ToList();
    }

    private IReadOnlyList<DisplayCardDto> BuildNewTrackAlbumCards(IReadOnlyList<DisplayWorkRow> works, int shelfLimit)
    {
        var musicWorks = works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
            .Where(work => !string.IsNullOrWhiteSpace(work.Album))
            .ToList();
        var worksByAlbum = musicWorks
            .GroupBy(AlbumGroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DisplayWorkRow>)group.ToList(), StringComparer.OrdinalIgnoreCase);

        return musicWorks
            .OrderByDescending(work => work.CreatedAt)
            .Take(Math.Max(8, Math.Max(1, shelfLimit) * 4))
            .GroupBy(AlbumGroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                if (!worksByAlbum.TryGetValue(group.Key, out var albumWorks)
                    || ToMusicAlbumCard(albumWorks) is not { } albumCard)
                {
                    return null;
                }

                var newTrackCount = group.Select(work => work.WorkId).Distinct().Count();
                var additionFact = $"{newTrackCount} new {(newTrackCount == 1 ? "track" : "tracks")} added";
                return albumCard with
                {
                    Facts = [additionFact, .. albumCard.Facts],
                    SortTimestamp = group.Max(work => work.CreatedAt),
                };
            })
            .Where(card => card is not null)
            .Cast<DisplayCardDto>()
            .OrderByDescending(card => card.SortTimestamp)
            .Take(Math.Max(1, shelfLimit))
            .ToList();
    }

    private IReadOnlyList<DisplayShelfDto> BuildAlbumFirstListenShelves(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyList<DisplayJourneyRow> journey,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> progressByWork,
        int shelfLimit)
    {
        var take = Math.Max(1, shelfLimit);
        var musicWorks = works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Music")
            .ToList();
        var audiobookWorks = works
            .Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Audiobook")
            .OrderByDescending(work => work.CreatedAt)
            .ToList();
        var continueCards = journey
            .Where(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType) == "Audiobook")
            .Where(item => item.ProgressPct is > 0 and < 99.5)
            .GroupBy(item => item.WorkId)
            .Select(group => group.OrderByDescending(item => item.LastAccessed).First())
            .Select(item => _cards.FromJourney(item, "listen"))
            .OrderByDescending(card => card.SortTimestamp)
            .Take(take)
            .ToList();
        var recentlyPlayedAlbums = BuildMusicJourneyAlbumCards(musicWorks, journey)
            .Take(take)
            .ToList();
        var occupiedAlbumIdentities = DisplayCardPlacementPolicy.CollectIdentities(recentlyPlayedAlbums);
        var recentlyUpdatedAlbums = DisplayCardPlacementPolicy.TakeUnplaced(
            BuildNewTrackAlbumCards(musicWorks, take),
            occupiedAlbumIdentities,
            take);
        var occupiedAudiobookIdentities = DisplayCardPlacementPolicy.CollectIdentities(continueCards);
        var recentlyAddedAudiobooks = DisplayCardPlacementPolicy.TakeUnplaced(
            audiobookWorks.Select(work => _cards.FromWork(
                work,
                "listen",
                progressByWork.GetValueOrDefault(work.WorkId))),
            occupiedAudiobookIdentities,
            take);

        var shelves = new List<DisplayShelfDto>();
        DisplayShelfBuilder.AddShelf(shelves, "continue-listening", "Continue Listening", "Resume audiobooks already in progress",
            continueCards, "/listen/audiobooks?status=in-progress");
        DisplayShelfBuilder.AddShelf(shelves, "recently-played", "Recently Played", "Albums from your latest music sessions",
            recentlyPlayedAlbums, "/listen/music");
        DisplayShelfBuilder.AddShelf(shelves, "recently-updated-albums", "Recently Updated Albums", "Albums that received newly added tracks",
            recentlyUpdatedAlbums, "/listen/music?sort=newest");
        DisplayShelfBuilder.AddShelf(shelves, "recently-added-audiobooks", "Recently Added Audiobooks", "New spoken-word titles in your library",
            recentlyAddedAudiobooks, "/listen/audiobooks?sort=newest");
        DisplayShelfBuilder.AddShelf(shelves, "audiobook-series", "Audiobook series", "Grouped audiobook runs from your library",
            _cards.BuildCollectionCards(audiobookWorks, "listen", progressByWork: progressByWork).Take(take).ToList(),
            "/listen/audiobooks?browse=series");
        return shelves;
    }

    private IReadOnlyList<DisplayCardDto> BuildMusicArtistCards(IReadOnlyList<DisplayWorkRow> works) =>
        works
            .Select(work => new { Artist = StringHelpers.FirstNonBlank(work.Artist, work.Author), Work = work })
            .Where(item => !string.IsNullOrWhiteSpace(item.Artist))
            .GroupBy(item => item.Artist!, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToMusicArtistCard(group.Key, group.Select(item => item.Work).ToList()))
            .OrderByDescending(card => card.SortTimestamp)
            .ToList();

    private static DisplayCardDto MarkFavorite(DisplayCardDto card, IReadOnlySet<Guid> favoriteWorkIds)
    {
        if (!card.WorkId.HasValue || !favoriteWorkIds.Contains(card.WorkId.Value))
        {
            return card;
        }

        return card with { Flags = card.Flags with { IsFavorite = true } };
    }

    private static DisplayCardDto ApplyMusicStats(
        DisplayCardDto card,
        IReadOnlyDictionary<Guid, MusicPlayStat> stats)
    {
        if (card.WorkId is not Guid workId
            || card.ListMetadata is null
            || !stats.TryGetValue(workId, out var stat))
        {
            return card;
        }

        return card with
        {
            ListMetadata = card.ListMetadata with
            {
                PlayCount = stat.PlayCount,
                LastPlayedAt = stat.LastPlayedAt,
            },
        };
    }

    private static DisplayCardDto? ToMusicAlbumCard(IReadOnlyList<DisplayWorkRow> works)
    {
        if (works.Count == 0)
        {
            return null;
        }

        var representative = works
            .OrderByDescending(work => !string.IsNullOrWhiteSpace(work.SquareUrl) || !string.IsNullOrWhiteSpace(work.CoverUrl))
            .ThenByDescending(work => work.CreatedAt)
            .First();
        var title = representative.Album ?? representative.Title;
        var artist = StringHelpers.FirstNonBlank(representative.Artist, representative.Author);
        // Album grouping is a system view rooted at the canonical album work.
        // A track's collection can be a broader/shared container and must not
        // become the album identity.
        var albumDetailId = representative.RootWorkId != Guid.Empty
            ? representative.RootWorkId
            : representative.WorkId;
        var route = $"/details/musicalbum/{albumDetailId:D}?context=listen";
        var action = new DisplayActionDto("playAlbum", "Play Album", representative.WorkId, representative.AssetId, representative.CollectionId, route);

        return new DisplayCardDto(
            Id: albumDetailId,
            WorkId: representative.WorkId,
            AssetId: representative.AssetId,
            CollectionId: representative.CollectionId,
            MediaType: "Music",
            GroupingType: "album",
            Title: title,
            Subtitle: artist,
            Facts: AlbumFacts(works, artist, representative.Year, representative.Genre, representative.Rating),
            Artwork: ArtworkFor(representative, representative.AssetId),
            PreferredShape: "square",
            Presentation: "album",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, true, false),
            SortTimestamp: works.Max(work => work.CreatedAt))
        {
            Subject = DisplaySubjectKind.Album,
            SortYear = int.TryParse(representative.Year, out var albumYear) && albumYear > 0 ? albumYear : 0,
            Tagline = representative.Tagline,
            Description = representative.Description,
            Genres = works
                .SelectMany(work => DisplayMediaRules.SplitValues(work.Genre))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PreviewItems = works
                .OrderBy(work => DisplayMediaRules.ParseDouble(work.TrackNumber) ?? double.MaxValue)
                .ThenBy(work => work.SortTitle ?? work.Title, StringComparer.OrdinalIgnoreCase)
                .Select(work => new DisplayCardPreviewItemDto(
                    work.WorkId,
                    work.AssetId,
                    work.Title,
                    StringHelpers.FirstNonBlank(work.SquareSmallUrl, work.CoverSmallUrl, work.SquareUrl, work.CoverUrl) ?? string.Empty,
                    "square",
                    work.TrackNumber,
                    "Music",
                    $"/listen/music?browse=songs&track={work.WorkId:D}",
                    work.Description,
                    [.. AlbumFacts([work], artist, work.Year, work.Genre, work.Rating)]))
                .ToList(),
            PreviewTotalCount = works.Count,
        };
    }

    private static DisplayCardDto ToMusicArtistCard(string artist, IReadOnlyList<DisplayWorkRow> works)
    {
        var representative = works
            .OrderByDescending(work => !string.IsNullOrWhiteSpace(work.SquareUrl) || !string.IsNullOrWhiteSpace(work.CoverUrl))
            .ThenByDescending(work => work.CreatedAt)
            .First();
        var artistPersonId = works
            .Where(work => work.ArtistPersonId.HasValue)
            .OrderByDescending(work => string.Equals(work.ArtistPersonName, artist, StringComparison.OrdinalIgnoreCase))
            .ThenBy(work => work.ArtistPersonName, StringComparer.OrdinalIgnoreCase)
            .Select(work => work.ArtistPersonId)
            .FirstOrDefault();
        var albumCount = works
            .Select(work => work.Album)
            .Where(album => !string.IsNullOrWhiteSpace(album))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var route = artistPersonId.HasValue
            ? $"/details/person/{artistPersonId.Value:D}?context=listen"
            : $"/search?q={Uri.EscapeDataString(artist)}&type=person";
        var action = new DisplayActionDto("openPerson", "Open artist", representative.WorkId, representative.AssetId, representative.CollectionId, route);

        return new DisplayCardDto(
            Id: representative.CollectionId ?? representative.WorkId,
            WorkId: representative.WorkId,
            AssetId: representative.AssetId,
            CollectionId: representative.CollectionId,
            MediaType: "Music",
            GroupingType: "artist",
            Title: artist,
            Subtitle: $"{works.Count} tracks",
            Facts: ArtistFacts(albumCount, works.Count, representative.Genre),
            Artwork: ArtworkFor(representative, representative.AssetId),
            PreferredShape: "square",
            Presentation: "artist",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, true, false),
            SortTimestamp: works.Max(work => work.CreatedAt))
        {
            Genres = works
                .SelectMany(work => DisplayMediaRules.SplitValues(work.Genre))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static IOrderedEnumerable<DisplayWorkRow> SortBrowseWorks(
        IEnumerable<DisplayWorkRow> works,
        string? sort) =>
        sort?.Trim().ToLowerInvariant() switch
        {
            "title" => works
                .OrderBy(work => StringHelpers.FirstNonBlank(work.SortTitle, work.Title), StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(work => work.CreatedAt),
            "oldest" => works
                .OrderBy(work => work.CreatedAt)
                .ThenBy(work => StringHelpers.FirstNonBlank(work.SortTitle, work.Title), StringComparer.OrdinalIgnoreCase),
            "creator" => works
                .OrderBy(work => StringHelpers.FirstNonBlank(work.Artist, work.Author, work.Director, work.Narrator), StringComparer.OrdinalIgnoreCase)
                .ThenBy(work => StringHelpers.FirstNonBlank(work.SortTitle, work.Title), StringComparer.OrdinalIgnoreCase),
            "year" => works
                .OrderByDescending(work => DisplayMediaRules.ParseDouble(work.Year) ?? 0)
                .ThenBy(work => StringHelpers.FirstNonBlank(work.SortTitle, work.Title), StringComparer.OrdinalIgnoreCase),
            _ => works
                .OrderByDescending(work => work.CreatedAt)
                .ThenBy(work => StringHelpers.FirstNonBlank(work.SortTitle, work.Title), StringComparer.OrdinalIgnoreCase),
        };

    private static DisplayBrowseFacetsDto BuildBrowseFacets(IEnumerable<DisplayWorkRow> works)
    {
        var source = works.ToList();
        var genres = source
            .SelectMany(work => DisplayMediaRules.SplitValues(work.Genre))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .ToList();
        var creators = source
            .SelectMany(ContributorValues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var years = source
            .Select(work => work.Year)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DisplayBrowseFacetsDto(genres, creators, years);
    }

    private static IReadOnlyList<string> ContributorValues(DisplayWorkRow work)
        => new[] { work.Author, work.Artist, work.Director }
            .SelectMany(DisplayMediaRules.SplitValues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyDictionary<Guid, DisplayJourneyRow> LatestProgressByWork(IReadOnlyList<DisplayJourneyRow> journey) =>
        journey
            .GroupBy(item => item.WorkId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastAccessed).First());

    private static IEnumerable<DisplayWorkRow> CollapseReadVariantsByQid(IEnumerable<DisplayWorkRow> works) =>
        works
            .GroupBy(ReadVariantKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() == 1
                ? group.First()
                : group
                    .OrderBy(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "Audiobook" ? 1 : 0)
                    .ThenByDescending(HasPrimaryArtwork)
                    .ThenByDescending(work => work.CreatedAt)
                    .First());

    private static string ReadVariantKey(DisplayWorkRow work)
    {
        if (DisplayMediaRules.IsReadKind(work.MediaType)
            && !string.IsNullOrWhiteSpace(work.IdentityQid)
            && !work.IdentityQid.StartsWith("NF", StringComparison.OrdinalIgnoreCase))
        {
            return $"qid:{work.IdentityQid.Trim()}";
        }

        return $"work:{work.WorkId:N}";
    }

    private static bool HasPrimaryArtwork(DisplayWorkRow work) =>
        !string.IsNullOrWhiteSpace(work.CoverUrl)
        || !string.IsNullOrWhiteSpace(work.SquareUrl)
        || !string.IsNullOrWhiteSpace(work.BannerUrl)
        || !string.IsNullOrWhiteSpace(work.BackgroundUrl);

    private static string TitleForLane(string? lane) => DisplayMediaRules.NormalizeLane(lane) switch
    {
        "watch" => "Watch",
        "read" => "Read",
        "listen" => "Listen",
        _ => "Browse",
    };

    private static string SubtitleForLane(string lane) => lane switch
    {
        "watch" => "Movies and shows from your local library",
        "read" => "Books and comics from your local library",
        "listen" => "Music and audiobooks from your local library",
        _ => "Browse your local library",
    };

    private static string EyebrowForLane(string lane, bool hasProgress) => lane switch
    {
        "watch" => hasProgress ? "Continue watching" : "Featured from your library",
        "read" => hasProgress ? "Continue reading" : "New on your shelf",
        "listen" => hasProgress ? "Continue listening" : "Featured from your library",
        _ => "From your library",
    };

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldReturnTvShowGroups(string? normalizedLane, string? mediaType, string? grouping) =>
        string.Equals(grouping, "shows", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(normalizedLane, "watch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(DisplayMediaRules.NormalizeMediaType(mediaType ?? string.Empty), "TV", StringComparison.OrdinalIgnoreCase));

    private static int ResolveShelfOffset(string? cursor, int? offset)
    {
        if (!string.IsNullOrWhiteSpace(cursor)
            && int.TryParse(cursor, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedCursor))
        {
            return Math.Max(0, parsedCursor);
        }

        return Math.Max(0, offset ?? 0);
    }

    private static string AlbumGroupKey(DisplayWorkRow work) =>
        AlbumGroupKey(work.WorkId, work.RootWorkId, work.Album, StringHelpers.FirstNonBlank(work.Artist, work.Author));

    private static string AlbumGroupKey(DisplayJourneyRow item) =>
        AlbumGroupKey(item.WorkId, item.RootWorkId, item.Album, StringHelpers.FirstNonBlank(item.Artist, item.Author));

    private static string AlbumGroupKey(Guid workId, Guid rootWorkId, string? album, string? artist) =>
        rootWorkId != Guid.Empty && rootWorkId != workId
            ? $"root:{rootWorkId:N}"
            : $"local:{album?.Trim()}|{artist?.Trim()}";

    private static DisplayArtworkDto ArtworkFor(IDisplayArtworkRow row, Guid? assetId = null)
    {
        var hasPrimaryArtwork = !string.IsNullOrWhiteSpace(row.CoverUrl)
                                || !string.IsNullOrWhiteSpace(row.CoverSmallUrl)
                                || !string.IsNullOrWhiteSpace(row.CoverMediumUrl)
                                || !string.IsNullOrWhiteSpace(row.CoverLargeUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareSmallUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareMediumUrl)
                                || !string.IsNullOrWhiteSpace(row.SquareLargeUrl);
        var managedCoverFallback = !hasPrimaryArtwork && assetId.HasValue
            ? $"/stream/{assetId.Value:D}/cover"
            : null;

        return new DisplayArtworkDto(
            StringHelpers.FirstNonBlank(row.CoverUrl, managedCoverFallback),
            row.CoverSmallUrl,
            row.CoverMediumUrl,
            row.CoverLargeUrl,
            row.SquareUrl,
            row.SquareSmallUrl,
            row.SquareMediumUrl,
            row.SquareLargeUrl,
            row.BannerUrl,
            row.BannerSmallUrl,
            row.BannerMediumUrl,
            row.BannerLargeUrl,
            row.BackgroundUrl,
            row.BackgroundSmallUrl,
            row.BackgroundMediumUrl,
            row.BackgroundLargeUrl,
            row.LogoUrl,
            ParseInt(row.CoverWidthPx),
            ParseInt(row.CoverHeightPx),
            ParseInt(row.SquareWidthPx),
            ParseInt(row.SquareHeightPx),
            ParseInt(row.BannerWidthPx),
            ParseInt(row.BannerHeightPx),
            ParseInt(row.BackgroundWidthPx),
            ParseInt(row.BackgroundHeightPx),
            row.AccentColor);
    }

    private static IReadOnlyList<string> AlbumFacts(IReadOnlyList<DisplayWorkRow> works, string? artist, string? year, string? genre, string? rating)
    {
        var facts = new List<string>();
        AddFact(facts, artist);
        AddFact(facts, year);
        AddFact(facts, $"{works.Count} tracks");
        AddFact(facts, TotalMusicDuration(works));
        AddFact(facts, DisplayFactBuilder.Build("Music", string.Empty, rating: rating).FirstOrDefault());
        foreach (var item in DisplayMediaRules.SplitValues(genre).Take(2))
        {
            AddFact(facts, item);
        }

        return facts;
    }

    private static string? TotalMusicDuration(IReadOnlyList<DisplayWorkRow> works)
    {
        var total = TimeSpan.Zero;
        var found = false;
        foreach (var duration in works.Select(work => work.Duration).Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (double.TryParse(duration, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
            {
                total += TimeSpan.FromMilliseconds(milliseconds);
                found = true;
                continue;
            }

            if (TimeSpan.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                total += parsed;
                found = true;
            }
        }

        if (!found)
        {
            return null;
        }

        if (total.TotalHours >= 1)
        {
            var hours = (int)Math.Floor(total.TotalHours);
            return total.Minutes > 0 ? $"{hours}h {total.Minutes}m" : $"{hours}h";
        }

        return $"{Math.Max(1, (int)Math.Round(total.TotalMinutes))} min";
    }

    private static IReadOnlyList<string> ArtistFacts(int albumCount, int trackCount, string? genre)
    {
        var facts = new List<string>();
        if (albumCount > 0)
        {
            AddFact(facts, $"{albumCount} albums");
        }

        AddFact(facts, $"{trackCount} tracks");
        foreach (var item in DisplayMediaRules.SplitValues(genre).Take(2))
        {
            AddFact(facts, item);
        }

        return facts;
    }

    private static void AddFact(List<string> facts, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var cleaned = value.Trim();
        if (!facts.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            facts.Add(cleaned);
        }
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}
