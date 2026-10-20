using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Services;

/// <summary>
/// The one rule for what a profile may be shown of a character without a spoiler. Every surface that names characters
/// (the Universe page's Characters and Cast, "Also appears in", the Universe graph, later the companion) calls this, so they
/// cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// A character's <em>first seen</em> positions are one place per title it appears in: the earliest owned episode when the
/// character is tied to episodes of a show, otherwise the title itself with the position unknown (books and audiobooks never
/// have a position within the title). A character is judged per title and the most permissive reading wins: someone the
/// profile has met in any title it appears in (a film it finished, an episode it has reached) is <see cref="CharacterVisibility.Shown"/>,
/// even if another title first sees them later. The reading for one title:
/// </para>
/// <list type="bullet">
/// <item><description>Level "Everything": always <see cref="CharacterVisibility.Shown"/>, spoiler markers included.</description></item>
/// <item><description>A spoiler marker for a work the profile has not finished (or does not own): <see cref="CharacterVisibility.Hidden"/>.</description></item>
/// <item><description>Seen at an episode the profile has started or passed: <see cref="CharacterVisibility.Shown"/>. Seen only at later episodes of every title it appears in: <see cref="CharacterVisibility.Hidden"/>.</description></item>
/// <item><description>Position unknown within a title the profile has started or finished, a title it has not started, or no known position at all: <see cref="CharacterVisibility.NoPlotDetails"/>.</description></item>
/// </list>
/// </remarks>
public static class SpoilerRule
{
    /// <param name="level">The profile's spoiler level.</param>
    /// <param name="firstSeen">One position per title the character appears in; empty when none is known.</param>
    /// <param name="spoilerFor">The works the character's appearances are marked as a spoiler for; empty when it is not spoiler-marked.</param>
    /// <param name="progress">How far the profile has got.</param>
    public static CharacterVisibility Evaluate(
        SpoilerLevel level,
        IReadOnlyList<WorkPosition> firstSeen,
        IReadOnlyList<WorkPosition> spoilerFor,
        ViewerProgress progress)
    {
        ArgumentNullException.ThrowIfNull(firstSeen);
        ArgumentNullException.ThrowIfNull(spoilerFor);
        ArgumentNullException.ThrowIfNull(progress);

        if (level == SpoilerLevel.Everything)
        {
            return CharacterVisibility.Shown;
        }

        if (spoilerFor.Any(target => !IsFinished(target, progress)))
        {
            return CharacterVisibility.Hidden;
        }

        // The most permissive reading across titles wins: someone who has met the character in a film they finished is not
        // spoiled by a show they have only just begun.
        var result = CharacterVisibility.Hidden;
        foreach (var position in firstSeen)
        {
            var seen = Classify(position, progress);
            if (seen == CharacterVisibility.Shown)
            {
                return CharacterVisibility.Shown;
            }

            if (seen == CharacterVisibility.NoPlotDetails)
            {
                result = CharacterVisibility.NoPlotDetails;
            }
        }

        // No known position at all: the character is named without details rather than guessed about.
        return firstSeen.Count == 0 ? CharacterVisibility.NoPlotDetails : result;
    }

    private static CharacterVisibility Classify(WorkPosition position, ViewerProgress progress)
    {
        var title = progress.For(position.TitleId);
        if (position.TitleId == Guid.Empty || (!title.Started && !title.Finished))
        {
            return CharacterVisibility.NoPlotDetails;
        }

        if (title.Finished)
        {
            return CharacterVisibility.Shown;
        }

        if (position.Episode is not { } episode || title.FurthestStartedEpisode is not { } reached)
        {
            return CharacterVisibility.NoPlotDetails;
        }

        return episode <= reached ? CharacterVisibility.Shown : CharacterVisibility.Hidden;
    }

    private static bool IsFinished(WorkPosition target, ViewerProgress progress)
    {
        if (target.TitleId == Guid.Empty)
        {
            return false;
        }

        var title = progress.For(target.TitleId);
        return title.Finished
            || (target.Episode is { } episode && title.FurthestFinishedEpisode is { } finished && episode <= finished);
    }
}
