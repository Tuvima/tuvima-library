using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>One slide the Home hero could show, with what the orderers need to rank it. <paramref name="Suggested"/> is how strongly a plugin suggested it (0 to 100), or 0.</summary>
public sealed record HeroSlideCandidate(DisplayHeroDto Hero, bool Continued, DateTimeOffset At, int Suggested = 0);

/// <summary>
/// The second step of the Home hero (Home page redesign spec D6): puts the allowed slides in order. The default rules are one
/// orderer; another (for example one that uses AI) can replace it. An orderer only reorders what it was given, it never adds items.
/// </summary>
public interface IHeroOrderer
{
    IReadOnlyList<HeroSlideCandidate> Order(IReadOnlyList<HeroSlideCandidate> allowed);
}

/// <summary>
/// The default hero rules (Home, detail and tiles spec D16): the slides take turns across Watch, Read and Listen so no kind is shut
/// out. Inside each kind, what the person is in the middle of comes first (most recently touched first), then newly added items,
/// newest first. A plugin's suggestion only lifts an item inside the "new" group of its kind (strongest first); it can never move
/// anything ahead of something the person is in the middle of. This is also what runs when a smarter orderer is off or fails.
/// </summary>
public sealed class DefaultHeroOrderer : IHeroOrderer
{
    public IReadOnlyList<HeroSlideCandidate> Order(IReadOnlyList<HeroSlideCandidate> allowed)
    {
        var lanes = allowed
            .GroupBy(slide => HomeHeroKinds.Of(slide.Hero))
            .OrderBy(group => group.Key)
            .Select(group => new Queue<HeroSlideCandidate>(group
                .OrderByDescending(slide => slide.Continued)
                .ThenByDescending(slide => slide.Continued ? 0 : slide.Suggested)
                .ThenByDescending(slide => slide.At)))
            .ToList();

        var ordered = new List<HeroSlideCandidate>(allowed.Count);
        while (lanes.Any(lane => lane.Count > 0))
        {
            foreach (var lane in lanes.Where(lane => lane.Count > 0))
            {
                ordered.Add(lane.Dequeue());
            }
        }
        return ordered;
    }
}

/// <summary>Which of the three Home kinds a hero slide belongs to. The numbers are the turn order: Watch, Read, Listen, anything else.</summary>
public static class HomeHeroKinds
{
    public const int Watch = 0;
    public const int Read = 1;
    public const int Listen = 2;
    public const int Other = 3;

    public static int Of(DisplayHeroDto hero)
    {
        if (hero.EpisodeContext is not null || hero.Subject is DisplaySubjectKind.TvShow or DisplaySubjectKind.TvEpisode)
        {
            return Watch;
        }
        if (hero.Subject == DisplaySubjectKind.Album)
        {
            return Listen;
        }

        var mediaType = hero.MediaType ?? string.Empty;
        return DisplayMediaRules.IsWatchKind(mediaType) ? Watch
            : DisplayMediaRules.IsReadKind(mediaType) ? Read
            : DisplayMediaRules.IsListenKind(mediaType) ? Listen
            : Other;
    }
}

/// <summary>
/// The first step of the Home hero: decides which slides may appear at all, from the person's hero settings. It only narrows the
/// list. Matching, real artwork and what the person may open are already settled before a slide becomes a candidate.
/// </summary>
public static class HomeHeroAllowedItems
{
    public static IReadOnlyList<HeroSlideCandidate> Filter(IEnumerable<HeroSlideCandidate> candidates, ProfileHeroSettings settings)
    {
        if (!settings.Enabled)
        {
            return [];
        }

        return candidates
            .Where(slide => !settings.ContinuedOnly || slide.Continued)
            .Where(slide => TypeAllowed(slide.Hero, settings))
            .ToList();
    }

    private static bool TypeAllowed(DisplayHeroDto hero, ProfileHeroSettings settings)
    {
        if (hero.EpisodeContext is not null || hero.Subject is DisplaySubjectKind.TvShow or DisplaySubjectKind.TvEpisode)
        {
            return settings.AllowWatch;
        }
        if (hero.Subject == DisplaySubjectKind.Album)
        {
            return settings.AllowListen;
        }

        var mediaType = hero.MediaType ?? string.Empty;
        if (DisplayMediaRules.IsWatchKind(mediaType))
        {
            return settings.AllowWatch;
        }
        if (DisplayMediaRules.IsReadKind(mediaType))
        {
            return settings.AllowRead;
        }
        if (DisplayMediaRules.IsListenKind(mediaType))
        {
            return settings.AllowListen;
        }

        // A kind the settings have no switch for only appears while every type is allowed.
        return settings is { AllowWatch: true, AllowRead: true, AllowListen: true };
    }
}
