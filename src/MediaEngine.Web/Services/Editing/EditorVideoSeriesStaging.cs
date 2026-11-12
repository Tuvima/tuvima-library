using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>What staging a video's series choice did.</summary>
public enum VideoSeriesStageOutcome
{
    /// <summary>The series change is staged, with the file move beside it when the file will move.</summary>
    Staged,

    /// <summary>The Engine could not say where the file would go, so nothing was staged: in a Managed folder a series change without its move would break the rule that the file follows its series.</summary>
    PreviewFailed,

    /// <summary>The Engine refused the choice outright (a name that cannot be a folder name), so nothing was staged.</summary>
    Blocked,

    /// <summary>The video already has a file move staged for its match; the two cannot be saved together, so nothing was staged.</summary>
    MatchMoveInTheWay,
}

/// <summary>
/// Stages a video's series choice together with the file move that goes with it (universal editor v6, D24). In a Managed folder the file
/// follows its series, so the choice stages a <see cref="EditSessionChangeKinds.FileMove"/> that names the series change it belongs to:
/// the editor's footer and Review say what Save changes will do, and the Engine moves the file itself, first, only for that very choice
/// (and puts it back if anything fails). In a Read-only folder nothing is staged beside the series change and the choice lives in Library
/// only. A choice whose file move could not be worked out stages nothing at all, so a series change never goes in without the move the
/// person should have been shown. One place for the single video's chooser and the ticked rows' picker, so the two cannot differ.
/// </summary>
public static class EditorVideoSeriesStaging
{
    /// <summary>
    /// Replaces the video's staged series choice with <paramref name="targetId"/> / <paramref name="token"/>, as <paramref name="placement"/>
    /// (the Engine's answer for that choice) describes. A name already taken at the destination is staged all the same, so the Engine's
    /// check stops the save and says why.
    /// </summary>
    public static VideoSeriesStageOutcome Stage(
        PendingEditList pending, Guid videoId, string? targetId, string? token, string? label, string? before, EditorVideoSeriesPlacementDto? placement)
    {
        ArgumentNullException.ThrowIfNull(pending);
        Unstage(pending, videoId);
        if (placement is null)
        {
            return VideoSeriesStageOutcome.PreviewFailed;
        }

        var moves = MovesFile(placement);
        if (moves && pending.FindFileMove(videoId) is not null)
        {
            return VideoSeriesStageOutcome.MatchMoveInTheWay;
        }

        if (placement.Blocked && !moves)
        {
            return VideoSeriesStageOutcome.Blocked;
        }

        pending.SetTarget(EditSessionChangeKinds.VideoSeries, videoId, targetId, token, label, null, before);
        if (moves)
        {
            pending.SetVideoSeriesMove(videoId, placement.FromPath!, placement.ToPath!);
        }

        return VideoSeriesStageOutcome.Staged;
    }

    /// <summary>True when the placement says the file will move: a Managed folder and both paths known.</summary>
    public static bool MovesFile(EditorVideoSeriesPlacementDto? placement) =>
        placement is { Managed: true } && !string.IsNullOrWhiteSpace(placement.FromPath) && !string.IsNullOrWhiteSpace(placement.ToPath);

    /// <summary>Takes the video's staged series change and the move that goes with it back out (the pair always goes together).</summary>
    public static void Unstage(PendingEditList pending, Guid videoId)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.FindVideoSeries(videoId) is { } series)
        {
            pending.Remove(series.Id);
        }
        else if (pending.FindVideoSeriesMove(videoId) is { } orphan)
        {
            pending.Remove(orphan.Id);
        }
    }
}
