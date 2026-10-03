using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

public enum AudiobookSleepBoundaryKind
{
    EndOfChapter,
    EndOfNextChapter,
}

/// <summary>An immutable, source-verified audiobook sleep target.</summary>
public sealed record AudiobookSleepBoundaryTarget(
    Guid WorkId,
    Guid SessionLeaseId,
    Guid OriginAssetId,
    int OriginChapterIndex,
    double OriginPositionSeconds,
    Guid TargetAssetId,
    int TargetChapterIndex,
    string TargetChapterTitle,
    double TargetEndSeconds,
    AudiobookSleepBoundaryKind Kind);

/// <summary>Captures a chapter stop point once; callers must not resolve it again after seek or skip.</summary>
public static class AudiobookSleepBoundaryResolver
{
    public static bool TryCapture(
        Guid workId,
        Guid sessionLeaseId,
        Guid currentAssetId,
        double localPositionSeconds,
        IReadOnlyList<PlaybackChapterDto> orderedChapters,
        IReadOnlySet<Guid> authorizedAssetIds,
        AudiobookSleepBoundaryKind kind,
        out AudiobookSleepBoundaryTarget? target)
    {
        target = null;
        if (workId == Guid.Empty || sessionLeaseId == Guid.Empty || currentAssetId == Guid.Empty
            || !double.IsFinite(localPositionSeconds) || localPositionSeconds < 0
            || !authorizedAssetIds.Contains(currentAssetId) || orderedChapters.Count == 0)
        {
            return false;
        }

        var chapters = new List<(PlaybackChapterDto Chapter, Guid? AssetId, int SourceOrder)>(orderedChapters.Count);
        var priorIndex = int.MinValue;
        for (var i = 0; i < orderedChapters.Count; i++)
        {
            var chapter = orderedChapters[i];
            if (chapter.Index < 0 || chapter.Index <= priorIndex)
            {
                return false;
            }

            priorIndex = chapter.Index;
            if (chapter.AssetId == Guid.Empty)
            {
                return false;
            }

            chapters.Add((chapter, chapter.AssetId, i));
        }

        var potentiallyContaining = chapters
            .Where(entry => entry.AssetId == currentAssetId
                && double.IsFinite(entry.Chapter.StartSeconds)
                && entry.Chapter.StartSeconds >= 0
                && entry.Chapter.StartSeconds <= localPositionSeconds
                && (!double.IsFinite(entry.Chapter.EndSeconds ?? double.NaN)
                    || localPositionSeconds < entry.Chapter.EndSeconds!.Value))
            .ToArray();
        var containing = potentiallyContaining
            .Where(entry => IsTimedChapter(entry.Chapter)
                && localPositionSeconds < entry.Chapter.EndSeconds!.Value)
            .ToArray();
        if (containing.Length != 1 || potentiallyContaining.Length != containing.Length)
        {
            return false;
        }

        var origin = containing[0];
        var originAssetId = origin.AssetId!.Value;
        var targetChapter = origin.Chapter;
        var targetAssetId = originAssetId;
        if (kind == AudiobookSleepBoundaryKind.EndOfNextChapter)
        {
            if (origin.SourceOrder + 1 >= chapters.Count)
            {
                return false;
            }

            var successor = chapters[origin.SourceOrder + 1];
            if (successor.AssetId is not Guid successorAssetId
                || !authorizedAssetIds.Contains(successorAssetId)
                || !IsTimedChapter(successor.Chapter))
            {
                return false;
            }

            targetChapter = successor.Chapter;
            targetAssetId = successorAssetId;
        }
        else if (kind != AudiobookSleepBoundaryKind.EndOfChapter)
        {
            return false;
        }

        target = new AudiobookSleepBoundaryTarget(
            workId,
            sessionLeaseId,
            originAssetId,
            origin.Chapter.Index,
            localPositionSeconds,
            targetAssetId,
            targetChapter.Index,
            targetChapter.Title,
            targetChapter.EndSeconds!.Value,
            kind);
        return true;
    }

    private static bool IsTimedChapter(PlaybackChapterDto chapter) =>
        double.IsFinite(chapter.StartSeconds)
        && chapter.StartSeconds >= 0
        && chapter.EndSeconds is double end
        && double.IsFinite(end)
        && end > chapter.StartSeconds;
}
