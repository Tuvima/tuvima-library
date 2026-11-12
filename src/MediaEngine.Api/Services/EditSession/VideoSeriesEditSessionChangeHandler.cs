using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Puts a video in a series or one of its sections, starts a new series for it, or makes it stand alone again (kind <c>video_series</c>,
/// universal editor v3.1). The series is how Library shows videos together. In a Managed folder the file follows its series when the save
/// also holds the file move the person was shown (editor v6, D24; a series change without one, such as a drag between sections in the order
/// list, never moves a file): it moves first, into the series' folder, and the series rows change only after that worked; if they then fail the file is moved
/// back, so Library and disk always agree. In a Read-only folder nothing on disk changes.
/// A video already in a series that joins another place is taken out and put in as one step; if the second half fails the first is put back.
/// See <see cref="EditSessionVideo"/> for how a change names where the video goes and <see cref="IVideoSeriesPlacement"/> for the move.
/// </summary>
public sealed class VideoSeriesEditSessionChangeHandler(
    IVideoSeriesRepository series,
    ISystemActivityRepository activityRepo,
    IItemCanonicalRepository items,
    IVideoSeriesPlacement? placement = null,
    IAutoOrganizeService? organizer = null) : IFileFollowingEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.VideoSeries;

    // The editor may hold a video by its file's id (the page's own id for it); the series store speaks in works.
    private async Task<Guid> WorkOfAsync(EditSessionChange change, CancellationToken ct) =>
        await items.ResolveWorkIdForAssetAsync(change.EntityId, ct).ConfigureAwait(false) ?? change.EntityId;

    public Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default) => CheckAsync(change, moveFile: false, ct);

    public async Task<string?> CheckAsync(EditSessionChange change, bool moveFile, CancellationToken ct = default)
    {
        var problem = await CheckSeriesAsync(change, ct).ConfigureAwait(false);
        if (problem is not null || placement is null || !moveFile)
        {
            return problem;
        }

        // The file is planned again now, whatever the Dashboard's preview said; a name taken at the destination stops the save.
        var plan = await placement.PlanAsync(change.EntityId, change.TargetId, change.Token, ct).ConfigureAwait(false);
        return plan is { Blocks: true } ? plan.Reason : null;
    }

    private async Task<string?> CheckSeriesAsync(EditSessionChange change, CancellationToken ct)
    {
        var workId = await WorkOfAsync(change, ct).ConfigureAwait(false);
        var membership = await series.GetMembershipAsync(workId, ct).ConfigureAwait(false);
        if (membership is null)
        {
            return EditSessionReasons.NotAVideo;
        }

        if (EditSessionVideo.IsNoSeries(change.Token))
        {
            return membership.ParentWorkId is null ? EditSessionReasons.AlreadyAlone : null;
        }

        if (change.Token is { Length: > 0 } && change.Token.StartsWith("new:", StringComparison.Ordinal))
        {
            if (!EditSessionVideo.TryParseNewSeries(change.Token, out var name))
            {
                return EditSessionReasons.SeriesNeedsName;
            }

            return name.Length > EditSessionVideo.MaxSeriesNameLength ? EditSessionReasons.SeriesNameTooLong : null;
        }

        if (!Guid.TryParse(change.TargetId, out var target))
        {
            return EditSessionReasons.NoDestination;
        }

        if (membership.ParentWorkId == target)
        {
            return null;
        }

        var places = await series.ListTargetsAsync(workId, ct).ConfigureAwait(false);
        return places.Any(place => place.ParentWorkId == target) ? null : EditSessionReasons.SeriesGone;
    }

    public Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default) =>
        ApplyAsync(change, moveFile: false, ct);

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, bool moveFile, CancellationToken ct = default)
    {
        var workId = await WorkOfAsync(change, ct).ConfigureAwait(false);
        var before = await series.GetMembershipAsync(workId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.NotAVideo);

        // The file moves first. If it cannot, nothing has changed and the person is told why; once it has, the series rows follow, and if
        // they fail the file goes back.
        VideoSeriesFilePlan? plan = null;
        string? movedFrom = null;
        if (moveFile && placement is not null && organizer is not null)
        {
            plan = await placement.PlanAsync(change.EntityId, change.TargetId, change.Token, ct).ConfigureAwait(false);
            if (plan is { TargetFolder: not null, ToPath: not null })
            {
                if (plan.Blocks)
                {
                    throw new InvalidOperationException(plan.Reason);
                }

                var outcome = await organizer.MoveToFolderAsync(plan.AssetId, plan.TargetFolder, ct).ConfigureAwait(false);
                if (outcome.Kind != AssetRefileOutcomeKind.Moved)
                {
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(plan.FromPath)} could not be moved to its series' folder: {outcome.Reason ?? "it could not be moved"}");
                }

                movedFrom = Path.GetDirectoryName(plan.FromPath);
            }
        }

        Func<CancellationToken, Task>? seriesUndo;
        try
        {
            seriesUndo = await ApplySeriesAsync(change, workId, before, ct).ConfigureAwait(false);
        }
        catch when (movedFrom is not null)
        {
            await PutFileBackAsync(plan!.AssetId, movedFrom).ConfigureAwait(false);
            throw;
        }

        if (movedFrom is null)
        {
            return seriesUndo;
        }

        var assetId = plan!.AssetId;
        return async undoCt =>
        {
            if (seriesUndo is not null)
            {
                await seriesUndo(undoCt).ConfigureAwait(false);
            }

            await PutFileBackAsync(assetId, movedFrom).ConfigureAwait(false);
        };
    }

    private async Task PutFileBackAsync(Guid assetId, string folder)
    {
        var outcome = await organizer!.MoveToFolderAsync(assetId, folder, CancellationToken.None).ConfigureAwait(false);
        if (outcome.Kind is not (AssetRefileOutcomeKind.Moved or AssetRefileOutcomeKind.AlreadyFits))
        {
            throw new InvalidOperationException(
                $"The video was not changed, but its file could not be moved back to {folder} and is now in {Path.GetDirectoryName(outcome.Path)}.");
        }
    }

    private async Task<Func<CancellationToken, Task>?> ApplySeriesAsync(
        EditSessionChange change, Guid workId, VideoSeriesMembership before, CancellationToken ct)
    {
        if (EditSessionVideo.IsNoSeries(change.Token))
        {
            Require(await series.RemoveAsync(workId, ct).ConfigureAwait(false), EditSessionReasons.AlreadyAlone);
            await LogAsync(workId, "Took a video out of its series.", ct).ConfigureAwait(false);
            return before.ParentWorkId is { } old ? undoCt => PutBackAsync(workId, old, undoCt) : null;
        }

        Guid? target = Guid.TryParse(change.TargetId, out var parsed) ? parsed : null;
        if (target is not null && before.ParentWorkId == target)
        {
            return null;
        }

        var leftOld = false;
        if (before.ParentWorkId is not null)
        {
            Require(await series.RemoveAsync(workId, ct).ConfigureAwait(false), EditSessionReasons.SeriesClash);
            leftOld = true;
        }

        VideoSeriesChange joined;
        if (target is { } place)
        {
            joined = await series.PromoteAsync(workId, place, ct).ConfigureAwait(false);
        }
        else if (EditSessionVideo.TryParseNewSeries(change.Token, out var name))
        {
            joined = await series.PromoteToNewSeriesAsync(workId, name, ct).ConfigureAwait(false);
        }
        else
        {
            joined = new VideoSeriesChange(VideoSeriesOutcome.TargetUnavailable);
        }

        if (joined.Outcome != VideoSeriesOutcome.Done)
        {
            if (leftOld && before.ParentWorkId is { } back)
            {
                await series.PromoteAsync(workId, back, ct).ConfigureAwait(false);
            }

            Require(joined, EditSessionReasons.SeriesGone);
        }

        await LogAsync(workId, $"Put a video in the series {joined.SeriesTitle}.", ct).ConfigureAwait(false);
        return async undoCt =>
        {
            await series.RemoveAsync(workId, undoCt).ConfigureAwait(false);
            if (before.ParentWorkId is { } old)
            {
                await series.PromoteAsync(workId, old, undoCt).ConfigureAwait(false);
            }
        };
    }

    private async Task PutBackAsync(Guid videoId, Guid parentId, CancellationToken ct) =>
        await series.PromoteAsync(videoId, parentId, ct).ConfigureAwait(false);

    private static void Require(VideoSeriesChange change, string notDone)
    {
        if (change.Outcome == VideoSeriesOutcome.Done)
        {
            return;
        }

        throw new InvalidOperationException(change.Outcome switch
        {
            VideoSeriesOutcome.NotAVideo => EditSessionReasons.NotAVideo,
            VideoSeriesOutcome.NameRequired => EditSessionReasons.SeriesNeedsName,
            VideoSeriesOutcome.Conflict => EditSessionReasons.SeriesClash,
            _ => notDone,
        });
    }

    private Task LogAsync(Guid workId, string detail, CancellationToken ct) =>
        activityRepo.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = workId,
            EntityType = "Work",
            Detail = detail,
        }, ct);
}
