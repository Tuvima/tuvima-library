using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Puts a video in a series or one of its sections, starts a new series for it, or makes it stand alone again (kind <c>video_series</c>,
/// universal editor v3.1). Library's own data: the series is how Library shows videos together, never a folder, so nothing on disk moves.
/// A video already in a series that joins another place is taken out and put in as one step; if the second half fails the first is put back.
/// See <see cref="EditSessionVideo"/> for how a change names where the video goes.
/// </summary>
public sealed class VideoSeriesEditSessionChangeHandler(
    IVideoSeriesRepository series,
    ISystemActivityRepository activityRepo,
    IItemCanonicalRepository items) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.VideoSeries;

    // The editor may hold a video by its file's id (the page's own id for it); the series store speaks in works.
    private async Task<Guid> WorkOfAsync(EditSessionChange change, CancellationToken ct) =>
        await items.ResolveWorkIdForAssetAsync(change.EntityId, ct).ConfigureAwait(false) ?? change.EntityId;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
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

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        var workId = await WorkOfAsync(change, ct).ConfigureAwait(false);
        var before = await series.GetMembershipAsync(workId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(EditSessionReasons.NotAVideo);

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
