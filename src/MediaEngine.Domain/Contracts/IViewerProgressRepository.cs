namespace MediaEngine.Domain.Contracts;

/// <summary>One work a profile has started, with the furthest progress on any of its files.</summary>
/// <param name="WorkId">The work (an episode, a film, a book) the profile has started.</param>
/// <param name="ProgressPct">0 to 100; above 0 means started.</param>
public sealed record ViewerWorkProgress(Guid WorkId, double ProgressPct);

/// <summary>
/// Reads how far one profile has got, per work, for the spoiler rule. Read-only: progress is written by playback and the
/// personal status actions, never here.
/// </summary>
public interface IViewerProgressRepository
{
    /// <summary>Every work the profile has started (progress above 0) with its furthest progress.</summary>
    Task<IReadOnlyList<ViewerWorkProgress>> GetStartedWorksAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// The Wikidata QID of each of the given works that has one (its own, else the one stored with its values). Works with no
    /// QID are left out. Used to read a show's own identity when its episodes carry identities of their own.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetWorkQidsAsync(IEnumerable<Guid> workIds, CancellationToken ct = default);

    /// <summary>
    /// Of the given Wikidata QIDs, the ones that are the own identity of an episode-level work (a child work, never a show
    /// or film). A show whose episodes carry no QID of their own is not in this set, so a link to it is never read as an episode.
    /// </summary>
    Task<IReadOnlySet<string>> GetChildWorkQidsAsync(IEnumerable<string> workQids, CancellationToken ct = default);
}
