using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Stages the file move that goes with a video's series choice (universal editor v6, D24). In a Managed folder the file follows its series, so
/// the choice stages a <see cref="EditSessionChangeKinds.FileMove"/> beside the series change: the editor's footer and Review say what Save
/// changes will do, exactly as they do for a match that moves a file. The move carries <see cref="EditSessionVideo.MovesWithSeriesToken"/>, so
/// the Engine moves the file itself, first, with the series change (and puts it back if that fails). In a Read-only folder nothing is staged
/// and the choice lives in Library only. One place for the single video's chooser and the ticked rows' picker, so the two cannot differ.
/// </summary>
public static class EditorVideoSeriesStaging
{
    /// <summary>
    /// Replaces the video's staged series move with the one <paramref name="placement"/> describes. A file that does not move stages nothing;
    /// one whose destination name is taken is staged all the same, so the Engine's check stops the save and says why.
    /// </summary>
    public static void StageMove(PendingEditList pending, Guid videoId, EditorVideoSeriesPlacementDto? placement)
    {
        ArgumentNullException.ThrowIfNull(pending);
        UnstageMove(pending, videoId);
        if (placement is { Managed: true } && !string.IsNullOrWhiteSpace(placement.FromPath) && !string.IsNullOrWhiteSpace(placement.ToPath))
        {
            pending.SetTarget(
                EditSessionChangeKinds.FileMove,
                videoId,
                null,
                token: EditSessionVideo.MovesWithSeriesToken,
                label: placement.ToPath,
                before: placement.FromPath);
        }
    }

    /// <summary>Takes the video's staged series move back out. Any other staged file move of the video stays.</summary>
    public static void UnstageMove(PendingEditList pending, Guid videoId)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var staged = pending.Changes.FirstOrDefault(change =>
            change.Kind == EditSessionChangeKinds.FileMove
            && change.EntityId == videoId
            && string.Equals(change.Token, EditSessionVideo.MovesWithSeriesToken, StringComparison.Ordinal));
        if (staged is not null)
        {
            pending.Remove(staged.Id);
        }
    }

    /// <summary>Takes the video's staged series change and the move that goes with it back out.</summary>
    public static void Unstage(PendingEditList pending, Guid videoId)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.FindVideoSeries(videoId) is { } series)
        {
            pending.Remove(series.Id);
        }

        UnstageMove(pending, videoId);
    }
}
