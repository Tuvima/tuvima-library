using System.Globalization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Display;

public sealed partial class DisplayComposerService
{
    private IReadOnlyList<DisplayHeroDto> BuildHomeSpotlights(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> states,
        IReadOnlyList<DisplayCardDto> shows,
        IReadOnlyList<DisplayCardDto> candidates)
    {
        var showHeroes = new List<DisplayHeroDto>();
        foreach (var show in shows)
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
                    _ => "Watch Episode",
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

        var rankedShows = showHeroes.OrderByDescending(LastStarted).ToList();
        var ranked = rankedShows.Where(hero => LastStarted(hero).HasValue)
            .Concat(candidates.Where(card => card.MediaType != "TV").Select(card => DisplayCardBuilder.ToHero(card,
                card.Progress is null ? "Featured Content" : "Continue Across Media")))
            .Concat(rankedShows)
            .DistinctBy(hero => hero.EpisodeContext is { } episode ? $"show:{episode.ShowWorkId:D}" :
                hero.Subject == DisplaySubjectKind.Album ? $"album:{hero.Id}" : $"work:{hero.WorkId ?? hero.Id}")
            .ToList();
        static string Bucket(DisplayHeroDto hero) => hero.Subject == DisplaySubjectKind.TvShow ? "TV"
            : hero.Subject == DisplaySubjectKind.Album ? "Album"
            : DisplayMediaRules.NormalizeDisplayKind(hero.MediaType) switch
            {
                "Book" or "Comic" => "Read",
                var kind => kind,
            };
        var selected = new List<DisplayHeroDto>();
        foreach (var bucket in new[] { "TV", "Movie", "Read", "Album", "Audiobook" })
        {
            var representative = ranked.FirstOrDefault(hero => Bucket(hero) == bucket);
            if (representative is not null)
            {
                selected.Add(representative);
            }
        }
        selected.AddRange(ranked.Where(hero => !selected.Contains(hero)).Take(5 - selected.Count));
        return selected;
    }

    private static int? Number(string? number) => int.TryParse(number, out var value) ? value : null;
    private static string? EpisodeNumber(DisplayEpisodeContextDto context) => context.SeasonNumber.HasValue && context.EpisodeNumber.HasValue
        ? $"S{context.SeasonNumber} E{context.EpisodeNumber}" : null;
}
