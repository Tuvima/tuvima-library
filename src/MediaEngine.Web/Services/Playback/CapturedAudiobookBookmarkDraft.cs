using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Playback;

/// <summary>The immutable position and source facts captured when the Add dialog opens.</summary>
public sealed record CapturedAudiobookBookmarkDraft(
    long Generation,
    Guid ProfileId,
    Guid WorkId,
    Guid SessionLeaseId,
    Guid AssetId,
    int? ChapterIndex,
    string? ChapterTitle,
    double PositionSeconds,
    double? DurationSeconds,
    DateTimeOffset CapturedAt);

/// <summary>A validated save attempt containing the frozen capture and normalized note.</summary>
public sealed record AudiobookBookmarkSaveAttempt(
    long Generation,
    CapturedAudiobookBookmarkDraft Draft,
    string? Note);

/// <summary>
/// Owns one transient draft lease. Same-book asset progression is intentionally not
/// part of lease validity; authorization of the captured asset is checked at save time.
/// </summary>
public sealed class CapturedAudiobookBookmarkDraftLease
{
    private long _generation;
    private bool _savePending;

    public CapturedAudiobookBookmarkDraft? Current { get; private set; }

    public bool TryCapture(
        Guid profileId,
        Guid workId,
        Guid sessionLeaseId,
        Guid assetId,
        double positionSeconds,
        double? durationSeconds,
        PlaybackChapterDto? chapter,
        out CapturedAudiobookBookmarkDraft? draft,
        DateTimeOffset? capturedAt = null)
    {
        // Every attempted new capture replaces the prior dialog lease, including
        // malformed or late capture results for a newly selected source.
        _generation++;
        var generation = _generation;
        _savePending = false;
        Current = null;
        draft = null;
        if (profileId == Guid.Empty || workId == Guid.Empty || sessionLeaseId == Guid.Empty || assetId == Guid.Empty
            || !double.IsFinite(positionSeconds) || positionSeconds < 0
            || (durationSeconds.HasValue && (!double.IsFinite(durationSeconds.Value)
                || durationSeconds.Value <= 0 || positionSeconds > durationSeconds.Value)))
        {
            return false;
        }

        int? chapterIndex = null;
        string? chapterTitle = null;
        if (chapter is not null)
        {
            if (chapter.Index < 0 || chapter.AssetId != assetId
                || !double.IsFinite(chapter.StartSeconds) || chapter.StartSeconds < 0
                || chapter.EndSeconds is not double chapterEnd || !double.IsFinite(chapterEnd)
                || chapterEnd <= chapter.StartSeconds || chapter.StartSeconds > positionSeconds
                || positionSeconds >= chapterEnd)
            {
                return false;
            }

            chapterIndex = chapter.Index;
            chapterTitle = string.IsNullOrWhiteSpace(chapter.Title) ? null : chapter.Title;
        }

        draft = new CapturedAudiobookBookmarkDraft(
            generation,
            profileId,
            workId,
            sessionLeaseId,
            assetId,
            chapterIndex,
            chapterTitle,
            positionSeconds,
            durationSeconds,
            capturedAt ?? DateTimeOffset.UtcNow);
        Current = draft;
        return true;
    }

    public bool TryBeginSave(
        long generation,
        Guid profileId,
        Guid workId,
        Guid sessionLeaseId,
        IReadOnlySet<Guid> authorizedAssetIds,
        string? note,
        out AudiobookBookmarkSaveAttempt? attempt)
    {
        attempt = null;
        var draft = Current;
        if (_savePending || draft is null || generation != _generation || draft.Generation != generation
            || draft.ProfileId != profileId || draft.WorkId != workId || draft.SessionLeaseId != sessionLeaseId
            || !authorizedAssetIds.Contains(draft.AssetId)
            || !AudiobookBookmarkNotePolicy.TryNormalize(note, out var normalizedNote))
        {
            return false;
        }

        _savePending = true;
        attempt = new AudiobookBookmarkSaveAttempt(generation, draft, normalizedNote);
        return true;
    }

    /// <summary>Completes only the matching save; stale responses cannot clear a newer draft's busy state.</summary>
    public bool CompleteSave(long generation)
    {
        if (!_savePending || generation != _generation)
        {
            return false;
        }

        _savePending = false;
        return true;
    }

    /// <summary>Invalidates the draft on profile/book/session replacement or revoked asset access.</summary>
    public void Invalidate()
    {
        _generation++;
        _savePending = false;
        Current = null;
    }
}
