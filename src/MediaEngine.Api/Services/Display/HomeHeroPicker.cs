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
/// The default hero rules: everything the person is in the middle of first, most recently touched first, then newly added items,
/// newest first. A plugin's suggestion only lifts an item inside the "new" group (strongest first); it can never move anything ahead of
/// something the person is in the middle of. This is also what runs when a smarter orderer is off or fails.
/// </summary>
public sealed class DefaultHeroOrderer : IHeroOrderer
{
    public IReadOnlyList<HeroSlideCandidate> Order(IReadOnlyList<HeroSlideCandidate> allowed) =>
        allowed.OrderByDescending(slide => slide.Continued)
            .ThenByDescending(slide => slide.Continued ? 0 : slide.Suggested)
            .ThenByDescending(slide => slide.At)
            .ToList();
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
