using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// The one helper every read that names characters calls: the Universe page's Characters and Cast, the compact Universe
/// read, "Also appears in" and the Universe graph. It loads the requesting profile's spoiler level and progress once, then
/// answers per character through <see cref="SpoilerRule"/>, so no surface keeps a rule of its own. A request with no
/// profile is judged as a viewer who has started nothing at the default level.
/// </summary>
public sealed class CharacterSpoilerService(
    CharacterFirstSeenReader firstSeen,
    IViewerProgressRepository progress,
    IUserPlaybackSettingsService settings,
    IFictionalEntityRepository entities)
{
    /// <summary>The owned story the requesting profile may see (see <see cref="OwnedStory"/>).</summary>
    public Task<OwnedStory> LoadStoryAsync(CancellationToken ct = default) => firstSeen.LoadAsync(ct);

    /// <summary>
    /// Which of the given Character entities the profile has not reached, judged by their visible appearances. Used by the
    /// lists that name a character without loading a Universe: the credits on cast lists and person pages, and people search.
    /// Empty at the level "Everything".
    /// </summary>
    public async Task<IReadOnlySet<Guid>> HiddenEntityIdsAsync(
        Profile? profile,
        IEnumerable<Guid> entityIds,
        CancellationToken ct = default)
    {
        var ids = entityIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var view = await LoadAsync(profile, ct).ConfigureAwait(false);
        if (view.Level == SpoilerLevel.Everything)
        {
            return new HashSet<Guid>();
        }

        var visibleLinks = (await entities.GetWorkLinksAsync(ids, ct).ConfigureAwait(false))
            .Where(link => view.Story.VisibleQids.Contains(link.WorkQid))
            .ToLookup(link => link.FictionalEntityId);
        return ids
            .Where(id => visibleLinks.Contains(id) && view.Evaluate(visibleLinks[id]) == CharacterVisibility.Hidden)
            .ToHashSet();
    }

    public async Task<CharacterSpoilerView> LoadAsync(Profile? profile, CancellationToken ct = default)
    {
        var level = profile is null
            ? SpoilerLevel.UpToProgress
            : await settings.GetSpoilerLevelAsync(profile.Id, ct).ConfigureAwait(false);
        var story = await firstSeen.LoadAsync(ct).ConfigureAwait(false);
        // Progress matters only when the profile wants protection from what it has not reached.
        var viewer = profile is null || level == SpoilerLevel.Everything
            ? ViewerProgress.Empty
            : story.ProgressOf(await progress.GetStartedWorksAsync(profile.Id, ct).ConfigureAwait(false));
        return new CharacterSpoilerView(level, story, viewer);
    }
}

/// <summary>One request's spoiler judgement: the profile's level and progress against the owned story.</summary>
public sealed class CharacterSpoilerView(SpoilerLevel level, OwnedStory story, ViewerProgress progress)
{
    public SpoilerLevel Level => level;

    /// <summary>
    /// The same judgement for a viewer who is standing at <paramref name="position"/> right now, as a companion following a
    /// playing device or holding a place set by hand does. The place counts as reached (never as finished), so the one
    /// spoiler rule still decides everything else.
    /// </summary>
    public CharacterSpoilerView WithReached(WorkPosition position) => new(level, story, progress.Reaching(position));

    /// <summary>The owned story this judgement was made against.</summary>
    public OwnedStory Story => story;

    /// <summary>
    /// What may be shown of a character, from the appearance links the profile may see. A character whose every appearance
    /// is marked as a spoiler is judged by those markers (hidden until the marked work is finished); a character with at
    /// least one plain appearance is known from those, and the marked ones are simply not used.
    /// </summary>
    public CharacterVisibility Evaluate(IEnumerable<FictionalEntityWorkLink> visibleLinks)
    {
        if (level == SpoilerLevel.Everything)
        {
            return CharacterVisibility.Shown;
        }

        var links = visibleLinks.ToList();
        var counted = Counted(links);
        var allMarked = links.Count > 0 && links.All(IsSpoilerMarked);
        var spoilerFor = allMarked
            ? links
                .Select(link => link.SpoilerForWorkQid!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(ResolveSpoilerTarget)
                .ToList()
            : [];
        return SpoilerRule.Evaluate(level, story.FirstSeen(counted), spoilerFor, progress);
    }

    /// <summary>
    /// The appearance links that count towards what a surface says about a character: all of them at "Everything", otherwise
    /// the plain ones, or every one for a character whose appearances are all marked (the rule has already shown it only if
    /// every marker is cleared).
    /// </summary>
    public IReadOnlyList<FictionalEntityWorkLink> Counted(IReadOnlyList<FictionalEntityWorkLink> visibleLinks)
    {
        if (level == SpoilerLevel.Everything)
        {
            return visibleLinks;
        }

        var plain = visibleLinks.Where(link => !IsSpoilerMarked(link)).ToList();
        return plain.Count > 0 ? plain : visibleLinks;
    }

    /// <summary>
    /// The appearance links a surface may state for an entity: for a character, those that count (see <see cref="Counted"/>);
    /// for a place or organisation, which has no episode to place it by, every one at "Everything" and otherwise only the
    /// ones not marked as a spoiler.
    /// </summary>
    public IReadOnlyList<FictionalEntityWorkLink> VisibleAppearances(IReadOnlyList<FictionalEntityWorkLink> visibleLinks, bool isCharacter)
    {
        if (isCharacter)
        {
            return Counted(visibleLinks);
        }

        return level == SpoilerLevel.Everything
            ? visibleLinks
            : visibleLinks.Where(link => !IsSpoilerMarked(link)).ToList();
    }

    /// <summary>
    /// How many Works the links reach: a TV show is one Work however many of its episodes the links name, and a Work is
    /// counted once however many links name it.
    /// </summary>
    public int CountWorks(IEnumerable<FictionalEntityWorkLink> links) =>
        links
            .SelectMany(link => story.Resolve(link.WorkQid) is { Count: > 0 } positions
                ? positions.Select(position => position.TitleId.ToString("N"))
                : [link.WorkQid.Trim().ToUpperInvariant()])
            .Distinct(StringComparer.Ordinal)
            .Count();

    // A marked work the profile does not own can never be finished, so it holds the character back.
    private IEnumerable<WorkPosition> ResolveSpoilerTarget(string workQid)
    {
        var positions = story.Resolve(workQid);
        return positions.Count > 0 ? positions : [WorkPosition.Unowned];
    }

    private static bool IsSpoilerMarked(FictionalEntityWorkLink link) => !string.IsNullOrWhiteSpace(link.SpoilerForWorkQid);
}
