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
        string? movedTo = null;
        IReadOnlyList<string> travelled = [];
        if (moveFile && placement is not null && organizer is not null)
        {
            plan = await placement.PlanAsync(change.EntityId, change.TargetId, change.Token, ct).ConfigureAwait(false);
            if (plan is { Blocks: true })
            {
                throw new InvalidOperationException(plan.Reason);
            }

            if (plan is { TargetFolder: not null, ToPath: not null })
            {
                // The folder-wide files (cover, metadata.json) go with the last video out of a folder, exactly as in a match's move; what
                // went along is worked out from the two folders' contents so an undo can put every one of them back.
                var namesBefore = NamesIn(Path.GetDirectoryName(plan.FromPath));
                var namesAtTarget = NamesIn(plan.TargetFolder);
                var outcome = await organizer.MoveToFolderAsync(plan.AssetId, plan.TargetFolder, ct).ConfigureAwait(false);
                if (outcome.Kind != AssetRefileOutcomeKind.Moved)
                {
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(plan.FromPath)} could not be moved to its series' folder: {outcome.Reason ?? "it could not be moved"}");
                }

                movedFrom = Path.GetDirectoryName(plan.FromPath);
                movedTo = Path.GetDirectoryName(outcome.Path);
                travelled = namesBefore
                    .Where(name => !namesAtTarget.Contains(name)
                        && !File.Exists(Path.Combine(movedFrom!, name)) && File.Exists(Path.Combine(movedTo!, name)))
                    .ToList();
            }
        }

        Func<CancellationToken, Task>? seriesUndo;
        try
        {
            seriesUndo = await ApplySeriesAsync(change, workId, before, ct).ConfigureAwait(false);
        }
        catch when (movedFrom is not null)
        {
            await PutFileBackAsync(
                plan!.AssetId, movedFrom,
                where => $"The video was not changed, but its file could not be moved back to {movedFrom} and is now in {where}.").ConfigureAwait(false);
            ThrowIfStuck(PutCompanionsBack(movedTo!, movedFrom, travelled), movedFrom);
            throw;
        }

        if (movedFrom is null || movedTo is null)
        {
            return seriesUndo;
        }

        var assetId = plan!.AssetId;
        var forwardFolder = movedTo;
        var companions = travelled;
        return async undoCt =>
        {
            // The file goes back first. If it cannot, nothing else is touched: the series change stays saved and the file is where that
            // change put it, so Library and disk still agree, and the person is told which file is where.
            await PutFileBackAsync(
                assetId, movedFrom,
                where => $"The series change stays saved, because the video's file could not be moved back to {movedFrom}. It is in {where}.").ConfigureAwait(false);
            var stuck = PutCompanionsBack(forwardFolder, movedFrom, companions);
            if (seriesUndo is not null)
            {
                try
                {
                    await seriesUndo(undoCt).ConfigureAwait(false);
                }
                catch
                {
                    // The series rows could not be put back, so the file goes forward again to stay with them; the failure itself still
                    // reaches the person (the exception is rethrown). A move that fails here is reported by the next check of the folder.
                    await TryMoveAsync(assetId, forwardFolder).ConfigureAwait(false);
                    throw;
                }
            }

            ThrowIfStuck(stuck, movedFrom);
        };
    }

    private static HashSet<string> NamesIn(string? folder)
    {
        try
        {
            return folder is not null && Directory.Exists(folder)
                ? Directory.GetFiles(folder).Select(file => Path.GetFileName(file)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed has nothing to compare; the move itself reports what it cannot do.
            _ = ex;
            return [];
        }
    }

    // The folder-wide files that went along with the video go back to the folder they came from. A name already taken there is never
    // overwritten; whatever could not go back is returned so the person is told.
    private static List<string> PutCompanionsBack(string fromFolder, string toFolder, IReadOnlyList<string> names)
    {
        var stuck = new List<string>();
        foreach (var name in names)
        {
            var from = Path.Combine(fromFolder, name);
            var to = Path.Combine(toFolder, name);
            if (!File.Exists(from))
            {
                continue;
            }

            try
            {
                if (File.Exists(to))
                {
                    stuck.Add(name);
                    continue;
                }

                File.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
                stuck.Add(name);
            }
        }

        return stuck;
    }

    private static void ThrowIfStuck(IReadOnlyList<string> stuck, string folder)
    {
        if (stuck.Count > 0)
        {
            throw new InvalidOperationException(
                $"The video is back in {folder}, but {string.Join(", ", stuck)} could not be moved back with it and stays in the series folder.");
        }
    }

    private async Task PutFileBackAsync(Guid assetId, string folder, Func<string, string> failure)
    {
        var outcome = await organizer!.MoveToFolderAsync(assetId, folder, CancellationToken.None).ConfigureAwait(false);
        if (outcome.Kind is not (AssetRefileOutcomeKind.Moved or AssetRefileOutcomeKind.AlreadyFits))
        {
            throw new InvalidOperationException(failure(Path.GetDirectoryName(outcome.Path) ?? "its new folder"));
        }
    }

    private async Task TryMoveAsync(Guid assetId, string folder)
    {
        try
        {
            await organizer!.MoveToFolderAsync(assetId, folder, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Best effort, on a path that is already reporting a failure to the person: the original exception is the one they are told.
            _ = ex;
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
