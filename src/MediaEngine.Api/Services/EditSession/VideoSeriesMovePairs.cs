using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Which video series changes of a save may move their video's file (universal editor v6, D24). A file moves only when the save holds the
/// file move the person was shown for that very series change: the move names the series change by id and carries the series choice it was
/// shown with, and both must still agree. A move that no longer matches (the choice was replaced, the series change is gone) is refused with
/// a plain line, so a file never moves somewhere neither the footer nor Review showed, and a "will move" line never goes unkept. A video's
/// file also never moves for its match and for its series in one save, because the two would disagree about where it ends up.
/// </summary>
public sealed class VideoSeriesMovePairs
{
    private VideoSeriesMovePairs(HashSet<string> followingSeriesChanges, Dictionary<string, string> refusals)
    {
        FollowingSeriesChanges = followingSeriesChanges;
        Refusals = refusals;
    }

    /// <summary>The ids of the series changes whose file moves with them.</summary>
    public IReadOnlySet<string> FollowingSeriesChanges { get; }

    /// <summary>The file-move changes that cannot be saved as they are, by change id, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Refusals { get; }

    public static VideoSeriesMovePairs Read(IReadOnlyList<EditSessionChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var following = new HashSet<string>(StringComparer.Ordinal);
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var move in changes.Where(change => change.Kind == EditSessionChangeKinds.FileMove && EditSessionVideo.IsMovesWithSeries(change.Token)))
        {
            var series = changes.FirstOrDefault(change =>
                change.Kind == EditSessionChangeKinds.VideoSeries
                && change.EntityId == move.EntityId
                && string.Equals(change.Id, move.TargetId, StringComparison.Ordinal));
            if (series is null
                || !EditSessionVideo.TryReadMovesWithSeries(move.Token, out var shownWith)
                || !string.Equals(EditSessionVideo.ChoiceKey(series.TargetId, series.Token), shownWith, StringComparison.Ordinal))
            {
                refusals[move.Id] = EditSessionReasons.SeriesMoveStale;
                continue;
            }

            if (changes.Any(change => change.Kind == EditSessionChangeKinds.FileMove
                                      && change.EntityId == move.EntityId
                                      && !EditSessionVideo.IsMovesWithSeries(change.Token)))
            {
                refusals[move.Id] = EditSessionReasons.SeriesMoveWithMatchMove;
                continue;
            }

            following.Add(series.Id);
        }

        return new VideoSeriesMovePairs(following, refusals);
    }
}
