using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Playback;

namespace MediaEngine.Api.Services.Activity;

/// <summary>
/// Keeps each person's own activity record up to date from playback and from My list. Writing is best effort: a
/// failure is logged and never reaches playback or the My list action that caused it.
/// </summary>
public sealed class PersonalActivityRecorder(
    IPersonalActivityRepository repository,
    IVaultLibraryRepository vaultLibraries,
    PersonalActivityArtworkService artwork,
    TimeProvider clock,
    ILogger<PersonalActivityRecorder> logger)
{
    /// <summary>How far through an item counts as finished it, in percent.</summary>
    internal const double FinishedPercent = 99.5d;

    /// <summary>
    /// Follows the playback sessions that just changed. A newly created session starts its row (with a snapshot of
    /// <paramref name="asset"/>, the item being played); every other change moves its row forward.
    /// </summary>
    public async Task RecordPlaybackAsync(
        IReadOnlyList<PlaybackTelemetryTransition> transitions,
        DisplayWorkRow? asset,
        PersonalActivityDevice? device,
        CancellationToken ct)
    {
        foreach (var transition in transitions)
        {
            if (transition.WasIgnored)
            {
                continue;
            }

            var session = transition.Session;
            try
            {
                var progress = ProgressOf(session);
                if (transition.WasCreated)
                {
                    if (asset is not null && asset.AssetId == session.AssetId)
                    {
                        await StartAsync(session, asset, device, progress, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                await repository.UpdateProgressAsync(session.Id, progress, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not update the personal activity record for playback session {SessionId}.", session.Id);
            }
        }
    }

    /// <summary>Moves the rows of sessions the Engine closed (for example stale ones) to their final state.</summary>
    public Task RecordClosedAsync(IReadOnlyList<PlaybackTelemetryTransition> transitions, CancellationToken ct) =>
        RecordPlaybackAsync(transitions, null, null, ct);

    /// <summary>Records that a title was added to My list, with a snapshot taken from the works the caller can see.</summary>
    public async Task RecordMyListAddedAsync(
        Guid profileId,
        ProfileEntityKind entityKind,
        Guid entityId,
        Func<CancellationToken, Task<IReadOnlyList<DisplayWorkRow>>> loadWorks,
        CancellationToken ct)
    {
        try
        {
            var works = await loadWorks(ct).ConfigureAwait(false);
            var snapshot = PersonalActivitySnapshotFactory.FromMyList(entityKind, entityId, works);
            if (snapshot is not null
                && works.FirstOrDefault(row => row.AssetId == snapshot.AssetId) is { } source
                && await IsVaultAsync(source, ct).ConfigureAwait(false))
            {
                return;
            }

            if (snapshot is null)
            {
                logger.LogWarning("Could not find {Kind} {EntityId} to record it as added to My list.", entityKind, entityId);
                return;
            }

            var key = snapshot.AssetId is { } assetId ? await artwork.CaptureAsync(assetId, ct).ConfigureAwait(false) : null;
            await repository.AddMyListAsync(profileId, clock.GetUtcNow(), snapshot with { ArtworkKey = key }, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not record {Kind} {EntityId} as added to My list.", entityKind, entityId);
        }
    }

    private async Task<bool> IsVaultAsync(DisplayWorkRow asset, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(asset.LibraryId)
        && (await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false)).Contains(asset.LibraryId);

    internal static PersonalActivityProgress ProgressOf(PlaybackTelemetrySession session)
    {
        double? percent = session.DurationSeconds is > 0
            ? Math.Clamp(session.LastPositionSeconds / session.DurationSeconds.Value * 100d, 0d, 100d)
            : null;
        var finished = session.State == PlaybackTelemetryStates.Completed ||
                       string.Equals(session.CompletionReason, PlaybackCompletionReasons.Completed, StringComparison.Ordinal) ||
                       percent >= FinishedPercent;
        return new PersonalActivityProgress(
            session.EndedAt ?? session.LastObservedAt,
            (long)Math.Floor(Math.Max(0d, session.PlayedDurationSeconds)),
            percent,
            finished);
    }

    private async Task StartAsync(
        PlaybackTelemetrySession session,
        DisplayWorkRow asset,
        PersonalActivityDevice? device,
        PersonalActivityProgress progress,
        CancellationToken ct)
    {
        // Photos and personal videos (View) never reach playback telemetry; this keeps them out even if one did.
        var kind = session.Feature == AccountFeatureId.Watch ? PersonalActivityKinds.Watch
            : session.Feature == AccountFeatureId.Listen ? PersonalActivityKinds.Listen
            : session.Feature == AccountFeatureId.Read ? PersonalActivityKinds.Read
            : null;
        if (kind is null)
        {
            return;
        }

        // Nothing about the Vault is ever written to anyone's activity history (Videos spec D17): no title, no cover, no row.
        if (await IsVaultAsync(asset, ct).ConfigureAwait(false))
        {
            return;
        }

        var snapshot = PersonalActivitySnapshotFactory.FromPlayback(asset);
        var key = await artwork.CaptureAsync(asset.AssetId, ct).ConfigureAwait(false);
        var isMusic = kind == PersonalActivityKinds.Listen &&
                      DisplayMediaRules.NormalizeDisplayKind(asset.MediaType) == "Music";
        await repository.AddPlaybackAsync(
            session.ProfileId, kind, isMusic, session.Id, session.StartedAt,
            device ?? new PersonalActivityDevice(session.Client.Name, null),
            snapshot with { ArtworkKey = key }, progress, ct).ConfigureAwait(false);
    }
}
