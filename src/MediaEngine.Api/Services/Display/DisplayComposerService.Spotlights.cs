using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Display;

public sealed partial class DisplayComposerService
{
    private IReadOnlyList<DisplayHeroDto> BuildHomeSpotlights(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> states,
        IReadOnlyList<DisplayCardDto> shows,
        IReadOnlyList<DisplayCardDto> continueCards,
        IReadOnlyList<DisplayCardDto> candidates,
        ProfileHeroSettings? settings = null)
    {
        settings ??= ProfileHeroSettings.Default;
        if (!settings.Enabled)
        {
            return [];
        }

        // A hero is the first thing on Home, so it only features items that have finished settling and
        // carry real artwork. Anything still being identified waits for the shelves.
        var heroReady = works
            .Where(work => !work.IsUpdatingDetails && HasStoredArtwork(work))
            .SelectMany(work => new[] { work.WorkId, work.RootWorkId })
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        bool IsHeroReady(DisplayCardDto card) => !card.IsSettling
            && (heroReady.Contains(card.Id) || (card.WorkId is { } workId && heroReady.Contains(workId)));
        candidates = candidates.Where(IsHeroReady).ToList();

        var showHeroes = new List<DisplayHeroDto>();
        foreach (var show in shows.Where(IsHeroReady))
        {
            var owned = works.Where(work => DisplayMediaRules.NormalizeDisplayKind(work.MediaType) == "TV"
                && (work.RootWorkId == Guid.Empty ? work.WorkId : work.RootWorkId) == show.Id && work.AssetId != Guid.Empty)
                .DistinctBy(work => work.WorkId).ToList();
            var context = TvEpisodeContextResolver.Resolve(owned.Select(work => new TvEpisodePlaybackCandidate(
                work.WorkId.ToString("D"), Number(work.SeasonNumber), Number(work.EpisodeNumber),
                states.GetValueOrDefault(work.WorkId)?.ProgressPct ?? 0,
                states.GetValueOrDefault(work.WorkId)?.LastAccessed.ToString("O", CultureInfo.InvariantCulture))));
            var target = owned.FirstOrDefault(work => work.WorkId.ToString("D") == context.Target?.Id);
            if (target is null)
            {
                continue;
            }
            var state = states.GetValueOrDefault(target.WorkId);
            var episode = state is null ? _cards.FromWork(target, "home", null) : _cards.FromJourney(state, "home");
            var episodeIdentity = episode.EpisodeContext! with { ShowWorkId = show.Id, ShowTitle = show.Title };
            var action = episode.Actions[0] with
            {
                Label = context.Reason switch
                {
                    TvEpisodeSelectionReason.Resume => "Resume Episode",
                    TvEpisodeSelectionReason.NextOwned or TvEpisodeSelectionReason.RemainingOwned => "Watch Next Episode",
                    TvEpisodeSelectionReason.AllOwnedCompleted => "Restart Episode",
                    _ => "Start watching",
                }
            };
            if (context.Reason == TvEpisodeSelectionReason.AllOwnedCompleted)
            {
                action = action with { WebUrl = $"/watch/player/{target.WorkId:D}?restart=true" };
            }
            var usesEpisodePresentation = context.UsesEpisodeArtwork
                || context.Reason == TvEpisodeSelectionReason.AllOwnedCompleted;
            var details = new DisplayActionDto("openWork", "Details", target.WorkId, null, null,
                usesEpisodePresentation ? TvEpisodeDetailRoute.Build(show.Id, target.WorkId)
                    : $"/details/tvshow/{show.Id:D}?context=watch");
            var hero = DisplayCardBuilder.ToHero(show, context.UsesEpisodeArtwork ? "Continue Watching" : "Featured Content") with
            {
                WorkId = target.WorkId,
                Subject = DisplaySubjectKind.TvShow,
                ContinuationState = episode.ContinuationState,
                EpisodeContext = episodeIdentity,
                Actions = [action, details],
                Facts = episode.Facts,
            };
            if (usesEpisodePresentation)
            {
                var art = episode.Artwork with
                {
                    BackgroundUrl = state?.EpisodeStillUrl ?? target.EpisodeStillUrl,
                    BackgroundSmallUrl = state?.EpisodeStillSmallUrl ?? target.EpisodeStillSmallUrl,
                    BackgroundMediumUrl = state?.EpisodeStillMediumUrl ?? target.EpisodeStillMediumUrl,
                    BackgroundLargeUrl = state?.EpisodeStillLargeUrl ?? target.EpisodeStillLargeUrl,
                };
                var hasStill = !string.IsNullOrWhiteSpace(art.BackgroundUrl);
                hero = hero with
                {
                    Subtitle = string.Join(" · ", new[] { EpisodeNumber(episodeIdentity), episodeIdentity.EpisodeTitle }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    Description = episode.Description,
                    Progress = episode.ContinuationState == DisplayContinuationState.InProgress ? episode.Progress : null,
                    Artwork = show.Artwork with
                    {
                        BackgroundUrl = hasStill ? art.BackgroundUrl : show.Artwork.BackgroundUrl,
                        BackgroundSmallUrl = hasStill ? art.BackgroundSmallUrl : show.Artwork.BackgroundSmallUrl,
                        BackgroundMediumUrl = hasStill ? art.BackgroundMediumUrl : show.Artwork.BackgroundMediumUrl,
                        BackgroundLargeUrl = hasStill ? art.BackgroundLargeUrl : show.Artwork.BackgroundLargeUrl,
                        // Still-specific native dimensions are not projected; show
                        // backdrop dimensions cannot certify an episode rendition.
                        BackgroundWidthPx = hasStill ? null : show.Artwork.BackgroundWidthPx,
                        BackgroundHeightPx = hasStill ? null : show.Artwork.BackgroundHeightPx,
                    },
                };
            }
            showHeroes.Add(hero);
        }

        DateTimeOffset? LastStarted(DisplayHeroDto hero) => works
            .Where(work => work.RootWorkId == hero.EpisodeContext?.ShowWorkId)
            .Select(work => states.GetValueOrDefault(work.WorkId))
            .Where(state => state is { ProgressPct: > 0 })
            .Select(state => (DateTimeOffset?)state!.LastAccessed).Max();

        // Hero order: everything the person is in the middle of comes first, most recently touched first,
        // then newly added items, newest first. A show is one slide; one never started shows as the series.
        var slides = new List<HeroSlideCandidate>();
        foreach (var hero in showHeroes)
        {
            var touched = LastStarted(hero);
            var inTheMiddle = touched.HasValue && hero.ContinuationState != DisplayContinuationState.Completed
                && hero.Actions[0].Label != "Restart Episode";
            var showCard = shows.First(card => card.Id == hero.EpisodeContext!.ShowWorkId);
            slides.Add(new(hero with { IsContinued = inTheMiddle }, inTheMiddle, inTheMiddle ? touched!.Value : showCard.SortTimestamp));
        }
        var continuedIds = continueCards
            .Where(card => card.MediaType != "TV")
            .Select(card => card.Id).ToHashSet();
        foreach (var card in continueCards.Where(card => card.MediaType != "TV" && IsHeroReady(card)))
        {
            slides.Add(new(DisplayCardBuilder.ToHero(card, "Continue Across Media") with { IsContinued = true }, true, card.SortTimestamp));
        }
        foreach (var card in candidates.Where(card => card.MediaType != "TV" && !continuedIds.Contains(card.Id)))
        {
            slides.Add(new(DisplayCardBuilder.ToHero(card, "Featured Content") with { IsContinued = false }, false, card.SortTimestamp));
        }

        // Two steps (D6): the person's settings decide what is allowed, then the orderer ranks only those.
        return _heroOrderer.Order(HomeHeroAllowedItems.Filter(slides, settings))
            .Select(slide => slide.Hero)
            .DistinctBy(hero => hero.EpisodeContext is { } episode ? $"show:{episode.ShowWorkId:D}" :
                hero.Subject == DisplaySubjectKind.Album ? $"album:{hero.Id}" : $"work:{hero.WorkId ?? hero.Id}")
            .Take(Math.Min(MaxHomeSpotlights, settings.SlideCount))
            .ToList();
    }

    private const int MaxHomeSpotlights = 5;

    private static bool HasStoredArtwork(DisplayWorkRow work) =>
        new[]
        {
            work.CoverUrl, work.SquareUrl, work.BackgroundUrl, work.BannerUrl, work.EpisodeStillUrl,
            work.RootCoverUrl, work.RootSquareUrl, work.RootBackgroundUrl, work.RootBannerUrl,
            work.CollectionCoverUrl, work.CollectionSquareUrl, work.CollectionBackgroundUrl, work.CollectionBannerUrl,
        }.Any(url => !string.IsNullOrWhiteSpace(url));

    private static int? Number(string? number) => int.TryParse(number, out var value) ? value : null;
    private static string? EpisodeNumber(DisplayEpisodeContextDto context) => context.SeasonNumber.HasValue && context.EpisodeNumber.HasValue
        ? $"S{context.SeasonNumber} E{context.EpisodeNumber}" : null;
}
